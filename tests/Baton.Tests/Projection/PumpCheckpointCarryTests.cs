using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Store;
using Baton.Tests.TestSupport;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Projection;

/// <summary>
/// The two claims about #971's core-aggregate carry that only the real pump can witness — the
/// Scope1b_* fixtures in <see cref="ProjectionCheckpointTests"/> drive the primitives
/// (<c>CoreEventAggregation</c>, <c>ProcessCrashRecoveryDetector</c>, <c>StateProjector</c>)
/// directly, so none of them would notice <see cref="MutationInterface"/> wiring those primitives
/// together wrongly (#971's second reader). Both tests here call only
/// <see cref="MutationInterface.StartWorkflowAsync"/> and assert on what it durably leaves behind.
///
/// The persisted half of the carry stays out of black-box reach: every reachable save happens with
/// no unfinalized process attempt (in-flight is drained and recorded before the save, orphans are
/// resolved before any save, and non-process steps never have core events), so Prune's
/// Running-only whitelist empties the aggregates in every checkpoint the pump can be driven to
/// write — the divergence fixtures' non-empty checkpoints are hand-built for that reason. The fold
/// itself, though, IS reachable, which this class's first draft got wrong and the second reader's
/// counterexample corrected: the crash-recovery buckets are priority-ordered with an early
/// `continue`, so a multi-bucket round defers the lower bucket to the next round, past the read
/// cursor — the two-bucket test below is that trace.
/// </summary>
public class PumpCheckpointCarryTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    // Same positive-signal-then-act discipline as MutationInterfaceRetryBackoffTests' poll helper
    // (see the race note there); local copy because that one is rightly private to its fixture.
    private static async Task WaitForEventAsync<T>(FlowEventLogReader reader, Task pumpTask, CancellationToken cancellationToken)
        where T : FlowEvent
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var events = await reader.ReadAllAsync(cancellationToken);
            if (events.OfType<T>().Any())
            {
                return;
            }

            if (pumpTask.IsCompleted)
            {
                await pumpTask;
                Assert.Fail($"Pump completed without appending {typeof(T).Name}.");
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {typeof(T).Name}.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static WorkflowDefinitionSnapshot MakeRetryableSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-carry"),
        new WorkflowTemplateId("template-carry"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                StepA, "worker-a", Inputs: [], Outputs: ["out.txt"], DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
        ]);

    private static Dictionary<string, WorkerBinding> FailingBindings() => new()
    {
        // Exits clean but never writes the declared output, so every attempt classifies Retryable —
        // which is what parks step-a in a Patient deferral with its failed attempt still latest.
        ["worker-a"] = new WorkerBinding.Process(
            new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
            ExitWithFailureCode(),
            TimeSpan.FromSeconds(30)),
    };

    /// <summary>
    /// Runs one real pump to the deferral-host-stop save: attempt 1 genuinely dispatches, fails,
    /// and schedules a Patient retry; the host stop lands during the deferral wait, which is the
    /// one reachable save whose step is still Running with a resolved attempt as its latest —
    /// exactly the shape whose aggregates <c>Prune</c> keeps. Returns the failed attempt's id.
    /// </summary>
    private static async Task<ExecutionId> RunPumpToDeferralSaveAsync(
        string roomDirectory, string artifactsRoot, string logPath, FakeTimeProvider fakeTime)
    {
        using var cts = new CancellationTokenSource();
        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);

        var pumpTask = MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-carry"), roomDirectory, MakeRetryableSnapshot(), FailingBindings(),
            artifactsRoot, reader, writer, new CoreDispatcher(writer, writer),
            timeProvider: fakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token);

        // The retry event is the durable proof that a LATER round has already re-read the log past
        // attempt 1's CoreEvents: they entered as one round's tail, and the offset has moved on.
        await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);
        cts.Cancel();
        await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

        var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
        return events.OfType<FlowEvent.ExecutionRequestAccepted>().Single().Request.ExecutionId;
    }

    [Fact]
    public async Task The_saved_checkpoint_prunes_the_resolved_attempts_core_events_rather_than_carrying_them_forever()
    {
        // Red-proven against the save site: with the Prune call there removed, attempt 1's
        // started/exited entries — merged during the round that read them and folded forward —
        // persist into the checkpoint and both Empty asserts fail. The unpruned direction is the
        // real defect class: a long-lived task's checkpoint accreting every execution it ever ran.
        // Deliberately NOT a fold test — see the class comment for why none can exist today.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var attempt1 = await RunPumpToDeferralSaveAsync(
                roomDirectory, Path.Combine(roomDirectory, "artifacts"), logPath,
                new FakeTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)));

            // The in-test control: attempt 1's core events genuinely exist in the log, so the
            // Empty asserts below measure pruning, not a run where nothing ever spawned.
            var coreEvents = await new FlowEventLogReader(logPath).ReadAllCoreEventsAsync(TestContext.Current.CancellationToken);
            Assert.Contains(coreEvents, e => e is CoreEvent.ExecutionStarted s && s.ExecutionId == attempt1);
            Assert.Contains(coreEvents, e => e is CoreEvent.ExecutionExited x && x.ExecutionId == attempt1);

            var checkpoint = ProjectionCheckpointStore.Load(roomDirectory);
            Assert.NotNull(checkpoint);
            // A real, tail-mode checkpoint (the next pump will seek, not replay) whose aggregates
            // hold nothing: attempt 1 is resolved (ExecutionFailed recorded), so its core events
            // are exactly what Prune exists to drop at the save.
            Assert.True(checkpoint.ByteOffset > 0);
            Assert.Empty(checkpoint.State.CoreStartedExecutionIds);
            Assert.Empty(checkpoint.State.CoreExitedByExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_recovery_pump_reading_only_the_tail_abandons_a_crashed_attempt_rather_than_respawning_it()
    {
        // The pump-level arm of the Scope1b divergence fixtures: same checkpoint-plus-crashed-tail
        // shape, but classified by StartWorkflowAsync itself rather than by calling the detector.
        // The wrong branch is a real dispatch (ToResubmit spawns the recorded request again), so a
        // regression here double-runs a worker that may still be alive — the stub dispatcher plus
        // the accepted-count assert make that direction loud.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await RunPumpToDeferralSaveAsync(roomDirectory, artifactsRoot, logPath, fakeTime);

            // The crashed second pump, reconstructed as the exact log lines it would have left:
            // it dispatched the scheduled retry (attempt 2 of 2), Core recorded the spawn, and the
            // host died before any exit — after the checkpoint above, so a bounded recovery read
            // sees only these lines.
            var attempt2 = new ExecutionId(Guid.NewGuid().ToString("n"));
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, attempt2);
                var request = new ExecutionRequest(
                    attempt2, new WorkflowId("wf-carry"), StepA, "worker-a", Inputs: [], Outputs: ["out.txt"],
                    TimeSpan.FromSeconds(30),
                    ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(attempt2, Pid: 4242), TestContext.Current.CancellationToken);
            }

            FlowState finalState;
            var stub = new StubCoreDispatcher();
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                finalState = await MutationInterface.StartWorkflowAsync(
                        new WorkflowId("wf-carry"), roomDirectory, MakeRetryableSnapshot(), FailingBindings(),
                        artifactsRoot, reader, writer, stub,
                        timeProvider: fakeTime, jitterSource: () => 0.0,
                        cancellationToken: TestContext.Current.CancellationToken)
                    .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);
            }

            // Abandoned and exhausted (attempt 2 of 2), never respawned: still exactly the two
            // accepted requests the two attempts wrote, and no dispatch ever reached Core.
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single().Status);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            var abandoned = events.OfType<FlowEvent.ExecutionFailed>().Single(e => e.ExecutionId == attempt2);
            Assert.Contains("crash recovery", abandoned.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.False(stub.DispatchStarted.TryRead(out _), "recovery re-dispatched the crashed attempt");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task An_orphan_discovered_alongside_a_classifiable_exit_is_still_abandoned_one_round_later()
    {
        // THE direct fold test, from the second reader's counterexample to this class's own first
        // draft (which claimed none could exist): the crash-recovery buckets are priority-ordered
        // and each `continue`s after acting, so a round that classifies a recorded exit does NOT
        // act on an orphan discovered in the same read — the orphan is handled a round later, when
        // its ExecutionStarted is already behind the read cursor. The fold is the only thing that
        // keeps it visible: red-proven by deleting the fold, which turns the round-2 abandon into
        // a resubmission — the double-spawn hazard the fold's own comment in MutationInterface
        // names as what it exists to prevent.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepY = new StepId("step-y");
            var stepZ = new StepId("step-z");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-two-buckets"),
                new WorkflowTemplateId("template-two-buckets"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(stepY, "worker-a", Inputs: [], Outputs: ["out.txt"], DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.Steady)),
                    new WorkflowStepDefinition(stepZ, "worker-a", Inputs: [], Outputs: ["out.txt"], DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.Steady)),
                ]);

            // The crashed prior pump's log, hand-written: Y ran and exited before the crash
            // (ToClassify, the highest-priority bucket), Z spawned and never exited (the orphan,
            // the lowest). Both surface in the same first read; only Y is acted on that round.
            var execY = new ExecutionId(Guid.NewGuid().ToString("n"));
            var execZ = new ExecutionId(Guid.NewGuid().ToString("n"));
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                foreach (var (execId, stepId) in new[] { (execY, stepY), (execZ, stepZ) })
                {
                    var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
                    var request = new ExecutionRequest(
                        execId, new WorkflowId("wf-two-buckets"), stepId, "worker-a", Inputs: [], Outputs: ["out.txt"],
                        TimeSpan.FromSeconds(30),
                        ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
                    await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                }

                await writer.AppendAsync(new CoreEvent.ExecutionStarted(execY, Pid: 4242), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionExited(execY, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new CoreEvent.ExecutionStarted(execZ, Pid: 4243), TestContext.Current.CancellationToken);
            }

            FlowState finalState;
            var stub = new StubCoreDispatcher();
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                finalState = await MutationInterface.StartWorkflowAsync(
                        new WorkflowId("wf-two-buckets"), roomDirectory, snapshot, FailingBindings(),
                        artifactsRoot, reader, writer, stub,
                        timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)),
                        jitterSource: () => 0.0,
                        cancellationToken: TestContext.Current.CancellationToken,
                        workerLivenessProbe: _ => new EngineLivenessResult(EngineLivenessStatus.Dead))
                    .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);
            }

            // Y classified from its recorded exit (failed: exit 0 but the declared output is
            // missing); Z has no retry budget and its fake dead PID produces the concrete terminal
            // fact on the next round. Neither ever reached the dispatcher, and no third attempt exists.
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == stepY).Status);
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == stepZ).Status);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            var abandoned = events.OfType<FlowEvent.ExecutionFailed>().Single(e => e.ExecutionId == execZ);
            Assert.Equal(FailureClassification.Permanent, abandoned.FailureClassification);
            Assert.Equal("Worker PID 4243 is no longer alive and no ExecutionExited was recorded.", abandoned.Reason);
            Assert.False(stub.DispatchStarted.TryRead(out _), "the orphan was re-dispatched instead of abandoned");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
