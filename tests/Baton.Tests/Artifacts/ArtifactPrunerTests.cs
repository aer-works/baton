using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Artifacts;

public class ArtifactPrunerTests
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
            await writer.AppendAsync(@event);
        }
    }

    [Fact]
    public async Task PruneAsync_moves_completed_terminal_run_artifacts_to_pruned_location()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"prune-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDir);
            var snapshotPath = Path.Combine(roomDir, "snapshot.json");
            var logPath = Path.Combine(roomDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-101");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            var artifactFile = Path.Combine(execDir, "output.txt");
            await File.WriteAllTextAsync(artifactFile, "artifact-data", TestContext.Current.CancellationToken);

            // Verify active state before prune
            Assert.True(Directory.Exists(execDir));
            Assert.True(File.Exists(artifactFile));

            var result = await ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.False(Directory.Exists(execDir));

            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.True(Directory.Exists(prunedDir));
            Assert.True(File.Exists(Path.Combine(prunedDir, "output.txt")));
            Assert.Equal("artifact-data", await File.ReadAllTextAsync(Path.Combine(prunedDir, "output.txt"), TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task PruneAsync_prunes_artifact_version_history_to_current_version_only()
    {
        // #496 point 4: a terminal, non-kept room's named-artifact version history sits inside the
        // same retention boundary ArtifactPruner already applies to execution_* directories.
        var roomDir = Path.Combine(Path.GetTempPath(), $"prune-versions-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDir);
            var snapshotPath = Path.Combine(roomDir, "snapshot.json");
            var logPath = Path.Combine(roomDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-201");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId));

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var attribution = new ArtifactAttribution(execId.Value, "worker", "claude", "sonnet");
            RoomArtifacts.Write(roomDir, "plan.md", "v1"u8.ToArray(), attribution);
            RoomArtifacts.Write(roomDir, "plan.md", "v2"u8.ToArray(), attribution);

            Assert.Equal(2, RoomArtifacts.Versions(roomDir, "plan.md").Count);

            var result = await ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken);

            Assert.True(result);
            var versionsAfterPrune = RoomArtifacts.Versions(roomDir, "plan.md");
            Assert.Single(versionsAfterPrune);
            Assert.Equal(2, versionsAfterPrune[0].Version);

            // The current file is untouched -- it already holds the surviving version's bytes.
            Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(artifactsRoot, "plan.md"), TestContext.Current.CancellationToken));

            // Version 1's own file and index line are gone; asking for it by number reads as null.
            Assert.Null(RoomArtifacts.Read(roomDir, "plan.md", 1));
            Assert.NotNull(RoomArtifacts.Read(roomDir, "plan.md", 2));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task PruneAsync_untouches_running_or_paused_runs()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"prune-running-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDir);
            var snapshotPath = Path.Combine(roomDir, "snapshot.json");
            var logPath = Path.Combine(roomDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-102");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId))
            );

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "in-flight", TestContext.Current.CancellationToken);

            var result = await ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken);

            Assert.False(result);
            Assert.True(Directory.Exists(execDir));
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task PruneAsync_untouches_keep_marked_runs()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"prune-keep-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDir);
            var snapshotPath = Path.Combine(roomDir, "snapshot.json");
            var logPath = Path.Combine(roomDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-103");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            await KeepMarker.MarkKeepAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.True(KeepMarker.IsKept(roomDir));

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "kept-artifact", TestContext.Current.CancellationToken);

            var result = await ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken);

            Assert.False(result);
            Assert.True(Directory.Exists(execDir));
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task PruneAsync_is_idempotent_on_repeated_calls()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"prune-idempotent-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDir);
            var snapshotPath = Path.Combine(roomDir, "snapshot.json");
            var logPath = Path.Combine(roomDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-104");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "data.bin"), "data", TestContext.Current.CancellationToken);

            var firstRun = await ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.True(firstRun);

            var secondRun = await ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.False(secondRun);

            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.True(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// A pre-existing target is a recoverable copy from an earlier prune. Pruning NEVER deletes it —
    /// so a second call finds it there, leaves both it and the (resumed) source untouched, and reports
    /// no move. The previous version of this test asserted the OPPOSITE (target overwritten, its
    /// contents gone), encoding the delete-the-recoverable-copy defect #973's second reader caught as
    /// the intended result. This arm now fails against that code and passes against the fix.
    /// </summary>
    [Fact]
    public void PruneDirectory_never_destroys_an_existing_recoverable_copy()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"prune-crash-{Guid.NewGuid():N}");
        try
        {
            var sourceDir = Path.Combine(tempRoot, "artifacts", "execution_exec-105");
            var targetDir = Path.Combine(tempRoot, "artifacts", "pruned", "execution_exec-105");

            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "resumed.txt"), "new-content");

            // The recoverable copy from an earlier prune of this same execution id.
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "recoverable.txt"), "the-good-copy");

            var moved = ArtifactPruner.PruneDirectory(sourceDir, targetDir);

            Assert.False(moved);
            // The recoverable copy is intact...
            Assert.Equal("the-good-copy", File.ReadAllText(Path.Combine(targetDir, "recoverable.txt")));
            // ...and the source was NOT pulled out from under whatever repopulated it.
            Assert.True(File.Exists(Path.Combine(sourceDir, "resumed.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempRoot);
        }
    }

    /// <summary>
    /// Finding 3: pruning holds the task's <see cref="ConcurrencyGuard"/> across probe→move so a resumed
    /// run cannot repopulate <c>execution_{id}</c> in the window between reading a run terminal and moving
    /// its directory. This asserts the lock is load-bearing: while another holder has the task lock,
    /// pruning refuses (throws <see cref="WorkflowLockedException"/>) and moves nothing. Against the
    /// pre-fix code — which took no lock — the same run would probe terminal and move the directory,
    /// so this arm fails there and passes against the fix.
    /// </summary>
    [Fact]
    public async Task PruneAsync_refuses_while_the_task_lock_is_held()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"prune-locked-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDir);
            var snapshotPath = Path.Combine(roomDir, "snapshot.json");
            var logPath = Path.Combine(roomDir, "flow.jsonl");

            await SnapshotBinder.PersistAsync(SingleStepSnapshot(), snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-106");
            await WriteLogEventsAsync(
                logPath,
                new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)),
                new FlowEvent.ExecutionSucceeded(execId)
            );

            var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            await File.WriteAllTextAsync(Path.Combine(execDir, "output.txt"), "held", TestContext.Current.CancellationToken);

            using var heldByAnotherInstance = ConcurrencyGuard.Acquire(roomDir);

            await Assert.ThrowsAsync<WorkflowLockedException>(
                () => ArtifactPruner.PruneAsync(roomDir, TestContext.Current.CancellationToken));

            // Nothing moved: the run's active directory is exactly where it was.
            Assert.True(Directory.Exists(execDir));
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
            Assert.False(Directory.Exists(prunedDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public void ResolvePrunedOutputDirectory_returns_expected_path()
    {
        var path = ArtifactManager.ResolvePrunedOutputDirectory("/artifacts", new ExecutionId("exec-999"));
        Assert.Equal(Path.Combine("/artifacts", "pruned", "execution_exec-999"), path);
    }
}
