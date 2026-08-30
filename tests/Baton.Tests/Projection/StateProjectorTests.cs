using System.Text.Json;
using Baton.Domain;
using Baton.Projection;

namespace Baton.Tests.Projection;

public class StateProjectorTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static StepState StepFor(FlowState state, StepId stepId) => Assert.Single(state.Steps, s => s.StepId == stepId);

    private static ExecutionRequest MakeStepLessRequest(ExecutionId executionId, string worker = "human")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            StepId: null,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public void A_step_with_no_events_at_all_projects_as_Pending()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.All(state.Steps, s =>
        {
            Assert.Equal(StepStatus.Pending, s.Status);
            Assert.Null(s.LatestExecutionId);
        });
    }

    [Fact]
    public void An_accepted_request_with_no_terminal_event_yet_projects_as_Running()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Running, architect.Status);
        Assert.Equal(executionId, architect.LatestExecutionId);
    }

    [Fact]
    public void A_succeeded_execution_projects_as_Succeeded()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_failed_execution_projects_as_Failed()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Failed, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_cancelled_execution_projects_as_Cancelled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Cancelled, StepFor(state, Architect).Status);
    }

    [Fact]
    public void Only_the_most_recently_accepted_attempt_determines_a_steps_status()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Succeeded, architect.Status);
        Assert.Equal(secondAttempt, architect.LatestExecutionId);
    }

    [Fact]
    public void A_paused_execution_projects_as_Paused_even_though_it_already_reached_a_terminal_outcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Paused, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Resuming_a_paused_execution_reverts_it_to_its_underlying_terminal_status()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Rejecting_a_paused_execution_that_had_succeeded_projects_it_as_Rejected()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Rejected, StepFor(state, Critic).Status);
    }

    [Fact]
    public void Rejecting_a_paused_execution_that_had_failed_projects_it_as_Rejected_not_Failed()
    {
        // Equivalent in effect to exhausting RetryPolicy, but externally triggered:
        // Rejected is a distinct terminal status from Failed so the Retry Engine never reconsiders it.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Reject, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Rejected, StepFor(state, Critic).Status);
    }

    [Fact]
    public void A_paused_execution_reports_its_underlying_outcome_as_PausedOutcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Succeeded, StepFor(state, Critic).PausedOutcome);
    }

    [Fact]
    public void A_step_that_is_not_currently_paused_reports_a_null_PausedOutcome()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Null(StepFor(state, Critic).PausedOutcome);
    }

    [Fact]
    public void A_step_with_no_WorkflowPaused_ever_recorded_projects_PauseRecordedForLatestExecution_false()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Critic).PauseRecordedForLatestExecution);
    }

    [Fact]
    public void PauseRecordedForLatestExecution_stays_true_after_resume_even_though_Status_reverts()
    {
        // "One resolving decision per pause": Resume clears the transient Paused status,
        // but the fact that this exact ExecutionId was once paused must survive so the Pause Engine
        // never re-pauses it.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Critic)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var critic = StepFor(state, Critic);
        Assert.Equal(StepStatus.Succeeded, critic.Status);
        Assert.True(critic.PauseRecordedForLatestExecution);
    }

    [Fact]
    public void A_new_attempt_after_resume_starts_with_PauseRecordedForLatestExecution_false()
    {
        // A fresh ExecutionId (e.g. via RetryWithRevision/Supersede, landing in later
        // phases) has never itself been paused, regardless of the step's history.
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(firstAttempt),
            new FlowEvent.WorkflowPaused(firstAttempt, Critic),
            new FlowEvent.ExternalDecisionRecorded(decisionId, firstAttempt, DecisionType.Resume, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Critic)),
            new FlowEvent.ExecutionSucceeded(secondAttempt),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.False(StepFor(state, Critic).PauseRecordedForLatestExecution);
    }

    [Fact]
    public void An_all_pending_workflow_projects_WorkflowStatus_Running()
    {
        // Flipped by #810 (the pin carried no rationale): an empty journal on a started run means
        // "accepted, first dispatch imminent" — or crashed before it, which already reads as
        // in-flight. Terminal's contract is "nothing further to dispatch", and a root step with
        // satisfiable dependencies is exactly further-to-dispatch.
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_workflow_with_a_running_step_projects_WorkflowStatus_Running()
    {
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("exec-1"), Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_workflow_with_a_paused_step_and_nothing_running_projects_WorkflowStatus_Paused()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, Architect),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Paused, state.Status);
    }

    [Fact]
    public void Running_takes_priority_over_Paused_when_both_are_present()
    {
        var architectExecutionId = new ExecutionId("exec-1");
        var criticExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.WorkflowPaused(architectExecutionId, Architect),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_fully_succeeded_workflow_projects_WorkflowStatus_Terminal()
    {
        var architectExecutionId = new ExecutionId("exec-1");
        var criticExecutionId = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_rejected_request_never_having_been_accepted_leaves_the_step_Pending()
    {
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestRejected(new ExecutionId("exec-1"), "concurrency cap reached"),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Pending, architect.Status);
        Assert.Null(architect.LatestExecutionId);
    }

    [Fact]
    public void A_fail_fail_succeed_sequence_resets_the_consecutive_failure_count_to_zero()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var third = new ExecutionId("exec-3");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(third, Architect)),
            new FlowEvent.ExecutionSucceeded(third),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
        Assert.Null(architect.LatestFailureClassification);
    }

    [Fact]
    public void A_fail_fail_sequence_leaves_the_consecutive_failure_count_at_two()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Permanent),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(2, architect.ConsecutiveFailureCount);
        Assert.Equal(FailureClassification.Permanent, architect.LatestFailureClassification);
    }

    [Fact]
    public void A_step_with_no_events_projects_a_zero_consecutive_failure_count_and_null_classification()
    {
        var state = StateProjector.Project([], TwoStepSnapshot());

        Assert.All(state.Steps, s =>
        {
            Assert.Equal(0, s.ConsecutiveFailureCount);
            Assert.Null(s.LatestFailureClassification);
        });
    }

    [Fact]
    public void Causal_linking_for_failure_history_is_by_ExecutionId_not_line_position()
    {
        // Architect and Critic attempts interleave in the log; each step's failure count must
        // track only its own ExecutionIds, never be confused by append order across steps.
        var architectFirst = new ExecutionId("a-1");
        var criticFirst = new ExecutionId("c-1");
        var architectSecond = new ExecutionId("a-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectFirst, Architect)),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticFirst, Critic)),
            new FlowEvent.ExecutionFailed(architectFirst, FailureClassification.Retryable),
            new FlowEvent.ExecutionSucceeded(criticFirst),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectSecond, Architect)),
            new FlowEvent.ExecutionFailed(architectSecond, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(2, StepFor(state, Architect).ConsecutiveFailureCount);
        Assert.Equal(0, StepFor(state, Critic).ConsecutiveFailureCount);
        Assert.Null(StepFor(state, Critic).LatestFailureClassification);
    }

    [Fact]
    public void RetryWithRevision_resets_the_consecutive_failure_count_for_a_fresh_retry_round()
    {
        // The externally-triggered counterpart to a success resetting the budget (M8 Phase 1):
        // an exhausted step reopened via RetryWithRevision must not still read as exhausted.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
    }

    [Fact]
    public void An_ExhaustedUntil_failure_does_not_increment_the_consecutive_failure_count()
    {
        // Both directions of the projector's ExhaustedUntil counting rule (the why lives on the
        // ExecutionFailed case in StateProjector.cs): the quota hit leaves the count alone, the
        // ordinary failure after it still counts.
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(first, Architect)),
            new FlowEvent.ExecutionFailed(first, FailureClassification.ExhaustedUntil, "quota", DateTimeOffset.UnixEpoch),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(second, Architect)),
            new FlowEvent.ExecutionFailed(second, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(1, architect.ConsecutiveFailureCount);
        Assert.Equal(FailureClassification.Retryable, architect.LatestFailureClassification);
    }

    [Fact]
    public void RetryWithRevision_clears_the_latest_failure_classification_like_a_success_does()
    {
        // The reopen IS a fresh round (the count reset above already says so). Leaving the stale
        // classification behind let a reopened quota-failed step re-pace itself to the old vendor
        // reset moment instead of honoring the operator's explicit retry-now. The log replayed
        // here is also what a pre-#594 engine could legitimately have written (a quota step that
        // auto-paused), so this doubles as replay compatibility for that history.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", DateTimeOffset.UnixEpoch),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Null(architect.LatestFailureClassification);
        Assert.Null(architect.LatestFailureReason);
        Assert.Null(architect.LatestExecutionFailedRetryNotBefore);
    }

    /// <summary>
    /// #815: the same clearing as above, but reached via the widened validator path — a quota-
    /// parked step that was never paused (no <see cref="FlowEvent.WorkflowPaused"/> in this log at
    /// all), which a lane workflow's missing <see cref="WorkflowStepDefinition.PausePoint"/> makes
    /// the ordinary case. <see cref="MutationInterface"/> always appends
    /// <see cref="FlowEvent.WorkflowResumed"/> after recording a decision regardless of whether a
    /// pause occurred, and this projector case already keys off the decision's
    /// <see cref="FlowEvent.ExternalDecisionRecorded.ReferencedExecutionId"/> rather than Paused
    /// status — so no projector change was needed for #815, only proven here.
    /// </summary>
    [Fact]
    public void RetryWithRevision_clears_the_classification_for_a_quota_parked_step_with_no_prior_pause()
    {
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var retryNotBefore = DateTimeOffset.UnixEpoch.AddHours(1);
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", retryNotBefore),
            new FlowEvent.StepRetryScheduled(Architect, executionId, retryNotBefore, RetryDelayMs: 60_000),
            new FlowEvent.ExternalDecisionRecorded(decisionId, executionId, DecisionType.RetryWithRevision, null, null),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.Equal(StepStatus.Failed, architect.Status);
        Assert.Equal(0, architect.ConsecutiveFailureCount);
        Assert.Null(architect.LatestFailureClassification);
        Assert.Null(architect.LatestFailureReason);
        Assert.Null(architect.LatestExecutionFailedRetryNotBefore);
        Assert.Null(architect.RetryNotBefore);
        Assert.Null(architect.RetryDelayMs);
    }

    [Fact]
    public void RetryWithRevision_with_a_SupplementaryExecutionId_projects_it_as_pending_for_the_referenced_step()
    {
        var executionId = new ExecutionId("exec-1");
        var supplementaryExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(executionId, Architect),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, executionId, DecisionType.RetryWithRevision, null, supplementaryExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(supplementaryExecutionId, StepFor(state, Architect).PendingSupplementaryExecutionId);
    }

    [Fact]
    public void A_newer_ExecutionRequestAccepted_clears_the_pending_supplementary_fact_for_that_step()
    {
        var firstAttempt = new ExecutionId("exec-1");
        var secondAttempt = new ExecutionId("exec-2");
        var supplementaryExecutionId = new ExecutionId("supplement-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstAttempt, Architect)),
            new FlowEvent.ExecutionFailed(firstAttempt, FailureClassification.Retryable),
            new FlowEvent.WorkflowPaused(firstAttempt, Architect),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, firstAttempt, DecisionType.RetryWithRevision, null, supplementaryExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondAttempt, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Null(StepFor(state, Architect).PendingSupplementaryExecutionId);
    }

    [Fact]
    public void Supersede_marks_its_TargetStepId_as_a_pending_Supersede_target_with_a_pending_supplement()
    {
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, criticExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.True(architect.IsPendingSupersedeTarget);
        Assert.Equal(criticExecutionId, architect.PendingSupplementaryExecutionId);

        // Critic itself carries no pending fact — it is the decision's referent, not its target.
        Assert.False(StepFor(state, Critic).IsPendingSupersedeTarget);
    }

    [Fact]
    public void A_newer_ExecutionRequestAccepted_for_the_target_clears_IsPendingSupersedeTarget()
    {
        var architectFirstAttempt = new ExecutionId("a-1");
        var architectSecondAttempt = new ExecutionId("a-2");
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectFirstAttempt, Architect)),
            new FlowEvent.ExecutionSucceeded(architectFirstAttempt),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, Architect, criticExecutionId),
            new FlowEvent.WorkflowResumed(decisionId),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectSecondAttempt, Architect)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var architect = StepFor(state, Architect);
        Assert.False(architect.IsPendingSupersedeTarget);
        Assert.Null(architect.PendingSupplementaryExecutionId);
    }

    [Fact]
    public void Projecting_the_same_events_twice_produces_an_identical_result()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };
        var snapshot = TwoStepSnapshot();

        var first = StateProjector.Project(events, snapshot);
        var second = StateProjector.Project(events, snapshot);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void A_step_less_ExecutionRequestAccepted_with_no_terminal_event_is_a_pending_StepLessExecution()
    {
        var executionId = new ExecutionId("supplement-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(executionId)),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var stepLess = Assert.Single(state.StepLessExecutions);
        Assert.Equal(executionId, stepLess.ExecutionId);
        Assert.Equal("human", stepLess.Worker);

        // Never perturbs any step's own projection — a step-less execution belongs to no StepId.
        Assert.Equal(StepStatus.Pending, StepFor(state, Architect).Status);
        Assert.Equal(StepStatus.Pending, StepFor(state, Critic).Status);
    }

    [Fact]
    public void A_settled_step_less_execution_is_no_longer_a_pending_StepLessExecution()
    {
        var executionId = new ExecutionId("supplement-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(executionId)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Empty(state.StepLessExecutions);
    }

    [Fact]
    public void Multiple_pending_step_less_executions_are_tracked_independently_in_append_order()
    {
        var first = new ExecutionId("supplement-1");
        var second = new ExecutionId("supplement-2");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(first)),
            new FlowEvent.ExecutionRequestAccepted(MakeStepLessRequest(second)),
            new FlowEvent.ExecutionSucceeded(first),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        var pending = Assert.Single(state.StepLessExecutions);
        Assert.Equal(second, pending.ExecutionId);
    }

    [Fact]
    public void A_CancellationRequested_with_no_terminal_event_yet_is_unfulfilled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal([executionId], state.CancellationRequestedExecutionIds);
        // Mid-execution, not an outcome — the step itself stays Running until a terminal event.
        Assert.Equal(StepStatus.Running, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_CancellationRequested_against_an_already_terminal_execution_is_never_unfulfilled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.CancellationRequested(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        // The too-late request: recorded, but never surfaced as still owed.
        Assert.Empty(state.CancellationRequestedExecutionIds);
        Assert.Equal(StepStatus.Succeeded, StepFor(state, Architect).Status);
    }

    [Fact]
    public void A_CancellationRequested_is_no_longer_unfulfilled_once_a_terminal_event_lands()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.CancellationRequested(executionId),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Empty(state.CancellationRequestedExecutionIds);
        Assert.Equal(StepStatus.Cancelled, StepFor(state, Architect).Status);
    }

    // #810: these pin the phantom-Terminal windows (the why and the live capture live on
    // StateProjector.DeriveWorkflowStatus's doc) plus the polarities that keep genuinely-dead
    // workflows Terminal.

    [Fact]
    public void A_workflow_between_steps_is_Running_not_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionSucceeded(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Pending, StepFor(state, Critic).Status);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_failed_step_with_retry_budget_keeps_the_workflow_Running_before_its_retry_is_scheduled()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, ThreeAttemptSnapshot());

        Assert.Null(StepFor(state, Architect).RetryNotBefore);
        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void An_ExhaustedUntil_step_keeps_the_workflow_Running_never_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota", DateTimeOffset.UnixEpoch),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Running, state.Status);
    }

    [Fact]
    public void A_retry_eligible_step_below_a_dead_chain_is_dead_too_and_the_workflow_Terminal()
    {
        // The #810 review's high finding, reconstructed: Critic fails with budget left (MayRetry
        // true), then a Supersede reopens Architect and its fresh attempt fails permanently.
        // Critic is locally retry-eligible forever, but DependencyResolver will never dispatch it
        // (its dependency is not Succeeded) — so local eligibility without the dependency cascade
        // read this workflow as Running for eternity. It is Terminal: nothing can ever dispatch.
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
            new FlowEvent.ExecutionFailed(new ExecutionId("c-1"), FailureClassification.Retryable),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("o-1"), Observer)),
            new FlowEvent.ExecutionSucceeded(new ExecutionId("o-1")),
            new FlowEvent.WorkflowPaused(new ExecutionId("o-1"), Observer),
            new FlowEvent.ExternalDecisionRecorded(
                new DecisionId("decision-1"), new ExecutionId("o-1"), DecisionType.Supersede, Architect, new ExecutionId("o-1")),
            new FlowEvent.WorkflowResumed(new DecisionId("decision-1")),
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-2"), Architect)),
            new FlowEvent.ExecutionFailed(new ExecutionId("a-2"), FailureClassification.Permanent),
        };

        var state = StateProjector.Project(events, ThreeStepPausingSnapshot());

        var critic = StepFor(state, Critic);
        Assert.Equal(StepStatus.Failed, critic.Status);
        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_pending_step_below_a_budget_exhausted_failure_is_dead_and_the_workflow_Terminal()
    {
        // The polarity that keeps this fix honest: Pending alone must NOT mean Running. Architect
        // fails with no budget left (MaxAttempts 1) and no retry eligibility; Critic can never
        // receive its inputs, so the workflow genuinely has nothing further to dispatch.
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(StepStatus.Pending, StepFor(state, Critic).Status);
        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    [Fact]
    public void A_pending_step_below_a_cancelled_step_is_dead_and_the_workflow_Terminal()
    {
        var executionId = new ExecutionId("exec-1");
        var events = new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
            new FlowEvent.ExecutionCancelled(executionId),
        };

        var state = StateProjector.Project(events, TwoStepSnapshot());

        Assert.Equal(WorkflowStatus.Terminal, state.Status);
    }

    private static readonly StepId Observer = new("observer");

    // Critic carries retry budget (the local-eligibility arm under test); Observer is an
    // independent step whose pause hosts the Supersede decision.
    private static WorkflowDefinitionSnapshot ThreeStepPausingSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-3p"),
        new WorkflowTemplateId("architect-critic-observer"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(Observer, "observer", ["goal"], ["notes"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static WorkflowDefinitionSnapshot ThreeAttemptSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-3a"),
        new WorkflowTemplateId("architect-critic-retrying"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(3)),
        ]);
}
