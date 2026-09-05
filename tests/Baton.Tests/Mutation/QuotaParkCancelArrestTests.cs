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
/// <see cref="InFlightExecutionRegistry.MarkArrestIntent"/> without ever waiting out the
/// park. Mirrors the fabricated-parked-history fixture
/// <c>MutationInterfaceRetryBackoffTests.Test815</c> already uses for the same shape.
/// </summary>
public class QuotaParkCancelArrestTests
{
    private static readonly StepId StepA = new("step-a");
    private static readonly StepId StepB = new("step-b");
    private static readonly TimeSpan PumpCompletionTimeout = TimeSpan.FromSeconds(30);

    // Returns the found event AND the exact events snapshot it was found in (F3, #1605 review):
    // callers that need to assert a state fact true AT THE MOMENT the event appeared (not
    // "eventually", which a later, unrelated read could satisfy vacuously) read off this same list.
    private static async Task<(T Found, IReadOnlyList<FlowEvent> Events)> WaitForEventAsync<T>(FlowEventLogReader reader, Task pumpTask, CancellationToken cancellationToken)
        where T : FlowEvent
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var events = await reader.ReadAllAsync(cancellationToken);
            if (events.OfType<T>().FirstOrDefault() is { } found)
            {
                return (found, events);
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
            var dispatcher = new CoreDispatcher(writer, writer);
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
            registry.MarkArrestIntent(firstAttempt, "test: quota-parked");

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
    // the real park is left untouched, not spuriously settled. F4 (#1605 review): the previous
    // version of this test proved that with an ambient-token cancel, but a pre-cancelled token can
    // win the deferral wait's WhenAny outright (the host-stop guard, same race as the F1 sub-point
    // documented at this loop's own `deferralHostStopWatcher` check) and skip the wait — and with it
    // SettleParkedCancelIntentsAsync/IsParkedRetryTarget — entirely, so the old assertions (no
    // CancellationRequested/ExecutionCancelled at all) were satisfied vacuously whether or not the
    // discrimination logic under test ever ran. Fixed deterministically, with no clock and no host
    // stop: mark BOTH the mismatched id and the real parked one before the pump ever wakes for
    // either. The real one settling is the positive control that the wake/drain path actually ran
    // (a broken settle hangs this test out to WaitAsync's timeout, not a silent pass); the mismatched
    // one never settling — whether IsParkedRetryTarget drops it in the same drain as the real one or
    // a later one — is what actually proves it discriminates rather than settling everything the
    // wake fires for.
    [Fact]
    public async Task Marking_an_intent_for_a_mismatched_execution_id_leaves_the_real_park_untouched()
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
            var dispatcher = new CoreDispatcher(writer, writer);
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
                cancellationToken: TestContext.Current.CancellationToken);

            // Mark the mismatched id first — if IsParkedRetryTarget did not actually validate
            // against projected state, this alone could spuriously settle the real park.
            registry.MarkArrestIntent(mismatchedTarget, "test: mismatched target");
            // Then mark the REAL target: its settlement below is the positive control (F4) proving
            // the wake/drain path genuinely ran — a broken drain hangs this test out to the WaitAsync
            // timeout below instead of a same-shape negative silently passing for the wrong reason.
            registry.MarkArrestIntent(firstAttempt, "test: quota-parked");

            var finalState = await pumpTask.WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            var stepState = finalState.Steps.Single(s => s.StepId == StepA);
            Assert.Equal(StepStatus.Cancelled, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            // The real target settled...
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested c && c.ExecutionId == firstAttempt);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == firstAttempt);
            // ...but the mismatched one never did — dropped by IsParkedRetryTarget's fail-closed
            // check whenever it was drained, not settled alongside the real one just because both
            // were marked before the pump's first wake.
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested c && c.ExecutionId == mismatchedTarget);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == mismatchedTarget);
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
    // `inFlight.Count > 0` is genuine, not simulated. F3 (#1605 review): proving the BUSY branch
    // specifically (not the idle one, also wired to the same latch, catching it after StepB
    // happens to finish first) needs a state discriminator, not just a short sleep — see the
    // assertion beside `WaitForEventAsync<FlowEvent.ExecutionCancelled>` below.
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
            var dispatcher = new CoreDispatcher(writer, writer);
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
            // this finding targets, rather than the idle-deferral one. Its ExecutionId is captured
            // here for the state discriminator below (F3).
            ExecutionId? stepBExecutionId = null;
            var stopwatchForStepB = Stopwatch.StartNew();
            while (stepBExecutionId is null)
            {
                var eventsSoFar = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
                stepBExecutionId = eventsSoFar.OfType<FlowEvent.ExecutionRequestAccepted>()
                    .Where(e => e.Request.StepId == StepB)
                    .Select(e => (ExecutionId?)e.Request.ExecutionId)
                    .FirstOrDefault();
                if (stepBExecutionId is not null)
                {
                    break;
                }

                Assert.True(stopwatchForStepB.Elapsed < TimeSpan.FromSeconds(20), "Timed out waiting for StepB's dispatch.");
                Assert.False(pumpTask.IsCompleted, "expected StepB to still be dispatching when this check runs");
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: poll interval inside a 20s-bounded loop, not the wait ceiling itself
            }

            registry.MarkArrestIntent(firstAttempt, "test: quota-parked");

            // Positive signal, polled well inside StepB's 4s sleep: proves the busy branch's own
            // WhenAny woke on the mark rather than waiting for StepB's dispatch, a host stop, or
            // fakeTime (never advanced) to reach the far-future RetryNotBefore.
            var (_, eventsAtCancel) = await WaitForEventAsync<FlowEvent.ExecutionCancelled>(reader, pumpTask, TestContext.Current.CancellationToken);

            // F3 (#1605 review): the wait above alone can pass via a vacuous path — StepB finishes
            // first (winning its own race against the 4s sleep under load), the pump moves to the
            // IDLE branch, and THAT branch's wake (also wired to the same latch) catches the mark
            // instead of the busy branch this test targets. Binding "the busy wait delivered while
            // StepB was genuinely in flight" to a state fact instead of a clock: on this exact same
            // read, StepB must not have recorded ExecutionSucceeded yet — if it had, the idle path
            // (not the busy one) is what caught this.
            Assert.DoesNotContain(eventsAtCancel, e => e is FlowEvent.ExecutionSucceeded s && s.ExecutionId == stepBExecutionId!.Value);

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

    // #1634: the redispatch race a poller-less pump loses. CancelCommand's DIRECT path (no live
    // `baton run` holding flow.lock) never runs CancelRequestPoller, so nothing ever calls
    // MarkArrestIntent for it -- MutationInterface.RequestCancellationAsync journals
    // CancellationRequested itself (intent-first) and then drives its OWN pump to a fixed point.
    // Reachable only against an already-OVERDUE park (a still-future one is refused earlier by
    // CancelCommand's own hasFutureDeferral gate, spec/baton.md's "direct path" paragraph) --
    // fabricated here the same way MutationInterfaceRetryBackoffTests' Test13 fabricates an
    // already-due StepRetryScheduled, so DependencyResolver.GetReadySteps sees the deadline as
    // already elapsed without ever needing to advance the fake clock.
    [Fact]
    public async Task Overdue_park_cancelled_through_the_direct_poller_less_path_settles_Cancelled_not_a_redispatch()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var overdueRetryNotBefore = now.AddSeconds(-10);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1634"),
                new WorkflowTemplateId("template-1634"),
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
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The overdue-park history, written directly: a first attempt failed and a retry was
            // already scheduled for a deadline now in the past -- the exact shape a real crash or a
            // prior pump exit would leave behind.
            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1634"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, firstAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // The DIRECT path itself: no CancelRequestPoller runs alongside this call, and
            // MarkArrestIntent is never invoked. RequestCancellationAsync journals
            // CancellationRequested before starting its own pump (intent-first ordering) -- the
            // ledger already carries the fact by the time the pump's first round runs.
            var finalState = await MutationInterface.RequestCancellationAsync(
                    new WorkflowId("wf-1634"),
                    roomDirectory,
                    snapshot,
                    bindings,
                    artifactsRoot,
                    reader,
                    writer,
                    dispatcher,
                    firstAttempt,
                    timeProvider: fakeTime,
                    jitterSource: () => 0.0,
                    cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested c && c.ExecutionId == firstAttempt);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == firstAttempt);
            // The race this issue is about: the journalled cancel must win it, not a second dispatch.
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Regression control for the fix above: the IDENTICAL overdue-park history, pumped with NO
    // cancellation request in the ledger at all, must still redispatch exactly as it always has --
    // the ledger-read rule must only preempt a redispatch when a CancellationRequested is actually
    // present, never on an ordinary overdue park.
    [Fact]
    public async Task Overdue_park_with_no_cancel_request_still_redispatches()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var overdueRetryNotBefore = now.AddSeconds(-10);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1634b"),
                new WorkflowTemplateId("template-1634b"),
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
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1634b"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, firstAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-1634b"),
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

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Polarity control (second-reader finding): a CancellationRequested naming a STALE execution id
    // — one the step already redispatched past, no longer its LatestExecutionId — must NOT stop the
    // step's CURRENT overdue park from redispatching. Mirrors
    // Marking_an_intent_for_a_mismatched_execution_id_leaves_the_real_park_untouched's polarity for
    // the poller-driven path, but for the direct/ledger-read path this fixture adds: proves
    // IsParkedRetryTarget's `LatestExecutionId ==` clause is load-bearing here too, not incidental.
    [Fact]
    public async Task Overdue_park_with_a_cancel_request_for_a_stale_execution_id_still_redispatches()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var overdueRetryNotBefore = now.AddSeconds(-10);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1634c"),
                new WorkflowTemplateId("template-1634c"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(
                        StepA,
                        "worker-a",
                        Inputs: [],
                        Outputs: [],
                        DependsOn: [],
                        RetryPolicy: new RetryPolicy(MaxAttempts: 3, Backoff: BackoffPolicy.Steady))
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // Two attempts already exhausted BEFORE this pump starts: the first (staleAttempt) failed
            // and was already redispatched to the second (currentAttempt) by a prior pump — the
            // second is the step's CURRENT parked attempt, overdue, still with retry budget left
            // (MaxAttempts: 3).
            var staleAttempt = new ExecutionId("a-1");
            var currentAttempt = new ExecutionId("a-2");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(staleAttempt, new WorkflowId("wf-1634c"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(staleAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, staleAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(currentAttempt, new WorkflowId("wf-1634c"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(currentAttempt, FailureClassification.Retryable, "boom again"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, currentAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // The DIRECT path, targeting the STALE first attempt — not the step's current parked one.
            var finalState = await MutationInterface.RequestCancellationAsync(
                    new WorkflowId("wf-1634c"),
                    roomDirectory,
                    snapshot,
                    bindings,
                    artifactsRoot,
                    reader,
                    writer,
                    dispatcher,
                    staleAttempt,
                    timeProvider: fakeTime,
                    jitterSource: () => 0.0,
                    cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(PumpCompletionTimeout, TestContext.Current.CancellationToken);

            // The step's CURRENT park redispatched normally — the stale target's cancel request
            // never touches it.
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested c && c.ExecutionId == staleAttempt);
            // The stale target itself never settles Cancelled — dropped, not spuriously arrested.
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == staleAttempt);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == currentAttempt);
            // Three attempts total: the two fabricated ones plus the redispatch this test proves happened.
            Assert.Equal(3, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1762 F1/F4: the failure the second-reader review found in the fix at #1634 (31cf3bf0) --
    // the block's INPUT set (cancellationRequestedExecutionIds) is not per-call the way its
    // !hostStopRequested GATE is. With no checkpoint (a fresh pump: the shape a second Ctrl-C, a
    // kill, a crash, or a closed terminal leaves behind, since
    // InFlightExecutionRegistry.RequestStopAsync's wind-down only gets a checkpoint saved past it on
    // a CLEAN return), a HostStop-origin CancellationRequested a PRIOR process journalled reads
    // exactly like an operator's on this fresh process, where hostStopRequested starts false.
    // Red-first: with the accumulation filter reverted to accumulate every CancellationRequested
    // unconditionally (31cf3bf0's shape, Origin ignored), this test fails --
    // `Assert.Equal() Failure: Values differ / Expected: Succeeded / Actual: Cancelled` -- confirmed
    // by actually running it against that reverted code before restoring the filter.
    // Also #1762 F5: ExhaustedUntil with a real RetryNotBefore on the failure, not the Retryable
    // shape the rest of this file's arms use, so this file's own name is honest for at least one arm.
    [Fact]
    public async Task Overdue_ExhaustedUntil_park_with_a_HostStop_origin_cancel_request_still_redispatches_on_a_fresh_pump()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var overdueRetryNotBefore = now.AddSeconds(-10);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1762a"),
                new WorkflowTemplateId("template-1762a"),
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
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            // The ledger a killed/crashed host-stop wind-down leaves behind: an ExhaustedUntil quota
            // park, already overdue, plus the HostStop-origin CancellationRequested
            // InFlightExecutionRegistry.RequestStopAsync journalled for it before the process died --
            // written directly, with NO checkpoint file at all, so the pump about to run reads this
            // from byte 0, exactly the fresh/full-replay shape F1 is about.
            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1762a"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(
                    firstAttempt, FailureClassification.ExhaustedUntil, "quota exhausted", overdueRetryNotBefore), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, firstAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
                await writerInit.AppendAsync(new FlowEvent.CancellationRequested(firstAttempt, CancellationOrigin.HostStop), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            // A brand-new pump call over this exact ledger -- no InFlightExecutionRegistry carrying
            // hostStopRequested state from whatever process wrote the HostStop line, and no
            // checkpoint file in roomDirectory, so this reads the entire ledger from byte 0.
            var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-1762a"),
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

            // Redispatched, not cancelled: a HostStop-authored cancel from a process that no longer
            // exists must not terminally settle a step RetryWithRevision could otherwise still reach.
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested c && c.ExecutionId == firstAttempt);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == firstAttempt);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1762 F4, the positive control for the test above: an Operator-origin cancel, same fresh-pump/
    // no-checkpoint shape, DOES still settle Cancelled -- proving the fix discriminates on Origin
    // rather than simply never honouring a replayed CancellationRequested at all.
    [Fact]
    public async Task Overdue_park_with_an_Operator_origin_cancel_request_settles_Cancelled_on_a_fresh_pump()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var overdueRetryNotBefore = now.AddSeconds(-10);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1762b"),
                new WorkflowTemplateId("template-1762b"),
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
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1762b"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, firstAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
                await writerInit.AppendAsync(new FlowEvent.CancellationRequested(firstAttempt, CancellationOrigin.Operator), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-1762b"),
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

            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == firstAttempt);
            // No new dispatch: the journalled Operator cancel won the race, exactly once.
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1762 F1: the pre-Origin wire shape (FlowEvent.CancellationRequested's own doc comment
    // explains the replay semantics; FlowEventSerializationTests pins the wire shape; spec/baton.md
    // §2 has why this is safe) -- the new block must not honour it either.
    [Fact]
    public async Task Overdue_park_with_a_legacy_null_origin_cancel_request_still_redispatches_on_a_fresh_pump()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var overdueRetryNotBefore = now.AddSeconds(-10);

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1762c"),
                new WorkflowTemplateId("template-1762c"),
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
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-a"] = new WorkerBinding.Process(
                    new WorkerContract("worker-a", [], [], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30))
            };

            var firstAttempt = new ExecutionId("a-1");
            await using (var writerInit = new FlowEventLogWriter(logPath))
            {
                var ct = TestContext.Current.CancellationToken;
                await writerInit.AppendAsync(new FlowEvent.ExecutionRequestAccepted(
                    new ExecutionRequest(firstAttempt, new WorkflowId("wf-1762c"), StepA, "worker-a", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>())), ct);
                await writerInit.AppendAsync(new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable, "boom"), ct);
                await writerInit.AppendAsync(new FlowEvent.StepRetryScheduled(StepA, firstAttempt, overdueRetryNotBefore, RetryDelayMs: 500), ct);
                // Origin omitted -- the pre-#1762 wire shape, not CancellationOrigin.Operator/HostStop.
                await writerInit.AppendAsync(new FlowEvent.CancellationRequested(firstAttempt), ct);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer, writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-1762c"),
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

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == StepA).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled c && c.ExecutionId == firstAttempt);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
