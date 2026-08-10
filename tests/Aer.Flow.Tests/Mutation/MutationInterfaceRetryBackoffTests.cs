using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Scheduling;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.Mutation;

public class MutationInterfaceRetryBackoffTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly StepId StepB = new("step-b");

    // Every fake-clock advance below happens only AFTER the event that proves the pump committed
    // to a deadline is visible in the log. Advancing on a wall-clock guess (`await Task.Delay(100)`
    // then Advance) is a race: under load the advance can land before the first attempt has even
    // failed, the deferral deadline then lands after the already-spent advance, and the pump waits
    // on a fake instant nobody will ever reach — the test hangs rather than fails. The poll below
    // is the positive signal; the WaitAsync timeouts on the pump awaits are the backstop that turns
    // any future reintroduction of the race into a red test instead of a hung suite.
    private static async Task<T> WaitForEventAsync<T>(FlowEventLogReader reader, Task pumpTask, CancellationToken cancellationToken)
        where T : FlowEvent
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var events = await reader.ReadAllAsync(cancellationToken);
            if (events.OfType<T>().FirstOrDefault() is { } found)
            {
                return found;
            }

            if (pumpTask.IsCompleted)
            {
                await pumpTask; // surfaces the pump's own exception if it faulted
                Assert.Fail($"Pump completed without appending {typeof(T).Name}.");
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"Timed out waiting for {typeof(T).Name}.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    // A single Advance is not enough to release the pump, even after the deferral event is visible:
    // the pump reads the clock and then creates its relative Task.Delay in two steps, so an advance
    // landing in that gap starts the timer from the already-advanced clock — due at deadline +
    // delay, an instant nothing will ever advance to. Harmless under a real clock (time keeps
    // moving and the pump re-checks readiness on every wake, so it just wakes late); a strand only
    // a fake clock can produce. Advancing repeatedly until the pump returns guarantees some advance
    // lands after the timer exists. Overshooting the deadline is safe — readiness is `now >=
    // notBefore`, never an exact-instant match.
    private static async Task<FlowState> AdvanceUntilPumpCompletesAsync(
        FakeTimeProvider fakeTime, Task<FlowState> pumpTask, TimeSpan step)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!pumpTask.IsCompleted)
        {
            Assert.True(
                stopwatch.Elapsed < PumpCompletionTimeout,
                "Pump did not complete while the clock kept advancing past every deferral deadline.");
            fakeTime.Advance(step);
            await Task.Delay(10);
        }

        return await pumpTask;
    }

    // 1. Fails on a zero-delay retry (Test 1 from §6)
    // Mutation control note: Zeroing the delay in GetRetryObligations causes test 1 to fail (dispatch occurs at t+0) while test 2 remains green.
    [Fact]
    public async Task Test1_Fails_on_zero_delay_retry_steady_backoff_defers_execution()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerPath = Path.Combine(roomDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-1"),
                new WorkflowTemplateId("template-retry-1"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Run pump in background
            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0, // floor sample: Steady initial 1s * 0.5 = 500ms
                cancellationToken: TestContext.Current.CancellationToken);

            // Positive signal that attempt 1 failed and the pump committed to a deadline.
            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            Assert.True(retryEvent.RetryDelayMs >= 500, $"Expected DelayMs >= 500, got {retryEvent.RetryDelayMs}");

            // No second attempt at t+0. This is the assertion the mutation control keys on: with the
            // delay zeroed, attempt 2 dispatches in real time before any advance, and this reads 2.
            var eventsMid = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsMid.OfType<FlowEvent.ExecutionRequestAccepted>());

            // Advance to t + DelayMs - 1ms: still no second attempt. Best-effort as a negative (the
            // grace period can only catch a dispatch that happens promptly); the exact boundary
            // semantics are pinned deterministically by DependencyResolverTests' clamp tests.
            fakeTime.Advance(TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs - 1));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(eventsBefore.OfType<FlowEvent.ExecutionRequestAccepted>());

            // Advance to t + DelayMs and beyond: second attempt dispatches and succeeds
            var finalState = await AdvanceUntilPumpCompletesAsync(
                fakeTime, pumpTask, TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs));

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var eventsFinal = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var acceptedFinal = eventsFinal.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, acceptedFinal.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 2. Polarity (Test 2 from §6)
    [Fact]
    public async Task Test2_Backoff_none_dispatches_retry_immediately_at_t0()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerPath = Path.Combine(roomDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-2"),
                new WorkflowTemplateId("template-retry-2"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var retryEvent = events.OfType<FlowEvent.StepRetryScheduled>().Single();
            Assert.Equal(0, retryEvent.RetryDelayMs);

            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, accepted.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 4. Replay determinism, falsifiable (Test 4 from §6)
    [Fact]
    public void Test4_Replay_determinism_under_throwing_time_provider_and_jitter_source()
    {
        var execId1 = new ExecutionId("exec-1");
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var notBefore = now.AddMilliseconds(500);

        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(execId1, new WorkflowId("wf-4"), StepA, "worker-a", [], ["out.txt"], null, [], new Dictionary<StepId, ExecutionId>())),
            new FlowEvent.ExecutionFailed(execId1, FailureClassification.Retryable, "Transient error"),
            new FlowEvent.StepRetryScheduled(StepA, execId1, notBefore, 500)
        };

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-retry-4"),
            new WorkflowTemplateId("template-retry-4"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
            ]);

        // StateProjector.Project is pure and does not consult any time provider or jitter source
        var state = StateProjector.Project(events, snapshot);

        var stepState = Assert.Single(state.Steps);
        Assert.Equal(StepStatus.Failed, stepState.Status);
        Assert.Equal(notBefore, stepState.RetryNotBefore);
        Assert.Equal(500, stepState.RetryDelayMs);
        Assert.Equal(execId1, stepState.RetryScheduledForExecutionId);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    // 7. Abandoned-crash corner (Test 7 from §6)
    [Fact]
    public async Task Test7_Abandoned_crash_recovery_execution_failed_gets_retry_scheduled()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var execId = new ExecutionId("abandoned-exec-1");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-7"),
                new WorkflowTemplateId("template-retry-7"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // Simulate a crash after the process spawned: an ExecutionRequestAccepted plus the
            // Core half's ExecutionStarted, with no ExecutionExited. Both live in the one
            // flow.jsonl (§5.1) — Core events are LogEntry-wrapped lines in the same file, so they
            // go through the writer's own CoreEvent overload, never a hand-built sidecar file.
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var request = new ExecutionRequest(execId, new WorkflowId("wf-7"), StepA, "worker-a", [], ["out.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                await writerInit.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 12345), TestContext.Current.CancellationToken);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-7"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // StepRetryScheduled is appended after the abandonment's ExecutionFailed, so its
            // presence proves both halves of the recovery happened.
            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);
            Assert.Equal(execId, retryEvent.ForExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed f && f.ExecutionId == execId && f.Reason!.Contains("Abandoned"));

            await AdvanceUntilPumpCompletesAsync(fakeTime, pumpTask, TimeSpan.FromSeconds(10));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 8a. Polarity floor for append-exactly-once: at MaxAttempts = 1 MayRetry is never true, so no
    // StepRetryScheduled may appear at all — and the pump must reach terminal without deferring.
    [Fact]
    public async Task Test8_No_StepRetryScheduled_when_retry_budget_is_exhausted_on_first_failure()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-8"),
                new WorkflowTemplateId("template-retry-8"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-8"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The await itself carries half the claim: fakeTime never advances, so if a deferral
            // were wrongly scheduled for the budget-exhausted step, the pump would never return.
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Test8_StepRetryScheduled_appended_exactly_once_per_failed_attempt()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-8b"),
                new WorkflowTemplateId("template-retry-8b"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.None))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(), // Always fails
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-8b"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var retryEvents = events.OfType<FlowEvent.StepRetryScheduled>().ToList();

            // Attempt 1 fails -> 1 StepRetryScheduled. Attempt 2 fails -> MaxAttempts 2 reached, no more retries.
            Assert.Single(retryEvents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 9. Operator RetryWithRevision is not deferred (Test 9 from §6)
    [Fact]
    public async Task Test9_Operator_RetryWithRevision_dispatches_immediately_clearing_deadline()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerPath = Path.Combine(roomDirectory, "attempt-marker");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-9"),
                new WorkflowTemplateId("template-retry-9"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: ["out.txt"],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 1, Backoff: BackoffPolicy.Patient),
                        PausePoint: new PausePoint([]))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    FailOnFirstAttemptThenSucceed(markerPath, "out.txt", "content"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Attempt 1 runs and fails, pause point triggers WorkflowPaused
            var pausedState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-9"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, pausedState.Status);

            var failedExecId = pausedState.Steps.Single(s => s.StepId == StepA).LatestExecutionId!.Value;

            // Operator issues RetryWithRevision
            var finalState = await MutationInterface.RecordDecisionAsync(
                new WorkflowId("wf-9"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                referencedExecutionId: failedExecId,
                decisionType: DecisionType.RetryWithRevision,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The await above returning is itself the claim: fakeTime never advances, so if the
            // operator's retry had been machine-deferred (Patient's initial delay is minutes), the
            // pump would still be waiting. The step lands Paused again rather than Succeeded — its
            // PausePoint pauses after every outcome, success included, same shape as Test11's
            // StepB — so the immediacy reads off the log: a second accepted execution, its
            // success, and no StepRetryScheduled ever appended for the operator-initiated attempt.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.StepRetryScheduled>());

            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.NotEqual(failedExecId, stepState.LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 10. Not Terminal while deferred (Test 10 from §6)
    [Fact]
    public async Task Test10_WorkflowStatus_remains_Running_while_step_is_deferred()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-10"),
                new WorkflowTemplateId("template-retry-10"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-10"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            var retryEvent = await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var projectedState = StateProjector.Project(events, snapshot);

            Assert.Equal(WorkflowStatus.Running, projectedState.Status);

            // Step by the recorded delay rather than a guessed 20 minutes — the event is the
            // authority on how long Patient actually deferred.
            var finalState = await AdvanceUntilPumpCompletesAsync(
                fakeTime, pumpTask, TimeSpan.FromMilliseconds(retryEvent.RetryDelayMs));
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 11. Paused sibling keeps aer decide reachable (Test 11 from §6)
    [Fact]
    public async Task Test11_Paused_sibling_keeps_aer_decide_reachable_pump_returns_paused()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-11"),
                new WorkflowTemplateId("template-retry-11"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["outA.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient)),
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["outB.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1), PausePoint: new PausePoint([]))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("outA.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("outB.txt")], []),
                    WriteFile("outB.txt", "contentB"),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // StepA fails and defers; StepB succeeds and pauses.
            // Pump should return WorkflowStatus.Paused immediately without blocking on StepA's deferral wait.
            var state = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-11"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, state.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 13. An expired deferral whose step is blocked on a terminally failed dependency is a fixed
    // point. Before the future-deadline filter in the idle wait, this state was a zero-delay spin:
    // nothing ready (the dependency is not Succeeded), nothing in flight, and a deadline in the
    // past producing delay <= 0 -> continue -> re-project -> repeat, forever, at full CPU.
    [Fact]
    public async Task Test13_Expired_deferral_blocked_on_failed_dependency_is_a_fixed_point_not_a_spin()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-13"),
                new WorkflowTemplateId("template-retry-13"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["outA.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["outB.txt"], DependsOn: [StepA], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("outA.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("outB.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The stranded shape, written as history: A succeeded, B failed and was deferred, then
            // A reran (a supersede consequence) and failed permanently -- all before this pump
            // starts, with B's deadline already in the past.
            var aFirst = new ExecutionId("a-1");
            var bAttempt = new ExecutionId("b-1");
            var aRerun = new ExecutionId("a-2");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(aFirst, new WorkflowId("wf-13"), StepA, "worker-a", [], ["outA.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionSucceeded(aFirst), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(bAttempt, new WorkflowId("wf-13"), StepB, "worker-b", [], ["outB.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId> { [StepA] = aFirst })), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(bAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepB, bAttempt, fakeTime.GetUtcNow().AddSeconds(-10), 500), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(aRerun, new WorkflowId("wf-13"), StepA, "worker-a", [], ["outA.txt"], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(aRerun, FailureClassification.Permanent, "dead"), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Nothing ever advances fakeTime: the pump must return on its own, promptly.
            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-13"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(StepStatus.Failed, finalState.Steps.Single(s => s.StepId == StepB).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // 12. Host stop during a deferral wait (Test 12 from §6)
    [Fact]
    public async Task Test12_Host_stop_during_deferral_wait_returns_promptly()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        using var cts = new CancellationTokenSource();

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-12"),
                new WorkflowTemplateId("template-retry-12"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Patient))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [new ProducedOutput("out.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-12"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: cts.Token);

            // Wait until the deferral is committed, so the stop provably lands during (or after
            // entering) the wait rather than before the first attempt even ran.
            await WaitForEventAsync<FlowEvent.StepRetryScheduled>(reader, pumpTask, TestContext.Current.CancellationToken);

            // Signal host stop while pump is waiting on the Patient deferral
            cts.Cancel();

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);
            Assert.NotNull(finalState);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task ExhaustedUntil_failure_RetryNotBefore_equals_reset_moment_while_ordinary_retryable_follows_backoff()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var resetMoment = now.AddMinutes(45);

        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var execIdExhausted = new ExecutionId("exec-exhausted");
            var execIdRetryable = new ExecutionId("exec-retryable");

            // Append ExhaustedUntil failure with explicit reset moment
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execIdExhausted, new WorkflowId("wf-ex"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execIdExhausted, FailureClassification.ExhaustedUntil, "quota exhausted", resetMoment), TestContext.Current.CancellationToken);

            var snapshotA = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-1"),
                new WorkflowTemplateId("tpl-1"),
                1,
                [new WorkflowStepDefinition(StepA, "worker-a", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var eventsExhausted = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var stateExhausted = StateProjector.Project(eventsExhausted, snapshotA);

            var getObligationsMethod = typeof(MutationInterface).GetMethod("GetRetryObligations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var obligationsExhausted = (IEnumerable<object>)getObligationsMethod.Invoke(null, [stateExhausted, snapshotA, fakeTime, (Func<double>)(() => 0.0)])!;
            var exhaustedObligation = obligationsExhausted.Single();

            var notBeforeProperty = exhaustedObligation.GetType().GetProperty("RetryNotBefore")!;
            var exhaustedNotBefore = (DateTimeOffset)notBeforeProperty.GetValue(exhaustedObligation)!;

            Assert.Equal(resetMoment, exhaustedNotBefore);

            // Append ordinary Retryable failure (no reset moment)
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                new ExecutionRequest(execIdRetryable, new WorkflowId("wf-retry"), StepB, "worker-b", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionFailed(execIdRetryable, FailureClassification.Retryable, "ordinary failure"), TestContext.Current.CancellationToken);

            var snapshotB = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snap-2"),
                new WorkflowTemplateId("tpl-2"),
                1,
                [new WorkflowStepDefinition(StepB, "worker-b", [], [], [], RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))]);

            var eventsRetryable = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var stateRetryable = StateProjector.Project(eventsRetryable, snapshotB);

            var obligationsRetryable = (IEnumerable<object>)getObligationsMethod.Invoke(null, [stateRetryable, snapshotB, fakeTime, (Func<double>)(() => 0.0)])!;
            var retryableObligation = obligationsRetryable.Single();
            var retryableNotBefore = (DateTimeOffset)notBeforeProperty.GetValue(retryableObligation)!;

            // Steady backoff with 0.0 jitter sample is 500ms delay
            Assert.Equal(now.AddMilliseconds(500), retryableNotBefore);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #815: the Test9 scenario (operator RetryWithRevision dispatches immediately, clearing the
    // deadline), but for a step #594's classification quota-parked and never paused — the lane-
    // workflow shape #815 measured live, where the step declares no PausePoint at all. Starts from
    // a fabricated Failed + StepRetryScheduled(far-future) history, exactly like Test13's manual
    // event log, so the far-future deadline proves the operator's decision — not fakeTime, which
    // never advances — is what releases the step.
    [Fact]
    public async Task Test815_Operator_RetryWithRevision_against_a_quota_parked_never_paused_step_dispatches_immediately()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureResetMoment = now.AddHours(2);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-retry-815"),
                new WorkflowTemplateId("template-retry-815"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: [],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                    // No PausePoint: the exact lane-workflow shape #815 measured live — nothing
                    // could ever have paused this step.
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The quota-parked history, written directly (Test13's pattern): a first attempt
            // failed with an ExhaustedUntil-shaped classification and a scheduled retry hours in
            // the future -- no WorkflowPaused, because this step has no PausePoint to trigger one.
            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-815"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureResetMoment), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, firstAttempt, farFutureResetMoment, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Operator issues RetryWithRevision against the parked (never-paused) execution.
            // Nothing ever advances fakeTime: if the decision had been machine-deferred back to
            // farFutureResetMoment instead of dispatching now, this would hang until the
            // WaitAsync timeout.
            var finalState = await MutationInterface.RecordDecisionAsync(
                new WorkflowId("wf-815"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                referencedExecutionId: firstAttempt,
                decisionType: DecisionType.RetryWithRevision,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.NotEqual(firstAttempt, stepState.LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
            // Only the fabricated first attempt's deferral -- the operator-triggered attempt was
            // never machine-paced, matching Test9's "no StepRetryScheduled for the operator
            // attempt" assertion.
            Assert.Single(events.OfType<FlowEvent.StepRetryScheduled>());
            Assert.Empty(events.OfType<FlowEvent.WorkflowPaused>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1094: shared seeding for the two polarity tests below — a fabricated parked history (the #815
    // test's pattern) whose classification the caller picks. fakeTime starts before the reset so the
    // step is not yet ready; each test then chooses how to release the pump deterministically.
    private static async Task<(string Room, string Artifacts, string Log, WorkflowDefinitionSnapshot Snapshot,
        Dictionary<string, WorkerBinding> Bindings, FakeTimeProvider FakeTime, DateTimeOffset Reset)>
        SeedParkedStepAsync(FailureClassification classification)
    {
        var room = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var log = Path.Combine(room, "flow.jsonl");
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(2);
        Directory.CreateDirectory(room);

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1094"),
            new WorkflowTemplateId("tmpl-1094"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(StepA, "worker-a", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["worker-a"] = new WorkerBinding.Process(
                new WorkerContract("worker-a", [], [], []), ExitCleanlyWithoutWriting(), TimeSpan.FromSeconds(30))
        };

        var firstAttempt = new ExecutionId("a-1");
        var ct = TestContext.Current.CancellationToken;
        await using var seed = new FlowEventLogWriter(log);
        await seed.AppendAsync(new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
            firstAttempt, new WorkflowId("wf-1094"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
        await seed.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, classification, "seeded park", reset), ct);
        await seed.AppendAsync(new FlowEvent.StepRetryScheduled(
            StepA, firstAttempt, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), ct);

        return (room, Path.Combine(room, "artifacts"), log, snapshot, bindings, new FakeTimeProvider(now), reset);
    }

    [Fact]
    public async Task A_vendor_quota_park_surfaces_the_reset_instant_to_the_foreground()
    {
        var s = await SeedParkedStepAsync(FailureClassification.ExhaustedUntil);
        try
        {
            await using var writer = new FlowEventLogWriter(s.Log);
            var ct = TestContext.Current.CancellationToken;
            DateTimeOffset? captured = null;
            var noticed = new TaskCompletionSource();
            using var cts = new CancellationTokenSource();

            // fakeTime is never advanced, so the pump enters and stays in the deferral wait: the notice
            // fires deterministically (no wall-clock grace), then the host stop releases the pump.
            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1094"), s.Room, s.Snapshot, s.Bindings, s.Artifacts,
                new FlowEventLogReader(s.Log), writer, new CoreDispatcher(writer),
                timeProvider: s.FakeTime, jitterSource: () => 0.0, cancellationToken: cts.Token,
                onVendorQuotaPark: instant => { captured = instant; noticed.TrySetResult(); });

            await noticed.Task.WaitAsync(PumpCompletionTimeout, ct);
            await cts.CancelAsync();
            await pump.WaitAsync(PumpCompletionTimeout, ct);

            Assert.Equal(s.Reset, captured);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(s.Room);
        }
    }

    [Fact]
    public async Task An_ordinary_retry_backoff_does_not_surface_a_vendor_quota_notice()
    {
        var s = await SeedParkedStepAsync(FailureClassification.Retryable);
        try
        {
            await using var writer = new FlowEventLogWriter(s.Log);
            DateTimeOffset? captured = null;

            // Advancing the clock past the reset drives the parked step's retry to a terminal state —
            // deterministic, no fixed grace. Whether or not the pump paused in the backoff wait first,
            // an ordinary Retryable park must never fire the vendor-quota notice.
            var pump = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1094"), s.Room, s.Snapshot, s.Bindings, s.Artifacts,
                new FlowEventLogReader(s.Log), writer, new CoreDispatcher(writer),
                timeProvider: s.FakeTime, jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken,
                onVendorQuotaPark: instant => captured = instant);

            await AdvanceUntilPumpCompletesAsync(s.FakeTime, pump, TimeSpan.FromMinutes(30));

            Assert.Null(captured);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(s.Room);
        }
    }
}
