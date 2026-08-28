using Aer.Flow.Domain;

namespace Aer.RoomSession;

/// <summary>
/// Reconstructs <see cref="ExecutionHistory"/> from event history — a pure function over the exact
/// same <c>IReadOnlyList&lt;FlowEvent&gt;</c> <c>StateProjector.Project</c> consumes (spec §12), so
/// it inherits the same determinism guarantee (§13) <see cref="RoomProjectionLoader"/> already
/// relies on for <see cref="Aer.Flow.Domain.FlowState"/>. Deliberately does not call into
/// <c>StateProjector</c> or duplicate its dispatch-affecting logic (retry admission, staleness,
/// dependency readiness) — it only re-derives the same "what happened to this ExecutionId" mapping
/// <c>StateProjector</c> already computes internally, but keeps one entry per execution instead of
/// collapsing to each step's latest.
/// </summary>
public static class ExecutionHistoryProjector
{
    public static ExecutionHistory Project(IReadOnlyList<FlowEvent> events, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(snapshot);

        var stepIdByExecutionId = new Dictionary<ExecutionId, StepId?>();
        var workerByExecutionId = new Dictionary<ExecutionId, string>();
        var isNonProcessByExecutionId = new Dictionary<ExecutionId, bool>();
        var orderedExecutionIdsByStepId = new Dictionary<StepId, List<ExecutionId>>();
        var orderedStepLessExecutionIds = new List<ExecutionId>();
        var terminalStatusByExecutionId = new Dictionary<ExecutionId, StepStatus>();
        var failureClassificationByExecutionId = new Dictionary<ExecutionId, FailureClassification?>();
        var reasonByExecutionId = new Dictionary<ExecutionId, string?>();
        var pausedExecutionIds = new HashSet<ExecutionId>();
        var decisions = new List<DecisionRecord>();
        var decisionIndexByDecisionId = new Dictionary<DecisionId, int>();

        foreach (var flowEvent in events)
        {
            switch (flowEvent)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    var request = accepted.Request;
                    stepIdByExecutionId[request.ExecutionId] = request.StepId;
                    workerByExecutionId[request.ExecutionId] = request.Worker;
                    isNonProcessByExecutionId[request.ExecutionId] = request.Timeout is null;

                    if (request.StepId is { } stepId)
                    {
                        if (!orderedExecutionIdsByStepId.TryGetValue(stepId, out var executionIds))
                        {
                            executionIds = [];
                            orderedExecutionIdsByStepId[stepId] = executionIds;
                        }

                        executionIds.Add(request.ExecutionId);
                    }
                    else
                    {
                        orderedStepLessExecutionIds.Add(request.ExecutionId);
                    }

                    break;

                case FlowEvent.ExecutionSucceeded succeeded:
                    terminalStatusByExecutionId[succeeded.ExecutionId] = StepStatus.Succeeded;
                    break;

                case FlowEvent.ExecutionFailed failed:
                    terminalStatusByExecutionId[failed.ExecutionId] = StepStatus.Failed;
                    failureClassificationByExecutionId[failed.ExecutionId] = failed.FailureClassification;
                    reasonByExecutionId[failed.ExecutionId] = failed.Reason;
                    break;

                case FlowEvent.ExecutionCancelled cancelled:
                    terminalStatusByExecutionId[cancelled.ExecutionId] = StepStatus.Cancelled;
                    break;

                case FlowEvent.WorkflowPaused paused:
                    pausedExecutionIds.Add(paused.ExecutionId);
                    break;

                case FlowEvent.ExternalDecisionRecorded decision:
                    decisionIndexByDecisionId[decision.DecisionId] = decisions.Count;
                    decisions.Add(new DecisionRecord(
                        decision.DecisionId,
                        decision.ReferencedExecutionId,
                        decision.DecisionType,
                        decision.TargetStepId,
                        decision.SupplementaryExecutionId,
                        Resolved: false));
                    break;

                case FlowEvent.WorkflowResumed resumed:
                    if (decisionIndexByDecisionId.TryGetValue(resumed.DecisionId, out var decisionIndex))
                    {
                        var recorded = decisions[decisionIndex];
                        decisions[decisionIndex] = recorded with { Resolved = true };
                        pausedExecutionIds.Remove(recorded.ReferencedExecutionId);

                        // Reject is the one decision type that changes the referenced execution's
                        // outcome rather than letting it stand (spec §17.2) — the same override
                        // StateProjector.Project applies to a step's latest attempt, re-applied here
                        // per-attempt since Reject always names a specific ExecutionId.
                        if (recorded.DecisionType == DecisionType.Reject)
                        {
                            terminalStatusByExecutionId[recorded.ReferencedExecutionId] = StepStatus.Rejected;
                        }
                    }

                    break;

                case FlowEvent.CancellationRequested:
                case FlowEvent.ExecutionRequestRejected:
                    break;
            }
        }

        ExecutionAttempt ToAttempt(ExecutionId executionId)
        {
            var status = pausedExecutionIds.Contains(executionId)
                ? StepStatus.Paused
                : terminalStatusByExecutionId.GetValueOrDefault(executionId, StepStatus.Running);

            return new ExecutionAttempt(
                executionId,
                workerByExecutionId[executionId],
                status,
                failureClassificationByExecutionId.GetValueOrDefault(executionId),
                isNonProcessByExecutionId[executionId],
                reasonByExecutionId.GetValueOrDefault(executionId));
        }

        var attemptsByStepId = snapshot.Steps.ToDictionary(
            step => step.StepId,
            step => (IReadOnlyList<ExecutionAttempt>)(orderedExecutionIdsByStepId.TryGetValue(step.StepId, out var executionIds)
                ? executionIds.Select(ToAttempt).ToList()
                : []));

        var stepLessExecutions = orderedStepLessExecutionIds.Select(ToAttempt).ToList();

        return new ExecutionHistory(attemptsByStepId, stepLessExecutions, decisions);
    }
}
