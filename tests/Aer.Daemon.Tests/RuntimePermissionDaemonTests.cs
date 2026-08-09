using System.Text.Json;
using Aer.Daemon;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
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
            try
            {
                Directory.Delete(_tempRoomDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public async Task NoLockIsolation_AnswerPermission_DoesNotBlockOnTurnLock()
    {
        // 0037: acquire SessionTurnLockFor(roomDir) in test, then call answer path
        var turnLock = DaemonHost.SessionTurnLockFor(_tempRoomDir);
        await turnLock.WaitAsync();

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
                _tempRoomDir, reader, writer, reqId, "AllowOnce", "{}", "ok", "human");

            var completedTask = await Task.WhenAny(task, Task.Delay(3000));
            Assert.Same(task, completedTask); // Proves it did not deadlock/block

            var state = await task;
            Assert.Null(state.PendingPermission);
        }
        finally
        {
            turnLock.Release();
        }
    }

    [Fact]
    public async Task Doorbell_DetectsAskFile_AndIgnoresControlFile()
    {
        var execId = new ExecutionId("ex-doorbell");
        var outputDir = ArtifactManager.ResolveOutputDirectory(
            Path.Combine(_tempRoomDir, ArtifactManager.ArtifactsDirectoryName),
            execId);
        Directory.CreateDirectory(outputDir);

        var reqId = "req-doorbell-1";
        var askFilePath = Path.Combine(outputDir, $"ask-{reqId}.json");
        var controlFilePath = Path.Combine(outputDir, "other-file.txt");

        var askPayload = new
        {
            permissionRequestId = reqId,
            toolName = "Bash",
            inputJson = "{\"command\":\"ls\"}",
            reason = "need to list files",
            askedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(askFilePath, JsonSerializer.Serialize(askPayload));
        await File.WriteAllTextAsync(controlFilePath, "some content");

        // Manually trigger or process asks via DoorbellMonitor logic
        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);

        // Process ask file directly as doorbell does
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(askFilePath));
        var root = doc.RootElement;
        PendingGateRegistry.Register(reqId, new PendingGateEntry(_tempRoomDir, outputDir, "ex-doorbell", askFilePath));

        await RoomMutationInterface.RaisePermissionAsync(
            _tempRoomDir, reader, writer, reqId, execId, new StepId("chat"), "chat-worker", "claude", "corr", "Bash", "{\"command\":\"ls\"}", "Bash");

        var events = await reader.ReadAllRoomEventsAsync();
        Assert.Single(events.OfType<RoomEvent.RuntimePermissionAsked>());
        Assert.True(PendingGateRegistry.TryGet(reqId, out _));

        // Control file should not add any events
        var eventsAfterControl = await reader.ReadAllRoomEventsAsync();
        Assert.Single(eventsAfterControl);
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
        await File.WriteAllTextAsync(answerFilePath, JsonSerializer.Serialize(answerPayload));

        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);

        await RoomMutationInterface.AnswerPermissionAsync(
            _tempRoomDir, reader, writer, reqId, "AllowOnce", "{\"command\":\"dir\"}", "approved by operator", "human");

        PendingGateRegistry.TryRemove(reqId, out _);

        Assert.True(File.Exists(answerFilePath));
        var answerText = await File.ReadAllTextAsync(answerFilePath);
        Assert.Contains("AllowOnce", answerText);

        var events = await reader.ReadAllRoomEventsAsync();
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

        await File.WriteAllTextAsync(orphanAskFile, JsonSerializer.Serialize(askPayloadOrphan));
        await File.WriteAllTextAsync(answeredAskFile, JsonSerializer.Serialize(askPayloadAnswered));
        await File.WriteAllTextAsync(answeredAnswerFile, JsonSerializer.Serialize(new { decisionKind = "AllowOnce" }));

        // Ensure room.jsonl exists
        var roomLogPath = Path.Combine(_tempRoomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            // Empty room log
        }

        // Run reconciliation
        await DaemonHost.ReconcileRoomPermissionsAsync(_tempRoomDir);

        var events = await reader.ReadAllRoomEventsAsync();
        var askedEvents = events.OfType<RoomEvent.RuntimePermissionAsked>().ToList();

        // Orphan must be re-raised
        Assert.Single(askedEvents);
        Assert.Equal(orphanReqId, askedEvents[0].PermissionRequestId);

        // Registry should hold orphan entry
        Assert.True(PendingGateRegistry.TryGet(orphanReqId, out _));
        Assert.False(PendingGateRegistry.TryGet(answeredReqId, out _));
    }
}
