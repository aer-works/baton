using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Store;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1563 (S0 of the ratified quota design, #802): the park this exercises, and why it can hold
/// <c>flow.lock</c> indefinitely, is described once at
/// <see cref="CancelRequestPoller.TickAsync"/>'s own remarks — this fixture only drives it. A
/// quota-parked lane must notice a cancel delivered through
/// <see cref="InFlightExecutionRegistry.MarkParkedCancelIntent"/> without ever waiting out the
/// park. Mirrors the fabricated-parked-history fixture
/// <c>MutationInterfaceRetryBackoffTests.Test815</c> already uses for the same shape.
/// </summary>
public class QuotaParkCancelArrestTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly StepId StepB = new("step-b");
    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

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

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Timed out waiting for {typeof(T).Name}.");
            Assert.False(pumpTask.IsCompleted, "expected the wake to settle the park while the sibling dispatch was still in flight, not after the pump itself exited");
            await Task.Delay(20, cancellationToken); // wait-ok: poll interval inside a 20s-bounded loop, not the wait ceiling itself
        }
    }

    [Fact]
    public async Task Cancel_delivered_during_a_quota_park_settles_ExecutionCancelled_without_advancing_the_clock()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureReset = now.AddDays(1);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1563"),
                new WorkflowTemplateId("template-1563"),
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
                    // No PausePoint, matching #815's lane-workflow shape: nothing could ever have
                    // paused this step, so `baton decide` is not the reachability path under test.
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The quota-parked history, written directly (Test815's pattern): a first attempt
            // failed with an ExhaustedUntil classification and a retry scheduled a day out — the
            // worker process behind it has already exited, so nothing is registered in-flight for
            // it by the time a cancel arrives.
            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1563"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, firstAttempt, farFutureReset, RetryDelayMs: (int)TimeSpan.FromDays(1).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1563"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                inFlightExecutions: registry,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // The cancel channel's own delivery point (CancelRequestPoller.TickAsync marks the same
            // way once it sees this step Failed with a pending RetryNotBefore) — called directly
            // here to isolate the pump-side wake from the file-polling machinery around it.
            registry.MarkParkedCancelIntent(firstAttempt);

            // fakeTime is NEVER advanced: if the mark's wake were not wired into the deferral wait's
            // WhenAny, this would hang until AdvanceUntilPumpCompletesAsync-style intervention —
            // which this test deliberately never performs — and time out instead of returning.
            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.Equal(StepStatus.Cancelled, stepState.Status);
            // The stale far-future deadline must not survive the settle, or a sibling deferral wait
            // reading RetryNotBefore off this step would keep waiting on a park that already ended.
            Assert.Null(stepState.RetryNotBefore);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested c && c.ExecutionId == firstAttempt);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == firstAttempt);
            // No second attempt was ever dispatched for the parked step.
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Polarity: an intent marked for a target that is NOT the currently-parked step's latest
    // execution is silently dropped by the pump's fail-closed validation (IsParkedRetryTarget) —
    // the real park is left untouched, not spuriously settled — and the pump keeps waiting exactly
    // as it would have with no mark at all. The poller's own bounded retry, not this seam, is what
    // eventually surfaces a genuinely unreachable target.
    [Fact]
    public async Task Marking_an_intent_for_a_mismatched_execution_id_leaves_the_real_park_untouched()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureReset = now.AddDays(1);
        using var cts = new CancellationTokenSource();

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1563b"),
                new WorkflowTemplateId("template-1563b"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            var firstAttempt = new ExecutionId("a-1");
            var mismatchedTarget = new ExecutionId("not-the-parked-execution");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1563b"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, firstAttempt, farFutureReset, RetryDelayMs: (int)TimeSpan.FromDays(1).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1563b"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                inFlightExecutions: registry,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: cts.Token);

            registry.MarkParkedCancelIntent(mismatchedTarget);

            // A hostStop is the deterministic way out of a wait this test deliberately never
            // resolves any other way: if the mismatched mark had wrongly settled the real park,
            // the pump would already have returned Terminal/Cancelled before this Cancel() even
            // has an effect, and the assertions below would catch it either way.
            await cts.CancelAsync();
            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.Equal(farFutureReset, stepState.RetryNotBefore);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Second-reader review finding: the wake was only wired into the idle-deferral branch
    // (`inFlight.Count == 0`), not the OTHER wait this same loop uses when a sibling step's
    // dispatch is genuinely in flight (`waitCandidates`). The risk that wiring closes — a
    // DIFFERENT step sitting quota-parked while this one's dispatch is still live — is spelled
    // out once, beside that wiring, at MutationInterface.cs's own `#1563` comment on
    // `waitParkedCancelWake`. This drives a real (short) OS process for StepB so
    // `inFlight.Count > 0` is genuine, not simulated.
    [Fact]
    public async Task Cancel_delivered_while_a_sibling_step_is_genuinely_in_flight_still_settles_the_parked_step()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var farFutureReset = now.AddDays(1);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1563c"),
                new WorkflowTemplateId("template-1563c"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(StepA, "worker-a", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2, Backoff: BackoffPolicy.Steady)),
                    // A real, short-lived OS process (M10 Phase 4's SleepThenWriteFile) — long
                    // enough that the assertion below genuinely races it, short enough to keep this
                    // test's own wall-clock small.
                    new WorkflowStepDefinition(StepB, "worker-b", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["worker-b"] = new WorkerBinding.Process(
                    new WorkerContract("worker-b", [], [new ProducedOutput("out.txt")], []),
                    SleepThenWriteFile(TimeSpan.FromSeconds(4), "out.txt", "content"),
                    TimeSpan.FromSeconds(30)),
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1563c"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", farFutureReset), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(
                    StepA, firstAttempt, farFutureReset, RetryDelayMs: (int)TimeSpan.FromDays(1).TotalMilliseconds), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1563c"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                inFlightExecutions: registry,
                timeProvider: fakeTime,
                jitterSource: () => 0.0,
                cancellationToken: TestContext.Current.CancellationToken);

            // StepB's own request being accepted (not StepA's, already in the log from the
            // fabricated history above) is the positive signal that this round dispatched it —
            // joining `inFlight` — before the pump moves on to the busy `waitCandidates` branch
            // this finding targets, rather than the idle-deferral one.
            var stopwatchForStepB = Stopwatch.StartNew();
            while (!(await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Any(e => e.Request.StepId == StepB))
            {
                Assert.True(stopwatchForStepB.Elapsed < TimeSpan.FromSeconds(20), "Timed out waiting for StepB's dispatch.");
                Assert.False(pumpTask.IsCompleted, "expected StepB to still be dispatching when this check runs");
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: poll interval inside a 20s-bounded loop, not the wait ceiling itself
            }

            registry.MarkParkedCancelIntent(firstAttempt);

            // Positive signal, polled well inside StepB's 4s sleep: proves the busy branch's own
            // WhenAny woke on the mark rather than waiting for StepB's dispatch, a host stop, or
            // fakeTime (never advanced) to reach the far-future RetryNotBefore.
            await WaitForEventAsync<FlowEvent.ExecutionCancelled>(reader, pumpTask, TestContext.Current.CancellationToken);

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == StepA).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepB).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
