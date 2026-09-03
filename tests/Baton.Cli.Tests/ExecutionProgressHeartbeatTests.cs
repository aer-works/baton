using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Store;

namespace Baton.Cli.Tests;

/// <summary>
/// #1549: unit-level tests against <see cref="ExecutionProgressHeartbeat.TickAsync"/> — the
/// mtime-gated, content-free heartbeat that closes the gap a healthy, long-running lane otherwise
/// leaves in <c>flow.jsonl</c>. Covers the baseline-on-first-observation rule, the "quiet stays
/// quiet" no-op, the advance-triggers-emission case, and the "zero or ambiguous candidates" reset —
/// deliberately not <see cref="ExecutionProgressHeartbeat.RunAsync"/>, whose only added behaviour is
/// a real-time <c>Task.Delay</c> loop around the same tick, already exercised deterministically here.
/// </summary>
public class ExecutionProgressHeartbeatTests
{
    private static readonly WorkflowDefinitionSnapshot Snapshot = new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("heartbeat-test"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("heartbeat-test"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public async Task First_observation_of_a_running_execution_captures_a_baseline_and_emits_nothing()
    {
        var (roomDirectory, artifactsRootPath, logPath) = CreateRoom();
        try
        {
            var execId = new ExecutionId("exec-1");
            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
            WriteStdout(artifactsRootPath, execId, "first chunk");

            var tracker = await ExecutionProgressHeartbeat.TickAsync(
                logPath, artifactsRootPath, Snapshot, writer, default, TestContext.Current.CancellationToken);

            Assert.Equal(execId, tracker.ExecutionId);
            Assert.NotNull(tracker.LastSeenStdoutMtimeUtc);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionProgress>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_second_tick_with_no_stdout_advance_stays_quiet()
    {
        var (roomDirectory, artifactsRootPath, logPath) = CreateRoom();
        try
        {
            var execId = new ExecutionId("exec-1");
            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
            WriteStdout(artifactsRootPath, execId, "first chunk");

            var afterFirst = await ExecutionProgressHeartbeat.TickAsync(
                logPath, artifactsRootPath, Snapshot, writer, default, TestContext.Current.CancellationToken);

            // A wedged worker: the file exists but its mtime never moves between ticks.
            var afterSecond = await ExecutionProgressHeartbeat.TickAsync(
                logPath, artifactsRootPath, Snapshot, writer, afterFirst, TestContext.Current.CancellationToken);

            Assert.Equal(afterFirst, afterSecond);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionProgress>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task An_advanced_stdout_mtime_emits_exactly_one_ExecutionProgress()
    {
        var (roomDirectory, artifactsRootPath, logPath) = CreateRoom();
        try
        {
            var execId = new ExecutionId("exec-1");
            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
            WriteStdout(artifactsRootPath, execId, "first chunk");

            var afterFirst = await ExecutionProgressHeartbeat.TickAsync(
                logPath, artifactsRootPath, Snapshot, writer, default, TestContext.Current.CancellationToken);

            // Simulate real worker output: a later write, so mtime strictly advances.
            var stdoutPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execId), ExecutionStreamLogger.StdoutLogFileName);
            File.SetLastWriteTimeUtc(stdoutPath, File.GetLastWriteTimeUtc(stdoutPath).AddSeconds(1));

            var afterSecond = await ExecutionProgressHeartbeat.TickAsync(
                logPath, artifactsRootPath, Snapshot, writer, afterFirst, TestContext.Current.CancellationToken);

            Assert.True(afterSecond.LastSeenStdoutMtimeUtc > afterFirst.LastSeenStdoutMtimeUtc);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var progress = Assert.Single(events.OfType<FlowEvent.ExecutionProgress>());
            Assert.Equal(execId, progress.ExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task No_running_or_parked_candidate_resets_the_tracker_and_emits_nothing()
    {
        var (roomDirectory, artifactsRootPath, logPath) = CreateRoom();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            // No ExecutionRequestAccepted at all -- the step is Pending, not Running.

            var tracker = await ExecutionProgressHeartbeat.TickAsync(
                logPath,
                artifactsRootPath,
                Snapshot,
                writer,
                new ExecutionProgressHeartbeat.Tracker(new ExecutionId("stale"), DateTime.UtcNow),
                TestContext.Current.CancellationToken);

            Assert.Equal(default, tracker);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionProgress>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static (string RoomDirectory, string ArtifactsRootPath, string LogPath) CreateRoom()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"execution-progress-heartbeat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var artifactsRootPath = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        return (roomDirectory, artifactsRootPath, logPath);
    }

    private static void WriteStdout(string artifactsRootPath, ExecutionId executionId, string content)
    {
        var directory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        File.WriteAllText(Path.Combine(directory, ExecutionStreamLogger.StdoutLogFileName), content);
    }
}
