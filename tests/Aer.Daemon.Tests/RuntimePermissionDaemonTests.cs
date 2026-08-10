using System.Text.Json;
using Aer.Daemon;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Tests.Shared;
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
}
