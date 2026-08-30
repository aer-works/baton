using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// Capability 14's validation half: a pure function — no I/O, no dispatch — deciding
/// whether a candidate <see cref="FlowEvent.ExternalDecisionRecorded"/> is admissible against
/// projected <see cref="FlowState"/>. Every <see cref="DecisionType"/> is validated here, even
/// though <see cref="DecisionType.RetryWithRevision"/>/<see cref="DecisionType.Supersede"/>
/// consequences land in a later phase — an invalid decision is rejected before anything is
/// appended, never silently widened.
/// </summary>
public static class ExternalDecisionValidator
{
    /// <exception cref="InvalidExternalDecisionException">The decision violates one of the validation rules.</exception>
    public static void Validate(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlySet<ExecutionId> succeededExecutionIds,
        ExecutionId referencedExecutionId,
        DecisionType decisionType,
        StepId? targetStepId,
        ExecutionId? supplementaryExecutionId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(succeededExecutionIds);

        // "One resolving decision per pause" needs no separate check: once a prior decision
        // has resolved this ExecutionId, WorkflowResumed has already cleared its Paused status, so a
        // further decision against it fails this same lookup.
        var referencedStep = state.Steps.SingleOrDefault(step => step.LatestExecutionId == referencedExecutionId);

        // #815: RetryWithRevision (only) also reaches a step #594's classification quota-parked —
        // Failed, with a FlowEvent.StepRetryScheduled already recorded (RetryNotBefore set) — even
        // though it was never paused; a workflow declares no PausePoint at all, so nothing
        // else can reach it. Reuses #594's existing classification-clearing consequence
        // (StateProjector's WorkflowResumed/RetryWithRevision handling already looks the step up by
        // ReferencedExecutionId, not by Paused status, so no change is needed there). Every other
        // DecisionType, and a Failed step with no scheduled retry, still requires the Paused gate.
        var isQuotaParkedRetryNow = decisionType == DecisionType.RetryWithRevision
            && referencedStep is { Status: StepStatus.Failed, RetryNotBefore: not null };

        if (referencedStep is null || (referencedStep.Status != StepStatus.Paused && !isQuotaParkedRetryNow))
        {
            throw new InvalidExternalDecisionException(
                $"Execution '{referencedExecutionId}' is not the currently paused latest attempt of any step.");
        }

        // Every Paused step was paused by the Pause Engine only for a step declaring PausePoint —
        // a Flow-internal invariant, not something a caller's input can violate.
        var pausePoint = snapshot.Steps.Single(step => step.StepId == referencedStep.StepId).PausePoint!;

        switch (decisionType)
        {
            case DecisionType.Resume:
                RequireNoTargetStepId(decisionType, targetStepId);
                break;

            case DecisionType.Reject:
                RequireNoTargetStepId(decisionType, targetStepId);
                break;

            case DecisionType.RetryWithRevision:
                RequireNoTargetStepId(decisionType, targetStepId);
                if (referencedStep.PausedOutcome == StepStatus.Succeeded)
                {
                    throw new InvalidExternalDecisionException(
                        $"RetryWithRevision requires step '{referencedStep.StepId}' to not have already succeeded.");
                }

                if (supplementaryExecutionId is { } retrySupplement && !succeededExecutionIds.Contains(retrySupplement))
                {
                    throw new InvalidExternalDecisionException(
                        $"SupplementaryExecutionId '{retrySupplement}' does not name a recorded successful execution.");
                }

                break;

            case DecisionType.Supersede:
                if (targetStepId is not { } target)
                {
                    throw new InvalidExternalDecisionException("Supersede decisions require a TargetStepId.");
                }

                if (!pausePoint.SupersedeTargets.Contains(target))
                {
                    throw new InvalidExternalDecisionException(
                        $"'{target}' is not a declared SupersedeTargets entry for step '{referencedStep.StepId}'.");
                }

                var targetState = state.Steps.Single(step => step.StepId == target);

                // M23 Phase 2 (#271): a prior Supersede already named this target and its
                // consequence (the re-dispatch StateProjector derives from WorkflowResumed) has not
                // landed yet — StepState.IsPendingSupersedeTarget stays true, and the target's
                // Status still reads the stale pre-Supersede Succeeded (StateProjector only updates
                // it once a fresh ExecutionRequestAccepted is recorded), so the Status check below
                // alone would not catch this. A second Supersede admitted here would silently clobber
                // the first's StepState.PendingSupplementaryExecutionId — StateProjector's
                // pendingSupplementaryExecutionIdByStepId is a plain last-write-wins assignment keyed
                // by StepId — losing the first decision's supplement entirely. Rejecting here instead
                // is a chain, not a hygiene nicety: this is the exact race a crash between recording
                // WorkflowResumed and the pump's re-dispatch reopens, since the concurrency guard
                // alone cannot rule it out across a restart. A second Supersede against the *same*
                // target is legal once this cycle's consequence has actually settled (Status back to
                // Succeeded, IsPendingSupersedeTarget false) — the repeated-supersede chain M24's chat
                // primitive depends on.
                if (targetState.IsPendingSupersedeTarget)
                {
                    throw new InvalidExternalDecisionException(
                        $"Supersede target '{target}' already has a pending Supersede consequence that has not been dispatched yet.");
                }

                if (targetState.Status != StepStatus.Succeeded)
                {
                    throw new InvalidExternalDecisionException($"Supersede target '{target}' has not succeeded.");
                }

                if (supplementaryExecutionId is not { } supersedeSupplement)
                {
                    throw new InvalidExternalDecisionException("Supersede decisions require a SupplementaryExecutionId.");
                }

                if (!succeededExecutionIds.Contains(supersedeSupplement))
                {
                    throw new InvalidExternalDecisionException(
                        $"SupplementaryExecutionId '{supersedeSupplement}' does not name a recorded successful execution.");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(decisionType), decisionType, "Unknown DecisionType.");
        }
    }

    private static void RequireNoTargetStepId(DecisionType decisionType, StepId? targetStepId)
    {
        if (targetStepId is not null)
        {
            throw new InvalidExternalDecisionException($"TargetStepId is only valid for {DecisionType.Supersede} decisions, not {decisionType}.");
        }
    }
}
