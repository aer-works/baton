using Baton.Flow.Domain;

namespace Baton.Flow.Scheduling;

/// <summary>
/// Determines which steps are ready to run per the Dependency Resolution Rule. A pure
/// function over <see cref="FlowState"/> and <see cref="WorkflowDefinitionSnapshot"/> — no I/O, no
/// dispatch, no retries.
/// </summary>
public static class DependencyResolver
{
    /// <summary>
    /// Returns the <see cref="StepId"/>s that are ready to run: for every <see cref="StepId"/> a
    /// step <c>DependsOn</c>, that dependency's most recent attempt succeeded (condition 1), and
    /// this step does not already have a successful execution that used the dependency's current
    /// most recent successful <see cref="ExecutionId"/> (condition 2 — staleness after
    /// <see cref="DecisionType.Supersede"/>). A step whose latest attempt failed is also
    /// ready when <see cref="RetryEngine.MayRetry"/> holds for it — "terminally failed" is
    /// the derived complement (<see cref="StepStatus.Failed"/> and not <c>MayRetry</c>), never a
    /// stored event.
    /// </summary>
    // `now` is required rather than defaulted, and that is a measured hazard, not style: a
    // defaulted DateTimeOffset is year one, which puts every deferral deadline ~2000 years in the
    // future -- further away than any delay -- so the skew clamp below reads the omission as a
    // backwards clock jump and releases the step immediately. A caller that forgot the clock would
    // silently reinstate the zero-delay retry #712 exists to end.
    public static IReadOnlySet<StepId> GetReadySteps(FlowState state, WorkflowDefinitionSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        var stepStateByStepId = state.Steps.ToDictionary(step => step.StepId);
        var readyStepIds = new HashSet<StepId>();

        foreach (var stepDefinition in snapshot.Steps)
        {
            var stepState = stepStateByStepId[stepDefinition.StepId];

            // A Supersede's target is already Succeeded and therefore never "ready" through the
            // ordinary conditions below — it is ready as the decision's direct consequence instead
            // independent of its own dependencies' staleness. Once dispatched, the next
            // projection clears this and the step falls back into ordinary readiness.
            if (stepState.IsPendingSupersedeTarget)
            {
                readyStepIds.Add(stepDefinition.StepId);
                continue;
            }

            // Running: an attempt is already in flight. Paused: idle until an external decision
            // resolves it. Rejected: an external Reject forecloses retry regardless of
            // remaining budget, equivalent in effect to exhausting RetryPolicy but externally
            // triggered.
            if (stepState.Status is StepStatus.Running or StepStatus.Paused or StepStatus.Rejected)
            {
                continue;
            }

            // Cancelled is never retried by RetryPolicy — but a RetryWithRevision
            // decision reopens "the referenced step, which has not yet succeeded"
            // regardless of whether that step's terminal outcome was Cancelled or Failed, and a
            // pending PendingSupplementaryExecutionId is recorded only in exactly that case (StateProjector
            // also resets ConsecutiveFailureCount for the same decision, mirroring how Failed's own
            // MayRetry check below is bypassed by remaining RetryPolicy budget rather than an
            // external decision). Falls through to the ordinary readiness check below rather than
            // bypassing it outright, unlike a Supersede target: a reopened Cancelled step's own
            // dependencies can still legitimately have gone stale.
            if (stepState.Status == StepStatus.Cancelled && stepState.PendingSupplementaryExecutionId is null)
            {
                continue;
            }

            // A failed step stays terminal unless its RetryPolicy still permits another attempt;
            // one that does proceeds into the same readiness check as any other step.
            if (stepState.Status == StepStatus.Failed && !RetryEngine.MayRetry(stepState, stepDefinition.RetryPolicy))
            {
                continue;
            }

            // 0026 §4/§5: an ExhaustedUntil step whose retry obligation was not scheduled
            // (e.g. an attended interactive turn with settleOnVendorExhaustion=true, or an unknown reset instant)
            // has no scheduled retry for this execution and is not ready for retry.
            if (stepState.Status == StepStatus.Failed &&
                stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil &&
                stepState.RetryScheduledForExecutionId != stepState.LatestExecutionId)
            {
                continue;
            }

            if (stepState.RetryNotBefore is { } notBefore && stepState.RetryDelayMs is { } delayMs)
            {
                var remaining = notBefore - now;
                var maxDelay = TimeSpan.FromMilliseconds(delayMs);
                var isReadyByTime = now >= notBefore || remaining > maxDelay;
                if (!isReadyByTime)
                {
                    continue;
                }
            }

            if (IsReady(stepDefinition, stepState, stepStateByStepId))
            {
                readyStepIds.Add(stepDefinition.StepId);
            }
        }

        return readyStepIds;
    }

    private static bool IsReady(
        WorkflowStepDefinition stepDefinition,
        StepState stepState,
        Dictionary<StepId, StepState> stepStateByStepId)
    {
        // Condition 2 only ever blocks re-readiness by comparing against a dependency that could
        // have gone stale. A step with no DependsOn has nothing to go stale against, so
        // the loop below would never run and an already-succeeded root step would be vacuously
        // "ready" on every single projection — an infinite re-run, not a one-time completion.
        if (stepState.Status == StepStatus.Succeeded && stepDefinition.DependsOn.Count == 0)
        {
            return false;
        }

        foreach (var dependencyStepId in stepDefinition.DependsOn)
        {
            var dependencyState = stepStateByStepId[dependencyStepId];

            // Condition 1: the dependency's most recent attempt must have succeeded.
            if (dependencyState.Status != StepStatus.Succeeded)
            {
                return false;
            }

            // Condition 2: only relevant once this step has already succeeded — otherwise
            // there is no prior successful execution to compare staleness against. If this step's
            // recorded upstream for this dependency still matches the dependency's current latest
            // successful ExecutionId, this step is up to date with respect to it and is not ready
            // again; a mismatch (or no recorded entry) means it is stale and must rerun.
            if (stepState.Status == StepStatus.Succeeded &&
                stepState.UpstreamExecutionIds.TryGetValue(dependencyStepId, out var recordedUpstreamExecutionId) &&
                recordedUpstreamExecutionId == dependencyState.LatestExecutionId)
            {
                return false;
            }
        }

        return true;
    }
}
