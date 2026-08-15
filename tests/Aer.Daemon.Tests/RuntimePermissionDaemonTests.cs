using System.Net.Http.Json;
using System.Text.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Daemon.Tests;

[Collection(PendingGateRegistryCollection.Name)]
public sealed class RuntimePermissionDaemonTests : IDisposable
{
    private readonly string _tempRoomDir;

    public RuntimePermissionDaemonTests()
    {
        _tempRoomDir = Path.Combine(Path.GetTempPath(), $"daemon-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoomDir);
        PendingGateRegistry.Clear();
    }

    public void Dispose()
    {
        PendingGateRegistry.Clear();
        if (Directory.Exists(_tempRoomDir))
        {
            DirectoryCleanup.DeleteRecursively(_tempRoomDir);
        }
    }

    [Fact]
    public async Task NoLockIsolation_AnswerPermission_DoesNotBlockOnTurnLock()
    {
        // 0037: acquire SessionTurnLockFor(roomDir) in test, then call answer path
        var turnLock = DaemonHost.SessionTurnLockFor(_tempRoomDir);
        await turnLock.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            var outputDir = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
                new ExecutionId("ex-nolock"));
            Directory.CreateDirectory(outputDir);

            var reqId = "req-nolock-1";
            PendingGateRegistry.Register(reqId, new PendingGateEntry(_tempRoomDir, outputDir, "ex-nolock", Path.Combine(outputDir, $"ask-{reqId}.json")));

            var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);

            // Must complete immediately without waiting for turnLock
            var task = RoomMutationInterface.AnswerPermissionAsync(
                _tempRoomDir, reader, writer, reqId, "AllowOnce", "{}", "ok", "human",
                cancellationToken: TestContext.Current.CancellationToken);

            // wait-ok: deadlock detector — the lock-free answer path finishes in ms; only a real
            // turn-lock deadlock fails within the budget (0037). 60s, not 3s: the answer path does
            // room.jsonl I/O that lost to a 3s budget under Windows CI load — a false deadlock (#1097).
            var completedTask = await Task.WhenAny(task, Task.Delay(60000, TestContext.Current.CancellationToken));
            Assert.Same(task, completedTask); // Proves it did not deadlock/block

            var state = await task;
            Assert.Null(state.PendingPermission);
        }
        finally
        {
            turnLock.Release();
        }
    }

    /// <summary>
    /// Drives a REAL <see cref="DoorbellMonitor"/> — its watcher, its backup poll, its
    /// <c>ProcessAskFileAsync</c> and its dedup — against a real room directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The version this replaces never constructed one.</b> It hand-rolled the doorbell's logic
    /// inline (parse the file, register, call <c>RaisePermissionAsync</c>) and asserted on the result,
    /// so it passed with <c>DoorbellMonitor.cs</c> deleted: what it measured was that
    /// <c>RaisePermissionAsync</c> appends an event, which is Phase 1's claim, not the doorbell's. Its
    /// "control" was the same reader read twice with nothing in between.
    /// </para>
    /// <para>
    /// The control here is a real one — a file the monitor must ignore, dropped into the same directory
    /// it is watching, with the same bounded wait spent on it. That the wait is spent matters: a
    /// control that returns immediately proves only that nothing happened yet.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Doorbell_DetectsAskFile_AndIgnoresControlFile()
    {
        var execId = new ExecutionId("ex-doorbell");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        // Counts how far ProcessAskFileAsync actually got: the load is the LAST thing it does, after
        // registering and appending, so a non-zero count is the only evidence here that the whole
        // method ran rather than throwing partway and being logged.
        var loads = 0;
        await using var monitor = new DoorbellMonitor(
            _tempRoomDir,
            "claude",
            "vendor-session-1",
            // Narrowed from RoomClient precisely so this line can exist; see the ctor's own docs.
            _ =>
            {
                Interlocked.Increment(ref loads);
                return Task.FromResult<RoomProjection?>(null);
            },
            (_, _) => Task.CompletedTask);

        // THE CONTROL, first and on its own: a non-ask file in the watched directory. Asserted after a
        // wait long enough for both the watcher and at least one backup-poll tick (1.5s) to have seen
        // it, so "nothing appended" is a decision the monitor made rather than one it has not reached.
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "other-file.txt"), "some content", TestContext.Current.CancellationToken);
        // wait-ok: negative assertion — bounded wait past the 1.5s backup-poll tick so "nothing appended" is a decision, not a not-yet
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        Assert.Empty(await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref loads));

        var reqId = "req-doorbell-1";
        var askPayload = new
        {
            permissionRequestId = reqId,
            toolName = "Bash",
            inputJson = "{\"command\":\"ls\"}",
            reason = "need to list files",
            askedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"ask-{reqId}.json"),
            JsonSerializer.Serialize(askPayload),
            TestContext.Current.CancellationToken);

        var asked = await WaitForAskAsync(reader, TimeSpan.FromSeconds(10));

        Assert.NotNull(asked);
        Assert.Equal(reqId, asked!.PermissionRequestId);
        Assert.Equal("Bash", asked.ToolName);
        // The execution id is recovered from the `execution_<id>` directory the ask file sits in --
        // nothing in the payload carries it.
        Assert.Equal(execId.Value, asked.ExecutionId.Value);

        // The registry entry is what the answer path later resolves the rendezvous file from; an event
        // raised without it strands the worker even though the human saw the ask.
        Assert.True(PendingGateRegistry.TryGet(reqId, out _));

        // And exactly one, after the watcher AND the poll have both had the file: the dedup is what
        // keeps a 1.5s poll from re-raising the same ask every tick.
        // wait-ok: bounded wait past a second 1.5s poll tick to prove dedup held rather than the poll simply not having re-run
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        Assert.Single(events.OfType<RoomEvent.RuntimePermissionAsked>());
        Assert.Equal(1, Volatile.Read(ref loads));
    }

    /// <summary>Polls <paramref name="reader"/> for the first appended ask, or null if none arrives.</summary>
    private static async Task<RoomEvent.RuntimePermissionAsked?> WaitForAskAsync(
        RoomEventLogReader reader, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            if (events.OfType<RoomEvent.RuntimePermissionAsked>().FirstOrDefault() is { } asked)
            {
                return asked;
            }

            // wait-ok: poll interval inside the bounded WaitForAskAsync — the 10s timeout is the real wait, this is just recheck cadence
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return null;
    }

    [Fact]
    public async Task AnswerRoundTrip_WritesFile_AppendsEvent_RemovesRegistryEntry()
    {
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            new ExecutionId("ex-roundtrip"));
        Directory.CreateDirectory(outputDir);

        var reqId = "req-roundtrip-1";
        PendingGateRegistry.Register(reqId, new PendingGateEntry(_tempRoomDir, outputDir, "ex-roundtrip", Path.Combine(outputDir, $"ask-{reqId}.json")));

        var answerFilePath = Path.Combine(outputDir, $"answer-{reqId}.json");
        var answerPayload = new
        {
            decisionKind = "AllowOnce",
            updatedInputJson = "{\"command\":\"dir\"}",
            reason = "approved by operator"
        };
        await File.WriteAllTextAsync(answerFilePath, JsonSerializer.Serialize(answerPayload), TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);

        await RoomMutationInterface.AnswerPermissionAsync(
            _tempRoomDir, reader, writer, reqId, "AllowOnce", "{\"command\":\"dir\"}", "approved by operator", "human",
            cancellationToken: TestContext.Current.CancellationToken);

        PendingGateRegistry.TryRemove(reqId, out _);

        Assert.True(File.Exists(answerFilePath));
        var answerText = await File.ReadAllTextAsync(answerFilePath, TestContext.Current.CancellationToken);
        Assert.Contains("AllowOnce", answerText);

        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var answered = Assert.Single(events.OfType<RoomEvent.RuntimePermissionAnswered>());
        Assert.Equal(reqId, answered.PermissionRequestId);

        Assert.False(PendingGateRegistry.TryGet(reqId, out _));
    }

    [Fact]
    public async Task Reconciliation_ReRaisesOrphanAsk_AndIgnoresAnsweredAsk()
    {
        var artifactsDir = Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName);
        var outputDirOrphan = Path.Combine(artifactsDir, "execution_ex-orphan");
        var outputDirAnswered = Path.Combine(artifactsDir, "execution_ex-answered");
        Directory.CreateDirectory(outputDirOrphan);
        Directory.CreateDirectory(outputDirAnswered);

        var orphanReqId = "req-orphan-1";
        var answeredReqId = "req-answered-1";

        var orphanAskFile = Path.Combine(outputDirOrphan, $"ask-{orphanReqId}.json");
        var answeredAskFile = Path.Combine(outputDirAnswered, $"ask-{answeredReqId}.json");
        var answeredAnswerFile = Path.Combine(outputDirAnswered, $"answer-{answeredReqId}.json");

        var askPayloadOrphan = new { permissionRequestId = orphanReqId, toolName = "Edit", inputJson = "{}", askedAt = DateTimeOffset.UtcNow };
        var askPayloadAnswered = new { permissionRequestId = answeredReqId, toolName = "Edit", inputJson = "{}", askedAt = DateTimeOffset.UtcNow };

        await File.WriteAllTextAsync(orphanAskFile, JsonSerializer.Serialize(askPayloadOrphan), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(answeredAskFile, JsonSerializer.Serialize(askPayloadAnswered), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(answeredAnswerFile, JsonSerializer.Serialize(new { decisionKind = "AllowOnce" }), TestContext.Current.CancellationToken);

        // Ensure room.jsonl exists
        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            // Empty room log
        }

        // Run reconciliation
        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir, TestContext.Current.CancellationToken);

        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var askedEvents = events.OfType<RoomEvent.RuntimePermissionAsked>().ToList();

        // Orphan must be re-raised
        Assert.Single(askedEvents);
        Assert.Equal(orphanReqId, askedEvents[0].PermissionRequestId);

        // Registry should hold orphan entry
        Assert.True(PendingGateRegistry.TryGet(orphanReqId, out _));
        Assert.False(PendingGateRegistry.TryGet(answeredReqId, out _));
    }

    // Exercises the helper directly, not the turn path: ExecuteSessionTurnAsync (which calls this
    // with reason "turn_ended" from its finally, before the turn-lock release) is private and
    // spawns real vendor processes, so the wiring itself is verifiable only by a live drive.
    [Fact]
    public async Task RevokePendingGatesForRoom_RevokesPendingPermission_AndClearsPendingGateRegistry()
    {
        var execId = new ExecutionId("ex-turnend");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var reqId = "req-turnend-1";
        PendingGateRegistry.Register(reqId, new PendingGateEntry(_tempRoomDir, outputDir, execId.Value, Path.Combine(outputDir, $"ask-{reqId}.json")));

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        await DaemonHost.RevokePendingGatesForRoomAsync(_tempRoomDir, executionIdFilter: null, reason: "turn_ended");

        Assert.False(PendingGateRegistry.TryGet(reqId, out _));

        var revokedPath = Path.Combine(outputDir, $"revoked-{reqId}.json");
        Assert.True(File.Exists(revokedPath));
        var revokedText = await File.ReadAllTextAsync(revokedPath, TestContext.Current.CancellationToken);
        Assert.Contains("turn_ended", revokedText);

        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var revokedEvent = Assert.Single(events.OfType<RoomEvent.RuntimePermissionRevoked>());
        Assert.Equal(reqId, revokedEvent.PermissionRequestId);
        Assert.Equal("turn_ended", revokedEvent.Reason);
    }

    [Fact]
    public async Task Reconciliation_ReRaisedYoungAsk_ExpiresAtItsOwnDeadline()
    {
        // #1113: a re-raised young ask's worker died with the daemon, so nothing else is left to
        // enforce its recorded timeout — reconcile itself must schedule the expiry.
        var artifactsDir = Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName);
        var outputDir = Path.Combine(artifactsDir, "execution_ex-young");
        Directory.CreateDirectory(outputDir);

        var reqId = "req-young-1";
        var askPayload = new { permissionRequestId = reqId, toolName = "Edit", inputJson = "{}", askedAt = DateTimeOffset.UtcNow, timeoutSeconds = 2 };
        await File.WriteAllTextAsync(Path.Combine(outputDir, $"ask-{reqId}.json"), JsonSerializer.Serialize(askPayload), TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir, TestContext.Current.CancellationToken);

        // Re-raised first: still inside its 2s window.
        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        Assert.Single(events.OfType<RoomEvent.RuntimePermissionAsked>());
        Assert.Empty(events.OfType<RoomEvent.RuntimePermissionRevoked>());
        Assert.True(PendingGateRegistry.TryGet(reqId, out _));

        // Then expired at its own deadline, with the full resolution (journal + sentinel + registry).
        var deadline = DateTime.UtcNow.AddSeconds(15);
        RoomEvent.RuntimePermissionRevoked? revoked = null;
        while (DateTime.UtcNow < deadline)
        {
            events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            revoked = events.OfType<RoomEvent.RuntimePermissionRevoked>().FirstOrDefault();
            if (revoked != null && !PendingGateRegistry.TryGet(reqId, out _))
            {
                break;
            }
            await Task.Delay(100, TestContext.Current.CancellationToken); // wait-ok: bounded poll for the scheduled expiry at the ask's 2s deadline
        }

        Assert.NotNull(revoked);
        Assert.Equal(reqId, revoked.PermissionRequestId);
        Assert.Equal("timeout", revoked.Reason);
        Assert.False(PendingGateRegistry.TryGet(reqId, out _));
        Assert.True(File.Exists(Path.Combine(outputDir, $"revoked-{reqId}.json")));
    }

    [Fact]
    public async Task Reconciliation_ReRaisedAsk_ResolvedBeforeDeadline_IsNotExpired()
    {
        // Polarity arm for the scheduled expiry: anything that resolves the ask first (here, the
        // answer path's registry removal + journaled Answered) makes the delayed sweep a no-op.
        var artifactsDir = Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName);
        var outputDir = Path.Combine(artifactsDir, "execution_ex-young2");
        Directory.CreateDirectory(outputDir);

        var reqId = "req-young-2";
        var askPayload = new { permissionRequestId = reqId, toolName = "Edit", inputJson = "{}", askedAt = DateTimeOffset.UtcNow, timeoutSeconds = 2 };
        await File.WriteAllTextAsync(Path.Combine(outputDir, $"ask-{reqId}.json"), JsonSerializer.Serialize(askPayload), TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir, TestContext.Current.CancellationToken);
        Assert.True(PendingGateRegistry.TryGet(reqId, out _));

        // Resolve like the answer endpoint does: journal Answered, then clear the registry entry.
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            await RoomMutationInterface.AnswerPermissionAsync(
                _tempRoomDir, reader, writer, reqId, "AllowOnce", updatedInputJson: null,
                reason: null, deciderIdentity: "human",
                cancellationToken: TestContext.Current.CancellationToken);
        }
        PendingGateRegistry.TryRemove(reqId, out _);

        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken); // wait-ok: spans the ask's 2s deadline to prove the scheduled expiry did not fire

        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(events.OfType<RoomEvent.RuntimePermissionRevoked>());
        Assert.Single(events.OfType<RoomEvent.RuntimePermissionAnswered>());
    }

    [Fact]
    public async Task Reconciliation_ExpiredAsk_EmitsRevoked_AndDoesNotReRaise()
    {
        var artifactsDir = Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName);
        var outputDirExpired = Path.Combine(artifactsDir, "execution_ex-expired");
        Directory.CreateDirectory(outputDirExpired);

        var expiredReqId = "req-expired-1";
        var expiredAskFile = Path.Combine(outputDirExpired, $"ask-{expiredReqId}.json");
        var expiredAskedAt = DateTimeOffset.UtcNow.AddMinutes(-5); // 5 minutes old (> 180s timeout)

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var askPayloadExpired = new
        {
            permissionRequestId = expiredReqId,
            toolName = "Edit",
            inputJson = "{}",
            askedAt = expiredAskedAt
        };
        await File.WriteAllTextAsync(expiredAskFile, JsonSerializer.Serialize(askPayloadExpired, jsonOptions), TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir, TestContext.Current.CancellationToken);

        var revokedFile = Path.Combine(outputDirExpired, $"revoked-{expiredReqId}.json");
        Assert.True(File.Exists(revokedFile));
        var revokedText = await File.ReadAllTextAsync(revokedFile, TestContext.Current.CancellationToken);
        Assert.Contains("expired_during_shutdown", revokedText);

        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(events.OfType<RoomEvent.RuntimePermissionAsked>());
        var revokedEvent = Assert.Single(events.OfType<RoomEvent.RuntimePermissionRevoked>());
        Assert.Equal(expiredReqId, revokedEvent.PermissionRequestId);
        Assert.Equal("expired_during_shutdown", revokedEvent.Reason);

        Assert.False(PendingGateRegistry.TryGet(expiredReqId, out _));
    }

    [Fact]
    public async Task AnswerPermission_LateAnswerAfterTimeout_Returns409Conflict_AndDoesNotAmendGrant()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var bindingsPath = Path.Combine(_tempRoomDir, "bindings.json");
            var initialBindingsJson = """{"chat-worker":{"adapter":"claude","contract":{"inputs":[],"outputs":[]},"promptTemplate":"test","timeout":"00:10:00","model":"claude-3-5-sonnet","permissionGrant":{"level":"AllowList","allowedTools":["Bash"]}}}""";
            await File.WriteAllTextAsync(bindingsPath, initialBindingsJson, TestContext.Current.CancellationToken);

            var artifactsDir = Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName);
            var outputDir = Path.Combine(artifactsDir, "execution_ex-late");
            Directory.CreateDirectory(outputDir);

            var reqId = "req-late-1";
            var askFile = Path.Combine(outputDir, $"ask-{reqId}.json");
            var revokedFile = Path.Combine(outputDir, $"revoked-{reqId}.json");

            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var askPayload = new { permissionRequestId = reqId, toolName = "Edit", inputJson = "{}" };
            var revokedPayload = new { permissionRequestId = reqId, reason = "timeout" };

            await File.WriteAllTextAsync(askFile, JsonSerializer.Serialize(askPayload, jsonOptions), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(revokedFile, JsonSerializer.Serialize(revokedPayload, jsonOptions), TestContext.Current.CancellationToken);

            var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await RoomMutationInterface.RaisePermissionAsync(
                    _tempRoomDir, reader, writer, reqId, new ExecutionId("ex-late"), new StepId("st-1"),
                    "chat-worker", "claude", "corr-1", "Edit", "{}", "Edit", cancellationToken: TestContext.Current.CancellationToken);
            }

            var answerRequestPayload = new
            {
                directoryPath = _tempRoomDir,
                permissionRequestId = reqId,
                decisionKind = "AllowOnce"
            };

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/rooms/permissions/answer", answerRequestPayload, TestContext.Current.CancellationToken);

            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);

            var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Revoked", responseText);
            Assert.Contains(reqId, responseText);

            var currentBindingsJson = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
            Assert.Equal(initialBindingsJson, currentBindingsJson);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            try
            {
                await daemonTask;
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Drives a REAL <see cref="DoorbellMonitor"/> against a <c>revoked-*.json</c> sentinel — the
    /// pickup path the second-reader found had zero coverage in either watcher or poll mode.
    /// </summary>
    [Fact]
    public async Task Doorbell_DetectsRevokedFile_JournalsRevoke_AndClearsRegistry()
    {
        var execId = new ExecutionId("ex-doorbell-rev");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        var reqId = "req-doorbell-rev-1";
        PendingGateRegistry.Register(reqId, new PendingGateEntry(
            _tempRoomDir, outputDir, execId.Value, Path.Combine(outputDir, $"ask-{reqId}.json")));

        var broadcasts = 0;
        await using var monitor = new DoorbellMonitor(
            _tempRoomDir,
            "claude",
            "vendor-session-1",
            _ => Task.FromResult<RoomProjection?>(null),
            (_, _) => { Interlocked.Increment(ref broadcasts); return Task.CompletedTask; });

        var revokedPayload = new { permissionRequestId = reqId, reason = "timeout" };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"revoked-{reqId}.json"),
            JsonSerializer.Serialize(revokedPayload),
            TestContext.Current.CancellationToken);

        // Poll until BOTH the journal event exists AND the registry entry is gone: the monitor
        // journals first and removes second (#1102's invariant), so asserting the registry at
        // first sight of the event races the monitor's own next statement (#1106, caught on CI).
        RoomEvent.RuntimePermissionRevoked? revoked = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline
            && (revoked is null || PendingGateRegistry.TryGet(reqId, out _)))
        {
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            revoked = events.OfType<RoomEvent.RuntimePermissionRevoked>().FirstOrDefault();
            // wait-ok: bounded poll for the monitor's pickup, journal write, and removal to land
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(revoked);
        Assert.Equal(reqId, revoked!.PermissionRequestId);
        Assert.Equal("timeout", revoked.Reason);
        Assert.False(PendingGateRegistry.TryGet(reqId, out _));
    }

    /// <summary>
    /// A revoked sentinel with no matching journal event must be HEALED by restart reconciliation,
    /// not skipped — the pre-fix skip made a tool-timeout-while-daemon-down invisible in
    /// <c>room.jsonl</c> forever (second-reader finding on #1098).
    /// </summary>
    [Fact]
    public async Task Reconciliation_OrphanedRevokedSentinel_IsJournaled_NotSkipped()
    {
        var execId = new ExecutionId("ex-orphan-rev");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var reqId = "req-orphan-rev-1";
        var askPayload = new
        {
            permissionRequestId = reqId,
            toolName = "Bash",
            inputJson = "{\"command\":\"ls\"}",
            reason = "list",
            askedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"ask-{reqId}.json"),
            JsonSerializer.Serialize(askPayload),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"revoked-{reqId}.json"),
            JsonSerializer.Serialize(new { permissionRequestId = reqId, reason = "timeout" }),
            TestContext.Current.CancellationToken);

        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir, TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var revoked = Assert.Single(events.OfType<RoomEvent.RuntimePermissionRevoked>());
        Assert.Equal(reqId, revoked.PermissionRequestId);
        Assert.Equal("timeout", revoked.Reason);
        Assert.False(PendingGateRegistry.TryGet(reqId, out _));
    }

    /// <summary>
    /// The crash window between the answer endpoint's journal-first write and its answer-file
    /// write leaves a journaled Answered with no file — the worker never released. Reconciliation
    /// must re-materialize the file from the event (second-reader Finding 1 on the reorder).
    /// </summary>
    [Fact]
    public async Task Reconciliation_JournaledAnswerWithoutAnswerFile_RematerializesTheAnswerFile()
    {
        var execId = new ExecutionId("ex-heal-ans");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var reqId = "req-heal-ans-1";
        var askPayload = new
        {
            permissionRequestId = reqId,
            toolName = "Bash",
            inputJson = "{\"command\":\"ls\"}",
            reason = "list",
            askedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"ask-{reqId}.json"),
            JsonSerializer.Serialize(askPayload),
            TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            await RoomMutationInterface.AnswerPermissionAsync(
                _tempRoomDir, reader, writer, reqId, "AllowOnce", "{\"command\":\"ls\"}", "ok", "human",
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var answerFilePath = Path.Combine(outputDir, $"answer-{reqId}.json");
        Assert.False(File.Exists(answerFilePath));

        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(answerFilePath));
        using var answerDoc = JsonDocument.Parse(
            await File.ReadAllTextAsync(answerFilePath, TestContext.Current.CancellationToken));
        Assert.Equal("AllowOnce", answerDoc.RootElement.GetProperty("decisionKind").GetString());
        Assert.False(PendingGateRegistry.TryGet(reqId, out _));
    }

    /// <summary>
    /// A failed revoke-journal write must NOT strip the registry entry — the doorbell keeps the
    /// entry, clears its dedup, and the 1.5s backup poll retries until the journal write lands
    /// (second-reader Finding 2: the pre-fix order removed the entry before journaling).
    /// </summary>
    [Fact]
    public async Task Doorbell_RevokeJournalFailure_KeepsRegistryEntry_ThenRetriesViaPoll()
    {
        var execId = new ExecutionId("ex-doorbell-retry");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var reqId = "req-doorbell-retry-1";
        PendingGateRegistry.Register(reqId, new PendingGateEntry(
            _tempRoomDir, outputDir, execId.Value, Path.Combine(outputDir, $"ask-{reqId}.json")));

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);

        // Hold the room guard so the monitor's journal write fails with WorkflowLockedException.
        var guard = Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(_tempRoomDir);

        await using var monitor = new DoorbellMonitor(
            _tempRoomDir,
            "claude",
            "vendor-session-1",
            _ => Task.FromResult<RoomProjection?>(null),
            (_, _) => Task.CompletedTask);

        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"revoked-{reqId}.json"),
            JsonSerializer.Serialize(new { permissionRequestId = reqId, reason = "timeout" }),
            TestContext.Current.CancellationToken);

        // Bounded wait past a 1.5s poll tick so at least one processing attempt has FAILED against
        // the held guard before the polarity assertion below.
        // wait-ok: negative-arm window sized to the monitor's own 1.5s backup-poll tick
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        Assert.Empty(await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
        // THE DISCRIMINATOR: pre-fix, the entry was removed before the (failed) journal write.
        Assert.True(PendingGateRegistry.TryGet(reqId, out _));

        guard.Dispose();

        // Same both-conditions poll as the pickup test above (#1106): the entry disappears only
        // AFTER the retried journal write lands, so first-sight-of-the-event is too early.
        RoomEvent.RuntimePermissionRevoked? revoked = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline
            && (revoked is null || PendingGateRegistry.TryGet(reqId, out _)))
        {
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            revoked = events.OfType<RoomEvent.RuntimePermissionRevoked>().FirstOrDefault();
            // wait-ok: bounded poll for the monitor's backup tick to retry after guard release
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(revoked);
        Assert.Equal(reqId, revoked!.PermissionRequestId);
        Assert.False(PendingGateRegistry.TryGet(reqId, out _));
    }

    /// <summary>
    /// The journal write must survive losing the fail-fast room guard to a short-lived holder
    /// (RoomWakeBridge's sweep, #857's shape). Pre-fix, the first <see
    /// cref="Aer.Flow.Concurrency.WorkflowLockedException"/> propagated immediately.
    /// </summary>
    [Fact]
    public async Task RetryOnRoomLock_JournalWrite_SurvivesTransientGuardContention()
    {
        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");

        var guard = Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(_tempRoomDir);
        var releaseTask = Task.Run(async () =>
        {
            // Holds the guard across the retry loop's first attempt(s), releasing well inside its
            // bounded backoff so success can only come from a retry, not first-try luck.
            // wait-ok: transient-contention window, far under the retry loop's own ceiling
            await Task.Delay(350, TestContext.Current.CancellationToken);
            guard.Dispose();
        }, TestContext.Current.CancellationToken);

        await DaemonHost.RetryOnRoomLockAsync(async () =>
        {
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);
            await RoomMutationInterface.RevokePermissionAsync(
                _tempRoomDir, reader, writer, "req-retry-1", "turn_ended",
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(false);
        });

        await releaseTask;

        var reader2 = new RoomEventLogReader(roomLogPath);
        var events = await reader2.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var revoked = Assert.Single(events.OfType<RoomEvent.RuntimePermissionRevoked>());
        Assert.Equal("req-retry-1", revoked.PermissionRequestId);
    }

    [Fact]
    public async Task AnswerPermission_SucceedsWhileFlowLockHeld()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var bindingsPath = Path.Combine(_tempRoomDir, "bindings.json");
            var seedEntry = new WorkerBindingConfigEntry(
                Adapter: "claude",
                Contract: InteractiveSessionMaterializer.ChatWorkerContract,
                PromptTemplate: "test",
                Timeout: TimeSpan.FromMinutes(10),
                PermissionGrant: new PermissionGrant(ReadFiles: true));
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry> { [InteractiveSessionMaterializer.DefaultWorkerName] = seedEntry },
                bindingsPath, TestContext.Current.CancellationToken);

            var artifactsDir = Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName);
            var outputDir = Path.Combine(artifactsDir, "execution_ex-holdflow");
            Directory.CreateDirectory(outputDir);

            var reqId = "req-holdflow-1";
            var askFile = Path.Combine(outputDir, $"ask-{reqId}.json");
            var askPayload = new
            {
                permissionRequestId = reqId,
                toolName = "Bash",
                toolInputJson = "{\"command\":\"ls\"}",
                category = "shell",
                askedAt = DateTimeOffset.UtcNow
            };
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            await File.WriteAllTextAsync(askFile, JsonSerializer.Serialize(askPayload, jsonOptions), TestContext.Current.CancellationToken);

            PendingGateRegistry.Register(reqId, new PendingGateEntry(_tempRoomDir, outputDir, "ex-holdflow", askFile));

            var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await RoomMutationInterface.RaisePermissionAsync(
                    _tempRoomDir, reader, writer, reqId, new ExecutionId("ex-holdflow"), new StepId("step-1"),
                    "chat-worker", "claude", "corr-1", "Bash", "{\"command\":\"ls\"}", "shell",
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            // Hold the FLOW lock during answer request
            using var flowGuard = Aer.Flow.Concurrency.ConcurrencyGuard.Acquire(_tempRoomDir, "testing flow lock hold during answer");

            var answerBody = new
            {
                directoryPath = _tempRoomDir,
                permissionRequestId = reqId,
                decisionKind = "AllowRoom",
                updatedInputJson = (string?)null,
                reason = "operator approved"
            };

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/rooms/permissions/answer", answerBody, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var answered = Assert.Single(events.OfType<RoomEvent.RuntimePermissionAnswered>());
            Assert.Equal(reqId, answered.PermissionRequestId);

            // Grant amender must have run successfully and updated bindings.json (AllowRoom -> RunShellCommands: true)
            var updatedBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
            Assert.Contains("RunShellCommands", updatedBindingsText);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
    [Fact]
    public async Task SetMode_WhileRoomEventsLockHeld_Returns503_AndBindingsUnchanged()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var sessionId = "sess-mode-lock-" + Guid.NewGuid().ToString("N");
            var roomDir = InteractiveSessionMaterializer.ResolveRoomDirectoryPath(sessionId, null, null);
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId, roomDir, "claude", null, _tempRoomDir, null, 100, InteractiveSessionMaterializer.GrantForMode("interactive"), TestContext.Current.CancellationToken);

            var bindingsPath = Path.Combine(roomDir, "bindings.json");
            var initialBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);

            try
            {
                // Hold room-events lock
                using var roomEventsGuard = Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test mode lock hold");

                var response = await client.PostAsJsonAsync($"{baseUrl}/api/sessions/{sessionId}/mode", new { mode = "auto" }, TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);

                var afterBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
                Assert.Equal(initialBindingsText, afterBindingsText);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SendTurn_WhileRoomEventsLockHeld_FailsBeforeRewritingBindings_AndNamesTheLock()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var sessionId = "sess-turn-lock-" + Guid.NewGuid().ToString("N");
            var roomDir = InteractiveSessionMaterializer.ResolveRoomDirectoryPath(sessionId, null, null);
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId, roomDir, "claude", null, _tempRoomDir, null, 100, InteractiveSessionMaterializer.GrantForMode("interactive"), TestContext.Current.CancellationToken);

            var bindingsPath = Path.Combine(roomDir, "bindings.json");
            var initialBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);

            try
            {
                // #1110: the per-turn bindings rewrite in ExecuteSessionTurnCoreAsync must refuse
                // (bounded acquire, then WorkflowLockedException) rather than rewrite bindings.json
                // while another room-events holder is live. The send endpoint is fire-and-forget
                // (200 up front, #341), so the failure surfaces in .aer/turn-errors.log — and since
                // 0053 the lock message names the contended lock file, which is what pins this
                // failure to the room-events lock rather than any other turn error.
                using var roomEventsGuard = Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test turn lock hold");

                var response = await client.PostAsJsonAsync($"{baseUrl}/api/sessions/send",
                    new { sessionId, message = "hello" }, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

                var errorLogPath = Path.Combine(roomDir, ".aer", "turn-errors.log");
                var errorDeadline = DateTime.UtcNow.AddSeconds(20);
                string errorText = "";
                while (DateTime.UtcNow < errorDeadline)
                {
                    if (File.Exists(errorLogPath))
                    {
                        // #1120: the daemon's AppendTurnErrorAsync can be mid-append when this poll
                        // reads — on Windows that is a sharing violation, not a failure. A locked
                        // file just means "try again next tick" inside the same bounded deadline.
                        try
                        {
                            errorText = await File.ReadAllTextAsync(errorLogPath, TestContext.Current.CancellationToken);
                        }
                        catch (IOException)
                        {
                            errorText = "";
                        }
                        if (errorText.Contains(Aer.Flow.Concurrency.ConcurrencyGuard.RoomEventsLockFileName))
                        {
                            break;
                        }
                    }
                    await Task.Delay(100, TestContext.Current.CancellationToken); // wait-ok: bounded poll for the fire-and-forget turn's persisted error
                }

                Assert.Contains(Aer.Flow.Concurrency.ConcurrencyGuard.RoomEventsLockFileName, errorText);

                var afterBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
                Assert.Equal(initialBindingsText, afterBindingsText);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1238: the endpoint that gives a standing permission back — see <c>RevokeAsync</c> for what
    /// answering a persisting rung used to leave a person stuck with.
    /// </summary>
    /// <remarks>
    /// Deliberately drives the round trip through HTTP rather than calling the primitive: what the
    /// primitive's own tests cannot see is whether the endpoint reaches it at all, with the right
    /// worker, under the room-events guard — the same seam #1240's reviewer found unwired on a
    /// sibling endpoint. The three arms are one call each: the withdrawal, the second withdrawal that
    /// must not read as an error, and the refusal of a kind that does not exist.
    /// </remarks>
    [Fact]
    public async Task RevokePermission_TakesTheStandingShellPermissionBack_AndSaysWhichHappened()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-revoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var revoked = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Contains(
                    nameof(PermissionRevokeOutcome.Revoked),
                    await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                // The file, not just the response: the shell is genuinely gone from the binding the
                // next turn reads.
                var afterGrant = (await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken))
                    [InteractiveSessionMaterializer.DefaultWorkerName].PermissionGrant;
                Assert.False(afterGrant!.RunShellCommands);

                // Again — still 200, and it says nothing was left to take back rather than reporting a
                // withdrawal that did not happen.
                var again = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.True(again.IsSuccessStatusCode);
                Assert.Contains(
                    nameof(PermissionRevokeOutcome.NothingToRevoke),
                    await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                // And a kind that does not exist is refused by name, not quietly treated as a nearby
                // one — DenyAlways in particular, since lifting a standing refusal is exactly what
                // revocation must never become.
                var unknown = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionDecisionKind.DenyAlways },
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.BadRequest, unknown.StatusCode);
                Assert.Contains(
                    PermissionRevokeKind.RoomShell,
                    await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1238's second reader: the sibling writers of a live room's <c>bindings.json</c> each have a
    /// test that holds the room-events lock and pins the refusal; this endpoint did not.
    /// </summary>
    /// <remarks>
    /// The failure it exists to catch is the specific one the endpoint's own comment names: a change
    /// that swallowed the lost guard and answered 200 would tell an operator a permission is withdrawn
    /// while it is still in force — the one thing a revocation must never say. So both halves are
    /// asserted: the status, and that the file is byte-identical afterwards.
    /// </remarks>
    [Fact]
    public async Task RevokePermission_WhileRoomEventsLockHeld_Returns503_AndBindingsUnchanged()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-revoke-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var initialBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);

                using (Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test revoke lock hold"))
                {
                    var response = await client.PostAsJsonAsync(
                        $"{baseUrl}/api/rooms/permissions/revoke",
                        new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                        TestContext.Current.CancellationToken);

                    Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);

                    var afterBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
                    Assert.Equal(initialBindingsText, afterBindingsText);
                }

                // The control arm: with the lock released the identical call succeeds, so the 503
                // above is the lock and not something else about this room refusing every attempt.
                var afterRelease = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.True(afterRelease.IsSuccessStatusCode);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
