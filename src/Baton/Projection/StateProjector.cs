using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// Reconstructs <see cref="FlowState"/> from event history:
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c>. A pure function — no I/O, no
/// wall-clock time, no live process state — so identical inputs always produce an identical
/// result. Supports incremental projection via <see cref="ProjectionCheckpoint"/> (#903 Scope 1).
/// </summary>
public static class StateProjector
{
    /// <summary>
    /// Projects <paramref name="events"/> — read linearly, in append order, from Flow's half of the
    /// Event Store — against <paramref name="snapshot"/> into a <see cref="FlowState"/>.
    /// If an optional <paramref name="checkpoint"/> is provided and valid, replays only events past
    /// <see cref="ProjectionCheckpoint.EventOffset"/>, returning the updated projected state.
    /// </summary>
    public static FlowState Project(
        IReadOnlyList<FlowEvent> events,
        WorkflowDefinitionSnapshot snapshot,
        ProjectionCheckpoint? checkpoint = null)
    {
        return ProjectAndCheckpoint(events, snapshot, checkpoint).State;
    }

    /// <summary>
    /// Projects <paramref name="events"/> against <paramref name="snapshot"/>, returning both the projected
    /// <see cref="FlowState"/> and a fresh <see cref="ProjectionCheckpoint"/> capturing the state at <paramref name="events"/>.Count.
    /// </summary>
    public static (FlowState State, ProjectionCheckpoint Checkpoint) ProjectAndCheckpoint(
        IReadOnlyList<FlowEvent> events,
        WorkflowDefinitionSnapshot snapshot,
        ProjectionCheckpoint? checkpoint = null,
        long logByteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(snapshot);

        int skipCount = 0;
        long totalEventOffset = 0;
        ProjectionCheckpointState state;

        if (checkpoint is not null)
        {
            if (checkpoint.EventOffset < 0 || (logByteOffset == 0 && checkpoint.EventOffset > events.Count))
            {
                Console.Error.WriteLine(
                    $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint EventOffset ({checkpoint.EventOffset}) exceeds log event count ({events.Count}) or is invalid.");
                state = ProjectionCheckpointState.CreateEmpty();
                skipCount = 0;
                totalEventOffset = events.Count;
            }
            else if (logByteOffset == 0)
            {
                // Full event list supplied
                state = checkpoint.State.DeepCopy();
                skipCount = (int)checkpoint.EventOffset;
                totalEventOffset = events.Count;
            }
            else
            {
                // Tail-only event list supplied
                state = checkpoint.State.DeepCopy();
                skipCount = 0;
                totalEventOffset = checkpoint.EventOffset + events.Count;
            }
        }
        else
        {
            state = ProjectionCheckpointState.CreateEmpty();
            skipCount = 0;
            totalEventOffset = events.Count;
        }

        for (int i = skipCount; i < events.Count; i++)
        {
            ApplyEvent(events[i], state);
        }

        var flowState = DeriveFlowState(state, snapshot);
        var finalByteOffset = logByteOffset > 0 ? logByteOffset : (checkpoint?.ByteOffset ?? 0);
        var newCheckpoint = new ProjectionCheckpoint(totalEventOffset, state.DeepCopy(), finalByteOffset, Version: 3);
        return (flowState, newCheckpoint);
    }

    private static void ApplyEvent(FlowEvent flowEvent, ProjectionCheckpointState state)
    {
        switch (flowEvent)
        {
            case FlowEvent.ExecutionRequestAccepted accepted:
                state.AcceptedRequestByExecutionId[accepted.Request.ExecutionId] = accepted.Request;
                if (accepted.Request.StepId is { } acceptedStepId)
                {
                    state.LatestExecutionIdByStepId[acceptedStepId] = accepted.Request.ExecutionId;
                    state.UpstreamExecutionIdsByStepId[acceptedStepId] = new Dictionary<StepId, ExecutionId>(accepted.Request.UpstreamExecutionIds);
                    state.StepIdByExecutionId[accepted.Request.ExecutionId] = acceptedStepId;
                    state.ExecutionCountByStepId[acceptedStepId] =
                        state.ExecutionCountByStepId.GetValueOrDefault(acceptedStepId) + 1;

                    // This dispatch is the consequence a prior decision was owed, if any — fulfilled now.
                    state.PendingSupplementaryExecutionIdByStepId.Remove(acceptedStepId);
                    state.PendingSupersedeTargetStepIds.Remove(acceptedStepId);
                    state.RetryNotBeforeByStepId.Remove(acceptedStepId);
                    state.RetryDelayMsByStepId.Remove(acceptedStepId);
                    state.RetryScheduledForExecutionIdByStepId.Remove(acceptedStepId);

                    // #1586 S1: a fresh dispatch reopens a foreclosed step — a foreclosure blocks
                    // MayRetry, not admission, and this is the same "the pump is dispatching it, so
                    // whatever blocked it is moot" reasoning the three clears above already rest on.
                    // Never permanent, per the state-truth design's own ruling on #1586.
                    state.RetryForeclosedStepIds.Remove(acceptedStepId);
                }
                else
                {
                    state.StepLessExecutionsInOrder.Add(new StepLessExecutionState(accepted.Request.ExecutionId, accepted.Request.Worker));
                }

                break;

            case FlowEvent.ExecutionSucceeded succeeded:
                state.SucceededExecutionIds.Add(succeeded.ExecutionId);
                state.TerminalStatusByExecutionId[succeeded.ExecutionId] = StepStatus.Succeeded;
                if (state.StepIdByExecutionId.TryGetValue(succeeded.ExecutionId, out var succeededStepId))
                {
                    state.ConsecutiveFailureCountByStepId[succeededStepId] = 0;
                    state.LatestFailureClassificationByStepId[succeededStepId] = null;
                    state.LatestFailureReasonByStepId[succeededStepId] = null;
                    state.LatestExecutionFailedRetryNotBeforeByStepId[succeededStepId] = null;
                    state.LatestCapturedResponseFileByStepId[succeededStepId] = null;
                    state.LatestUnsatisfiedOutputNamesByStepId[succeededStepId] = null;
                }

                break;

            case FlowEvent.ExecutionFailed failed:
                state.TerminalStatusByExecutionId[failed.ExecutionId] = StepStatus.Failed;
                if (state.StepIdByExecutionId.TryGetValue(failed.ExecutionId, out var failedStepId))
                {
                    if (failed.FailureClassification != FailureClassification.ExhaustedUntil)
                    {
                        state.ConsecutiveFailureCountByStepId[failedStepId] =
                            state.ConsecutiveFailureCountByStepId.GetValueOrDefault(failedStepId) + 1;
                    }

                    state.LatestFailureClassificationByStepId[failedStepId] = failed.FailureClassification;
                    state.LatestFailureReasonByStepId[failedStepId] = failed.Reason;
                    state.LatestExecutionFailedRetryNotBeforeByStepId[failedStepId] = failed.RetryNotBefore;
                    state.LatestCapturedResponseFileByStepId[failedStepId] = failed.CapturedResponseFile;
                    state.LatestUnsatisfiedOutputNamesByStepId[failedStepId] =
                        failed.UnsatisfiedOutputNames is null ? null : new List<string>(failed.UnsatisfiedOutputNames);
                }

                break;

            case FlowEvent.ExecutionCancelled cancelled:
                state.TerminalStatusByExecutionId[cancelled.ExecutionId] = StepStatus.Cancelled;

                // #1563: a park-abort settles a Failed, quota-parked execution as Cancelled (the
                // idle-deferral wait's own arrest seam) without ever dispatching a new attempt — so,
                // unlike ExecutionRequestAccepted's clear above, nothing else will clear the retry
                // this exact execution was scheduled for. Left in place, the idle wait's own
                // pendingDeferrals check (MutationInterface) reads this stale RetryNotBefore and
                // keeps waiting out the very deadline the cancellation was meant to end. Guarded by
                // matching RetryScheduledForExecutionId, not just StepId: a retry already
                // re-scheduled for a NEWER execution of the same step must survive this clear.
                if (state.StepIdByExecutionId.TryGetValue(cancelled.ExecutionId, out var cancelledStepId)
                    && state.RetryScheduledForExecutionIdByStepId.GetValueOrDefault(cancelledStepId) == cancelled.ExecutionId)
                {
                    state.RetryNotBeforeByStepId.Remove(cancelledStepId);
                    state.RetryDelayMsByStepId.Remove(cancelledStepId);
                    state.RetryScheduledForExecutionIdByStepId.Remove(cancelledStepId);
                }

                break;

            case FlowEvent.WorkflowPaused paused:
                state.PausedExecutionIds.Add(paused.ExecutionId);
                state.EverPausedExecutionIds.Add(paused.ExecutionId);
                break;

            case FlowEvent.ExternalDecisionRecorded decision:
                state.ReferencedExecutionIdByDecisionId[decision.DecisionId] = decision.ReferencedExecutionId;
                state.DecisionTypeByDecisionId[decision.DecisionId] = decision.DecisionType;
                if (decision.TargetStepId is { } declaredTargetStepId)
                {
                    state.TargetStepIdByDecisionId[decision.DecisionId] = declaredTargetStepId;
                }

                if (decision.SupplementaryExecutionId is { } declaredSupplementaryExecutionId)
                {
                    state.SupplementaryExecutionIdByDecisionId[decision.DecisionId] = declaredSupplementaryExecutionId;
                }

                break;

            case FlowEvent.WorkflowResumed resumed:
                if (state.ReferencedExecutionIdByDecisionId.TryGetValue(resumed.DecisionId, out var resumedExecutionId))
                {
                    state.PausedExecutionIds.Remove(resumedExecutionId);
                    var resumedDecisionType = state.DecisionTypeByDecisionId.GetValueOrDefault(resumed.DecisionId);
                    ExecutionId? supplementaryExecutionId = state.SupplementaryExecutionIdByDecisionId.TryGetValue(
                        resumed.DecisionId, out var declaredSupplement)
                        ? declaredSupplement
                        : null;

                    if (resumedDecisionType == DecisionType.Reject)
                    {
                        state.TerminalStatusByExecutionId[resumedExecutionId] = StepStatus.Rejected;
                    }

                    if (resumedDecisionType == DecisionType.RetryWithRevision &&
                        state.StepIdByExecutionId.TryGetValue(resumedExecutionId, out var retryStepId))
                    {
                        state.ConsecutiveFailureCountByStepId[retryStepId] = 0;
                        state.LatestFailureClassificationByStepId[retryStepId] = null;
                        state.LatestFailureReasonByStepId[retryStepId] = null;
                        state.LatestExecutionFailedRetryNotBeforeByStepId[retryStepId] = null;
                        state.LatestCapturedResponseFileByStepId[retryStepId] = null;
                        state.LatestUnsatisfiedOutputNamesByStepId[retryStepId] = null;
                        state.RetryNotBeforeByStepId.Remove(retryStepId);
                        state.RetryDelayMsByStepId.Remove(retryStepId);
                        state.RetryScheduledForExecutionIdByStepId.Remove(retryStepId);

                        // #1586 S1: RetryWithRevision reopens the step regardless of whether it was
                        // foreclosed — the same never-permanent rule ExecutionRequestAccepted's own
                        // clear above enforces for the ordinary dispatch path.
                        state.RetryForeclosedStepIds.Remove(retryStepId);

                        if (supplementaryExecutionId is { } retrySupplement)
                        {
                            state.PendingSupplementaryExecutionIdByStepId[retryStepId] = retrySupplement;
                        }
                        else
                        {
                            state.PendingSupplementaryExecutionIdByStepId.Remove(retryStepId);
                        }
                    }

                    if (resumedDecisionType == DecisionType.Supersede &&
                        state.TargetStepIdByDecisionId.TryGetValue(resumed.DecisionId, out var supersedeTargetStepId))
                    {
                        state.PendingSupersedeTargetStepIds.Add(supersedeTargetStepId);

                        if (supplementaryExecutionId is { } supersedeSupplement)
                        {
                            state.PendingSupplementaryExecutionIdByStepId[supersedeTargetStepId] = supersedeSupplement;
                        }
                    }
                }

                break;

            case FlowEvent.StepRetryScheduled retryScheduled:
                state.RetryNotBeforeByStepId[retryScheduled.StepId] = retryScheduled.RetryNotBefore;
                state.RetryDelayMsByStepId[retryScheduled.StepId] = retryScheduled.RetryDelayMs;
                state.RetryScheduledForExecutionIdByStepId[retryScheduled.StepId] = retryScheduled.ForExecutionId;
                break;

            case FlowEvent.CancellationRequested cancellationRequested:
                state.CancellationRequestedExecutionIds.Add(cancellationRequested.ExecutionId);
                break;

            case FlowEvent.StepRetryForeclosed foreclosed:
                // #1586 S1: all-or-nothing, the same discipline ExecutionCancelled's own retry-field
                // clear already follows (#1605) — guarded on ForExecutionId still matching the
                // scheduled retry this step carries now (FlowEvent.StepRetryForeclosed.ForExecutionId's
                // own remarks explain why a stale name must be a no-op). Applying the flag while
                // skipping the field clear (or the reverse) would leave RetryNotBefore set AND
                // MayRetry false at once — DeriveWorkflowStatus's deliverability predicate ORs the two
                // (`step.RetryNotBefore is not null` / MayRetry), so a half-applied foreclosure can
                // neither terminate nor retry.
                if (state.RetryScheduledForExecutionIdByStepId.GetValueOrDefault(foreclosed.StepId) == foreclosed.ForExecutionId)
                {
                    state.RetryForeclosedStepIds.Add(foreclosed.StepId);
                    state.RetryNotBeforeByStepId.Remove(foreclosed.StepId);
                    state.RetryDelayMsByStepId.Remove(foreclosed.StepId);
                    state.RetryScheduledForExecutionIdByStepId.Remove(foreclosed.StepId);
                }

                break;

            case FlowEvent.StepRebound rebound:
                // Overrides the frozen Adapter/Model on the accepted request so the rebind survives
                // replay (spec/baton.md §3, #802 section 3.3's own stated reason for freezing the value
                // into the event in the first place — a full replay must recover it without re-deriving
                // from bindings.json). No StepState/FlowState consequence otherwise: this does not
                // affect step lifecycle.
                if (state.AcceptedRequestByExecutionId.TryGetValue(rebound.ForExecutionId, out var reboundRequest))
                {
                    state.AcceptedRequestByExecutionId[rebound.ForExecutionId] =
                        reboundRequest with { Adapter = rebound.NewAdapter, Model = rebound.NewModel };
                }

                break;

            case FlowEvent.ExecutionRequestRejected:
            case FlowEvent.ZeroOutputsDespiteSubstantialWork:
                // Diagnostic-only facts: durable in the ledger, but no StepState/FlowState consequence.
                break;
        }
    }

    private static FlowState DeriveFlowState(
        ProjectionCheckpointState state,
        WorkflowDefinitionSnapshot snapshot)
    {
        var steps = new List<StepState>(snapshot.Steps.Count);
        foreach (var stepDefinition in snapshot.Steps)
        {
            if (!state.LatestExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var latestExecutionId))
            {
                steps.Add(new StepState(
                    stepDefinition.StepId,
                    StepStatus.Pending,
                    LatestExecutionId: null,
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                    ExecutionCount: state.ExecutionCountByStepId.GetValueOrDefault(stepDefinition.StepId)));
                continue;
            }

            var rawStatus = state.TerminalStatusByExecutionId.GetValueOrDefault(latestExecutionId, StepStatus.Running);
            var isPaused = state.PausedExecutionIds.Contains(latestExecutionId);
            var status = isPaused ? StepStatus.Paused : rawStatus;

            var upstreamExecs = state.UpstreamExecutionIdsByStepId.TryGetValue(stepDefinition.StepId, out var dict)
                ? (IReadOnlyDictionary<StepId, ExecutionId>)dict
                : new Dictionary<StepId, ExecutionId>();

            // #1359: the latest attempt's own recorded request, not a separate tracking dict — the
            // same source AcceptedRequestByExecutionId already is for every other request-carried
            // fact (contract reconstruction, GrantAuditMode replay).
            var linkedFromExecutionId = state.AcceptedRequestByExecutionId.TryGetValue(latestExecutionId, out var latestRequest)
                ? latestRequest.LinkedFromExecutionId
                : null;

            steps.Add(new StepState(
                stepDefinition.StepId,
                status,
                latestExecutionId,
                upstreamExecs,
                state.ConsecutiveFailureCountByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestFailureClassificationByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestFailureReasonByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.EverPausedExecutionIds.Contains(latestExecutionId),
                isPaused ? rawStatus : null,
                state.PendingSupplementaryExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var pendingSupplement)
                    ? pendingSupplement
                    : null,
                state.PendingSupersedeTargetStepIds.Contains(stepDefinition.StepId),
                state.RetryNotBeforeByStepId.TryGetValue(stepDefinition.StepId, out var rnb) ? rnb : null,
                state.RetryDelayMsByStepId.TryGetValue(stepDefinition.StepId, out var rdm) ? rdm : null,
                state.RetryScheduledForExecutionIdByStepId.TryGetValue(stepDefinition.StepId, out var rfe) ? rfe : null,
                state.LatestExecutionFailedRetryNotBeforeByStepId.GetValueOrDefault(stepDefinition.StepId),
                linkedFromExecutionId,
                state.ExecutionCountByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestCapturedResponseFileByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.LatestUnsatisfiedOutputNamesByStepId.GetValueOrDefault(stepDefinition.StepId),
                state.RetryForeclosedStepIds.Contains(stepDefinition.StepId)));
        }

        var workflowStatus = DeriveWorkflowStatus(steps, snapshot);

        var pendingStepLessExecutions = state.StepLessExecutionsInOrder
            .Where(execution => !state.TerminalStatusByExecutionId.ContainsKey(execution.ExecutionId))
            .ToList();

        var unfulfilledCancellationRequestExecutionIds = state.CancellationRequestedExecutionIds
            .Where(executionId => !state.TerminalStatusByExecutionId.ContainsKey(executionId))
            .ToList();

        return new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            steps,
            workflowStatus,
            pendingStepLessExecutions,
            unfulfilledCancellationRequestExecutionIds);
    }

    private static WorkflowStatus DeriveWorkflowStatus(
        IReadOnlyList<StepState> steps, WorkflowDefinitionSnapshot snapshot)
    {
        if (steps.Any(step => step.Status == StepStatus.Running))
        {
            return WorkflowStatus.Running;
        }

        if (steps.Any(step => step.Status == StepStatus.Paused))
        {
            return WorkflowStatus.Paused;
        }

        var stepById = steps.ToDictionary(step => step.StepId);
        var definitionById = snapshot.Steps.ToDictionary(definition => definition.StepId);

        if (steps.Any(step => step.IsPendingSupersedeTarget))
        {
            return WorkflowStatus.Running;
        }

        var deliverableByStepId = new Dictionary<StepId, bool>();
        bool CanStillDeliver(StepId stepId)
        {
            if (deliverableByStepId.TryGetValue(stepId, out var known))
            {
                return known;
            }

            deliverableByStepId[stepId] = false;
            var step = stepById[stepId];
            var eligible = step.Status == StepStatus.Succeeded
                || step.Status == StepStatus.Pending
                || step.RetryNotBefore is not null
                || (step.Status == StepStatus.Failed
                    && Scheduling.RetryEngine.MayRetry(step, definitionById[stepId].RetryPolicy))
                || step.PendingSupplementaryExecutionId is not null;
            var deliverable = eligible
                && (step.Status == StepStatus.Succeeded
                    || definitionById[stepId].DependsOn.All(CanStillDeliver));
            deliverableByStepId[stepId] = deliverable;
            return deliverable;
        }

        return steps.Any(step => step.Status != StepStatus.Succeeded && CanStillDeliver(step.StepId))
            ? WorkflowStatus.Running
            : WorkflowStatus.Terminal;
    }
}
