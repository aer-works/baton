using Baton.Domain;

namespace Baton.Scheduling;

/// <summary>
/// Capability 13: decides which steps owe a <see cref="FlowEvent.WorkflowPaused"/>
/// append. A pure function over <see cref="FlowState"/> and <see cref="WorkflowDefinitionSnapshot"/>
/// — no I/O, no dispatch — evaluated as a derived obligation at the top of every scheduling round,
/// so a crash between an outcome event and its pause event re-derives the identical obligation on
/// the next mutation call.
/// </summary>
public static class PauseEngine
{
    /// <summary>
    /// Returns the <see cref="ExecutionId"/>s (with their owning <see cref="StepId"/>) that owe a
    /// <see cref="FlowEvent.WorkflowPaused"/> append: the step declares a <see cref="PausePoint"/>,
    /// its latest attempt has an <see cref="ExecutionId"/>, that attempt's round has settled
    /// (<see cref="StepStatus.Succeeded"/>, <see cref="StepStatus.Cancelled"/>, or
    /// <see cref="StepStatus.Failed"/> with <see cref="RetryEngine.MayRetry"/> false — automatic
    /// retry runs first), and no <see cref="FlowEvent.WorkflowPaused"/> has been recorded for
    /// that attempt yet (<see cref="StepState.PauseRecordedForLatestExecution"/>) — so a resumed
    /// execution is never re-paused.
    /// </summary>
    public static IReadOnlyList<(StepId StepId, ExecutionId ExecutionId)> GetPauseObligations(
        FlowState state, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);

        var stepStateByStepId = state.Steps.ToDictionary(step => step.StepId);
        var obligations = new List<(StepId, ExecutionId)>();

        // Snapshot declaration order, not the projected list's order, for the same determinism
        // reason MutationInterface emits a round's ExecutionRequestAccepted events that way.
        foreach (var stepDefinition in snapshot.Steps)
        {
            if (stepDefinition.PausePoint is null)
            {
                continue;
            }

            var stepState = stepStateByStepId[stepDefinition.StepId];
            if (stepState.LatestExecutionId is null || stepState.PauseRecordedForLatestExecution)
            {
                continue;
            }

            var roundSettled = stepState.Status switch
            {
                StepStatus.Succeeded => true,
                StepStatus.Cancelled => true,
                StepStatus.Failed => !RetryEngine.MayRetry(stepState, stepDefinition.RetryPolicy),
                _ => false, // Pending never applies here; Running and Paused have not settled.
            };

            if (roundSettled)
            {
                obligations.Add((stepDefinition.StepId, stepState.LatestExecutionId.Value));
            }
        }

        return obligations;
    }
}
