using Baton.Domain;
using Baton.Scheduling;

namespace Baton.Tests.Scheduling;

public class DependencyResolverTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    // Any fixed instant works for tests with no deferral in play; `now` is a required parameter
    // because a defaulted one silently releases every deferral -- the reasoning lives on
    // GetReadySteps itself.
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowDefinitionSnapshot TwoStepSnapshot(int architectMaxAttempts = 1) => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(architectMaxAttempts)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static StepState Pending(StepId stepId) => new(stepId, StepStatus.Pending, LatestExecutionId: null, NoUpstream);

    private static StepState Terminal(StepId stepId, StepStatus status, ExecutionId executionId) =>
        new(stepId, status, executionId, NoUpstream);

    private static StepState Failed(StepId stepId, ExecutionId executionId, int consecutiveFailureCount, FailureClassification? classification = null) =>
        new(stepId, StepStatus.Failed, executionId, NoUpstream, consecutiveFailureCount, classification);

    private static StepState Deferred(StepId stepId, ExecutionId executionId, DateTimeOffset notBefore, int delayMs) =>
        new(stepId, StepStatus.Failed, executionId, NoUpstream, ConsecutiveFailureCount: 1,
            RetryNotBefore: notBefore, RetryDelayMs: delayMs);

    // #712's readiness clamp, pinned at the resolver directly: before the deadline the step is not
    // ready, at it the step is, and a deadline further away than the delay that produced it -- only
    // reachable via a backwards clock jump -- releases rather than strands.

    [Fact]
    public void A_deferred_step_is_not_ready_before_its_deadline()
    {
        var state = new FlowState(new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Deferred(Architect, new ExecutionId("e1"), Now.AddSeconds(30), delayMs: 30_000), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 2), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_deferred_step_is_ready_at_its_deadline()
    {
        var state = new FlowState(new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Deferred(Architect, new ExecutionId("e1"), Now, delayMs: 30_000), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 2), Now);

        Assert.Contains(Architect, ready);
    }

    [Fact]
    public void A_backwards_clock_jump_releases_a_deferred_step_instead_of_stranding_it()
    {
        // The deadline sits 60 s out while the delay that produced it was 30 s -- impossible unless
        // the clock moved backwards after scheduling, so the clamp treats the step as ready.
        var state = new FlowState(new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Deferred(Architect, new ExecutionId("e1"), Now.AddSeconds(60), delayMs: 30_000), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 2), Now);

        Assert.Contains(Architect, ready);
    }

    private static StepState SucceededUsing(StepId stepId, ExecutionId executionId, StepId dependencyStepId, ExecutionId upstreamExecutionId) =>
        new(stepId, StepStatus.Succeeded, executionId, new Dictionary<StepId, ExecutionId> { [dependencyStepId] = upstreamExecutionId });

    [Fact]
    public void A_step_with_no_dependencies_is_immediately_ready()
    {
        var state = new FlowState(new WorkflowDefinitionSnapshotId("snapshot-1"), [Pending(Architect), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.Contains(Architect, ready);
    }

    [Fact]
    public void A_step_whose_dependency_succeeded_becomes_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.Contains(Critic, ready);
    }

    [Fact]
    public void A_step_whose_dependency_failed_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Failed, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Critic, ready);
    }

    [Fact]
    public void A_step_already_succeeded_with_upstream_still_current_is_not_re_queued()
    {
        var architectExecutionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Terminal(Architect, StepStatus.Succeeded, architectExecutionId),
                SucceededUsing(Critic, new ExecutionId("C1"), Architect, architectExecutionId),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Critic, ready);
    }

    [Fact]
    public void A_step_already_succeeded_whose_dependency_was_since_superseded_becomes_ready_again()
    {
        // Architect's original success was A1; Critic ran against A1. Architect was
        // superseded and now has a newer success A2 — Critic's recorded upstream (A1) no longer
        // matches Architect's current latest success, so Critic is stale and ready to rerun.
        var supersededArchitectExecutionId = new ExecutionId("A1");
        var currentArchitectExecutionId = new ExecutionId("A2");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Terminal(Architect, StepStatus.Succeeded, currentArchitectExecutionId),
                SucceededUsing(Critic, new ExecutionId("C1"), Architect, supersededArchitectExecutionId),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.Contains(Critic, ready);
    }

    [Fact]
    public void A_step_with_an_execution_already_running_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Running, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_paused_step_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Paused, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_succeeded_step_with_no_dependencies_is_not_re_queued()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_failed_step_with_retry_budget_remaining_becomes_ready_again()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Failed(Architect, new ExecutionId("A1"), consecutiveFailureCount: 1), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 2), Now);

        Assert.Contains(Architect, ready);
    }

    [Fact]
    public void A_failed_step_with_an_exhausted_retry_budget_is_not_re_queued()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Failed(Architect, new ExecutionId("A1"), consecutiveFailureCount: 2), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 2), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_failed_step_classified_Permanent_is_not_re_queued_regardless_of_remaining_budget()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Failed(Architect, new ExecutionId("A1"), consecutiveFailureCount: 0, FailureClassification.Permanent), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 5), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_cancelled_step_is_never_re_queued_regardless_of_retry_policy()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Cancelled, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 5), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_rejected_step_is_never_re_queued_regardless_of_retry_policy()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Rejected, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 5), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void A_step_whose_dependency_was_rejected_is_not_ready()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Rejected, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Critic, ready);
    }

    [Fact]
    public void A_retried_steps_downstream_stays_blocked_until_the_retry_succeeds()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Failed(Architect, new ExecutionId("A1"), consecutiveFailureCount: 1), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(architectMaxAttempts: 2), Now);

        Assert.DoesNotContain(Critic, ready);
    }

    [Fact]
    public void A_pending_Supersede_target_is_ready_even_though_it_already_succeeded_with_no_stale_dependency()
    {
        // Architect has no DependsOn, so nothing about it could ever be "stale" via condition 2 —
        // the only reason it is ready again is IsPendingSupersedeTarget (Supersede's direct consequence).
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                new StepState(Architect, StepStatus.Succeeded, new ExecutionId("A1"), NoUpstream, IsPendingSupersedeTarget: true),
                Pending(Critic),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.Contains(Architect, ready);
    }

    [Fact]
    public void A_step_that_already_succeeded_and_is_not_a_pending_Supersede_target_is_not_ready_again()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, new ExecutionId("A1")), Pending(Critic)]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    // #1187: withholding the retry obligation is NOT by itself what stops an exhausted step from
    // running again. Readiness never consulted the obligation: an ExhaustedUntil step bypasses the
    // attempts check (0026, RetryEngine.MayRetry) and carries no RetryNotBefore when no obligation
    // was scheduled, so it fell through both guards above and came back ready on the very next pump
    // round -- the immediate re-dispatch loop #1119 believed it had removed by dropping the
    // obligation, and the one #1184's attended settle would otherwise inherit. Both arms that leave
    // an obligation unscheduled are pinned here; the paced-park arm below is the control that shows
    // the fix does not strand the ordinary 0026 §5 wait.
    [Fact]
    public void An_exhausted_step_with_no_scheduled_retry_is_not_ready_to_run_again()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                new StepState(
                    Architect, StepStatus.Failed, new ExecutionId("A1"), NoUpstream,
                    ConsecutiveFailureCount: 0,
                    LatestFailureClassification: FailureClassification.ExhaustedUntil),
                Pending(Critic),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Architect, ready);
    }

    [Fact]
    public void An_exhausted_step_whose_paced_retry_was_scheduled_and_has_come_due_is_ready()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                new StepState(
                    Architect, StepStatus.Failed, executionId, NoUpstream,
                    ConsecutiveFailureCount: 0,
                    LatestFailureClassification: FailureClassification.ExhaustedUntil,
                    RetryNotBefore: Now.AddMinutes(-1),
                    RetryDelayMs: 60_000,
                    RetryScheduledForExecutionId: executionId),
                Pending(Critic),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.Contains(Architect, ready);
    }

    // #1586 S1: the named trap from the ratified state-truth design (see StateProjectorTests' own
    // StepRetryForeclosed fixtures for the fuller citation) -- the exact fixture above, but with
    // StepState.RetryForeclosed set, is the red test that must NOT come back ready.
    // Both polarities from one shared shape: the fixture two tests up is the "false" control (still
    // ready without foreclosure), this is the "true" arm.
    [Fact]
    public void A_foreclosed_ExhaustedUntil_step_is_not_ready_even_though_its_paced_retry_has_come_due()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                new StepState(
                    Architect, StepStatus.Failed, executionId, NoUpstream,
                    ConsecutiveFailureCount: 0,
                    LatestFailureClassification: FailureClassification.ExhaustedUntil,
                    RetryNotBefore: Now.AddMinutes(-1),
                    RetryDelayMs: 60_000,
                    RetryScheduledForExecutionId: executionId,
                    RetryForeclosed: true),
                Pending(Critic),
            ]);

        var ready = DependencyResolver.GetReadySteps(state, TwoStepSnapshot(), Now);

        Assert.DoesNotContain(Architect, ready);
    }
}
