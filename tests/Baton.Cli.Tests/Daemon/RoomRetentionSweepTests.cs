using Baton.Cli.Daemon;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <remarks>
/// #1524: same <see cref="BatonEnvironmentSnapshot.BeginScope"/> isolation as
/// <c>Baton.Vendors.Tests.WorkerRoleCatalogTests</c>.
/// </remarks>
public class RoomRetentionSweepTests
{
    private static readonly StepId StepA = new("stepA");

    private static WorkflowDefinitionSnapshot SingleStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("single-step"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(StepA, "worker", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest TestRequest(ExecutionId execId) => new(
        execId,
        new WorkflowId("wf-1"),
        StepA,
        "worker",
        Inputs: [],
        Outputs: ["output.txt"],
        Timeout: TimeSpan.FromMinutes(1),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()
    );

    private static async Task WriteLogEventsAsync(string logPath, params FlowEvent[] events)
    {
        await using var writer = new FlowEventLogWriter(logPath);
        foreach (var @event in events)
        {
            await writer.AppendAsync(@event, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<string> CreateTerminalRoomWithArtifactsAsync(string parentDir, string roomName, ExecutionId execId)
    {
        var roomDir = Path.Combine(parentDir, roomName);
        Directory.CreateDirectory(roomDir);

        var snapshotPath = Path.Combine(roomDir, "snapshot.json");
        var logPath = Path.Combine(roomDir, "flow.jsonl");

        await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);
        await WriteLogEventsAsync(
            logPath,
            new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
            new FlowEvent.ExecutionSucceeded(execId));

        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
        await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "artifact-data", TestContext.Current.CancellationToken);

        return roomDir;
    }

    private static async Task<string> CreateRoomWithEventsAsync(string parentDir, string roomName, params RoomEvent[] events)
    {
        var roomDir = Path.Combine(parentDir, roomName);
        Directory.CreateDirectory(roomDir);

        var roomLogPath = Path.Combine(roomDir, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            foreach (var evt in events)
            {
                await writer.AppendAsync(evt, TestContext.Current.CancellationToken);
            }
        }

        return roomDir;
    }

    [Fact]
    public async Task PerRoomResilience_RoomFailure_DoesNotStopSweepFromCompactingNextRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Room 1: Corrupt room log that will throw on read/compaction
            var room1Dir = Path.Combine(tempRoot, "room-1-corrupt");
            Directory.CreateDirectory(room1Dir);
            await File.WriteAllTextAsync(Path.Combine(room1Dir, "room.jsonl"), "INVALID_JSON_CORRUPT_CONTENT\n", TestContext.Current.CancellationToken);

            // Room 2: Valid room with a resolved run that needs compaction
            var refCompleted = new HeldWorkRef("run-completed");
            var refLive = new HeldWorkRef("run-live");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
            var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

            var room2Dir = await CreateRoomWithEventsAsync(tempRoot, "room-2-valid", dispatchCompleted, resolveCompleted, dispatchLive);

            var sweep = new RoomRetentionSweep();

            // Run sweep with 0 byte threshold so size doesn't skip room 2
            var (compactedCount, _) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: 0,
                cancellationToken: TestContext.Current.CancellationToken);

            // Room 1 threw, Room 2 was compacted successfully
            Assert.Equal(1, compactedCount);

            var reader2 = new RoomEventLogReader(Path.Combine(room2Dir, "room.jsonl"));
            var room2Events = await reader2.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(room2Events);
            Assert.Equal(refLive, ((RoomEvent.HeldWorkDispatched)room2Events[0]).Ref);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteSingleSweepAsync_SkipsRoomsBelowThreshold()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_test_thresh_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var refCompleted = new HeldWorkRef("run-completed");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));

            var roomDir = await CreateRoomWithEventsAsync(tempRoot, "room-small", dispatchCompleted, resolveCompleted);

            var fileInfo = new FileInfo(Path.Combine(roomDir, "room.jsonl"));
            var fileSize = fileInfo.Length;

            var sweep = new RoomRetentionSweep();

            // Threshold set higher than file size -> should skip
            var (countSkipped, _) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: fileSize + 1000,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, countSkipped);

            // Threshold set lower than file size -> should compact
            var (countCompacted, _) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                thresholdBytesOverride: 0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, countCompacted);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void EnvironmentVariables_DefaultsAndOverrides()
    {
        Assert.False(RoomRetentionSweep.IsEnabled());
        Assert.Equal(RoomRetentionSweep.PlaceholderDefaultInterval, RoomRetentionSweep.GetInterval());
        Assert.Equal(RoomRetentionSweep.PlaceholderDefaultThresholdBytes, RoomRetentionSweep.GetThresholdBytes());
    }

    [Fact]
    public void GetInterval_ClampsPathologicalValue_InsteadOfOverflowing()
    {
        // Pins the clamp (RoomRetentionSweep.MaxInterval documents why it exists): a value whose
        // seconds would overflow TimeSpan.FromSeconds must collapse to MaxInterval, never throw.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionSweepIntervalSecondsOverride = "1e300" });

        var interval = RoomRetentionSweep.GetInterval();
        Assert.Equal(RoomRetentionSweep.MaxInterval, interval);
    }

    [Fact]
    public void GetInterval_LiftsSubSecondValue_ToMinInterval()
    {
        // Pins the lower clamp (RoomRetentionSweep.MinInterval documents the rationale): a value below
        // one second must lift to MinInterval rather than pass through near-zero.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionSweepIntervalSecondsOverride = "1e-9" });

        Assert.Equal(RoomRetentionSweep.MinInterval, RoomRetentionSweep.GetInterval());
    }

    [Fact]
    public async Task ExecuteSingleSweepAsync_PropagatesCancellation_InsteadOfSwallowingItAsAPerRoomError()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_test_cancel_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var refCompleted = new HeldWorkRef("run-completed");
            var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
            var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
            await CreateRoomWithEventsAsync(tempRoot, "room-cancel", dispatchCompleted, resolveCompleted);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var sweep = new RoomRetentionSweep();

            // A pre-cancelled token makes CompactAsync throw OperationCanceledException. The per-room catch
            // must rethrow it so shutdown unwinds the whole sweep — not log it as a compaction error and
            // march to the next room, which would swallow the cancellation. Without the rethrow clause this
            // returns a count instead of throwing.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                sweep.ExecuteSingleSweepAsync(
                    roomsDirectoryOverride: tempRoot,
                    thresholdBytesOverride: 0,
                    cancellationToken: cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task PruneRoomAsync_GraceWindow_PrunesOnlyWhenGraceElapsed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_grace_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var exec1 = new ExecutionId("exec-1");
            var room1Dir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "room-1-old", exec1);
            var flowLog1Path = Path.Combine(room1Dir, "flow.jsonl");
            File.SetLastWriteTimeUtc(flowLog1Path, DateTime.UtcNow.AddHours(-2));

            var exec2 = new ExecutionId("exec-2");
            var room2Dir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "room-2-new", exec2);
            var flowLog2Path = Path.Combine(room2Dir, "flow.jsonl");
            File.SetLastWriteTimeUtc(flowLog2Path, DateTime.UtcNow);

            var graceThreshold = TimeSpan.FromHours(1);
            var sweep = new RoomRetentionSweep();

            var (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                graceOverride: graceThreshold,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, prunedCount);

            var room1Artifacts = Path.Combine(room1Dir, ArtifactManager.ArtifactsDirectoryName);
            var room1PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(room1Artifacts, exec1);
            Assert.True(Directory.Exists(room1PrunedDir));

            var room2Artifacts = Path.Combine(room2Dir, ArtifactManager.ArtifactsDirectoryName);
            var room2PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(room2Artifacts, exec2);
            Assert.False(Directory.Exists(room2PrunedDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task PruneRoomAsync_KeepMarkedRoom_IsNotPruned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_keep_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var execId = new ExecutionId("exec-keep");
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "room-keep", execId);
            var flowLogPath = Path.Combine(roomDir, "flow.jsonl");
            File.SetLastWriteTimeUtc(flowLogPath, DateTime.UtcNow.AddHours(-2));

            await KeepMarker.MarkKeepAsync(roomDir, TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep();
            var (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                roomsDirectoryOverride: tempRoot,
                graceOverride: TimeSpan.Zero,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, prunedCount);

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var activeExecDir = ArtifactManager.ResolveOutputDirectory(artifactsRoot, execId);
            Assert.True(Directory.Exists(activeExecDir));

            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task PerRoomResilience_RoomPruneFailure_DoesNotStopSweepFromPruningNextRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_prune_resilience_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Room 1: a genuinely terminal, prunable room whose ConcurrencyGuard is held by someone else, so
            // ArtifactPruner.PruneAsync (which self-acquires that guard fail-fast) throws WorkflowLockedException.
            // A corrupt flow.jsonl would NOT exercise the catch: FlowEventLogReader swallows a malformed line,
            // leaving the room merely non-terminal so PruneAsync returns false without throwing. Lock contention
            // is both the realistic per-room prune failure and one that actually reaches the catch.
            var exec1 = new ExecutionId("exec-1");
            var room1Dir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "room-1-locked", exec1);
            File.SetLastWriteTimeUtc(Path.Combine(room1Dir, "flow.jsonl"), DateTime.UtcNow.AddHours(-2));

            // Room 2: an equally terminal, prunable room the sweep must still reach after room 1 throws.
            var exec2 = new ExecutionId("exec-2");
            var room2Dir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "room-2-valid", exec2);
            File.SetLastWriteTimeUtc(Path.Combine(room2Dir, "flow.jsonl"), DateTime.UtcNow.AddHours(-2));

            var sweep = new RoomRetentionSweep();

            int prunedCount;
            using (ConcurrencyGuard.Acquire(room1Dir, "test holds room-1 lock"))
            {
                (_, prunedCount) = await sweep.ExecuteSingleSweepAsync(
                    roomsDirectoryOverride: tempRoot,
                    graceOverride: TimeSpan.FromHours(1),
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            // Room 1 threw WorkflowLockedException (logged, skipped); room 2 was still pruned.
            Assert.Equal(1, prunedCount);

            var room1PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(
                Path.Combine(room1Dir, ArtifactManager.ArtifactsDirectoryName), exec1);
            Assert.False(Directory.Exists(room1PrunedDir));

            var room2PrunedDir = ArtifactManager.ResolvePrunedOutputDirectory(
                Path.Combine(room2Dir, ArtifactManager.ArtifactsDirectoryName), exec2);
            Assert.True(Directory.Exists(room2PrunedDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public void EnvironmentVariables_PruneDefaultsAndOverrides()
    {
        using (BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank))
        {
            Assert.False(RoomRetentionSweep.IsPruneEnabled());
            Assert.Equal(RoomRetentionSweep.PlaceholderDefaultPruneGrace, RoomRetentionSweep.GetPruneGrace());
        }

        using (BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneEnabledOverride = "true" }))
        {
            Assert.True(RoomRetentionSweep.IsPruneEnabled());
        }

        using (BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneEnabledOverride = "1" }))
        {
            Assert.True(RoomRetentionSweep.IsPruneEnabled());
        }

        using (BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneGraceSecondsOverride = "1800" }))
        {
            Assert.Equal(TimeSpan.FromSeconds(1800), RoomRetentionSweep.GetPruneGrace());
        }
    }

    [Fact]
    public void GetPruneGrace_ClampsPathologicalValue_ToMaxPruneGrace()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneGraceSecondsOverride = "1e300" });

        var grace = RoomRetentionSweep.GetPruneGrace();
        Assert.Equal(RoomRetentionSweep.MaxPruneGrace, grace);
    }

    [Fact]
    public void GetPruneGrace_LiftsSubSecondValue_ToMinPruneGrace()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RetentionPruneGraceSecondsOverride = "1e-9" });

        var grace = RoomRetentionSweep.GetPruneGrace();
        Assert.Equal(RoomRetentionSweep.MinPruneGrace, grace);
    }

    // #1659: the retention hook -- RoomRetentionSweep may call `baton rooms prune --terminal` behind
    // DaemonSettings.RoomsRetentionDays, default off.
    [Fact]
    public void ResolveRoomsRetentionDays_NoSettingsAndNoOverride_IsNull()
    {
        var sweep = new RoomRetentionSweep();
        Assert.Null(sweep.ResolveRoomsRetentionDays());
    }

    [Fact]
    public void ResolveRoomsRetentionDays_SettingsValue_IsUsedWhenNoOverride()
    {
        var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 5 });
        Assert.Equal(5, sweep.ResolveRoomsRetentionDays());
    }

    [Fact]
    public void ResolveRoomsRetentionDays_NonPositiveSettingsValue_IsTreatedAsOff()
    {
        var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 0 });
        Assert.Null(sweep.ResolveRoomsRetentionDays());
    }

    [Fact]
    public async Task ExecuteRoomsRetentionPruneAsync_NoRetentionConfigured_IsANoOp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-1"));
            await WriteRoomTerminalSentinelAsync(roomDir);
            var registryPath = Path.Combine(tempRoot, "room-registry.jsonl");
            await Baton.Vendors.RoomRegistryStore.AppendAsync(
                roomDir, tempRoot, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep(); // no DaemonSettings -> RoomsRetentionDays unset
            var deletedCount = await sweep.ExecuteRoomsRetentionPruneAsync(
                registryFilePathOverride: registryPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, deletedCount);
            Assert.True(Directory.Exists(roomDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteRoomsRetentionPruneAsync_ConfiguredRetentionDays_DeletesAnOldTerminalRoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "baton_sweep_retention_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var roomDir = await CreateTerminalRoomWithArtifactsAsync(tempRoot, "old-room", new ExecutionId("exec-1"));
            var terminalSentinelPath = await WriteRoomTerminalSentinelAsync(roomDir);
            // Backdate the sentinel so a 1-day retention window finds it eligible.
            File.SetLastWriteTimeUtc(terminalSentinelPath, DateTime.UtcNow.AddDays(-30));

            var registryPath = Path.Combine(tempRoot, "room-registry.jsonl");
            await Baton.Vendors.RoomRegistryStore.AppendAsync(
                roomDir, tempRoot, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var sweep = new RoomRetentionSweep(new Baton.Vendors.DaemonSettings { RoomsRetentionDays = 1 });
            var deletedCount = await sweep.ExecuteRoomsRetentionPruneAsync(
                registryFilePathOverride: registryPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, deletedCount);
            Assert.False(Directory.Exists(roomDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private static async Task<string> WriteRoomTerminalSentinelAsync(string roomDir)
    {
        var view = new Baton.Status.WorkflowStatusView(Baton.Status.WorkflowOutcome.Succeeded, [], [], null);
        await Baton.Status.TerminalSentinelWriter.WriteAsync(roomDir, view, TestContext.Current.CancellationToken);
        return Path.Combine(roomDir, Baton.Status.TerminalSentinelWriter.TerminalSentinelFileName);
    }
}
