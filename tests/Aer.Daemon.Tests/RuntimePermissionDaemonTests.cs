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

    [Fact]
    public async Task TurnEnd_RevokesPendingPermission_AndClearsPendingGateRegistry()
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
}
