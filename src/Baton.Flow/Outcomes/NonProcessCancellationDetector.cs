using Baton.Flow.Domain;
using Baton.Flow.Mutation;

namespace Baton.Flow.Outcomes;

/// <summary>
/// Capability 11's non-process cancellation half (vacuous with no process):
/// finds every unfulfilled <see cref="FlowEvent.CancellationRequested"/> — <see cref="FlowState.CancellationRequestedExecutionIds"/>
/// — that names a still-<see cref="StepStatus.Running"/> execution with no live Core process behind
/// it, either a step bound to a <see cref="WorkerBinding.NonProcess"/> worker or a step-less
/// supplementary execution (which is always non-process by construction). With nothing to
/// forward to Core, Flow is already the outcome authority for this tier (M9 Phase 4), so the same
/// round's derived obligation finalizes these directly. Consulted at the top of every scheduling
/// round, exactly like <see cref="NonProcessCompletionDetector"/>'s derived obligation, so a crash
/// between the intent and this finalization simply re-evaluates the identical projected fact on the
/// next mutation call. A <see cref="WorkerBinding.Process"/> target's unfulfilled request
/// is left untouched here — delivering it to a live Core execution is Phase 2's machinery.
/// </summary>
public static class NonProcessCancellationDetector
{
    /// <summary>
    /// Returns the <see cref="ExecutionId"/>s that owe an <see cref="FlowEvent.ExecutionCancelled"/>
    /// append right now: a pending non-process execution with an outstanding, unfulfilled
    /// cancellation request.
    /// </summary>
    public static IReadOnlyList<ExecutionId> GetCancelledExecutions(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);

        if (state.CancellationRequestedExecutionIds.Count == 0)
        {
            return [];
        }

        var stepStateByStepId = state.Steps.ToDictionary(step => step.StepId);
        var cancelled = new List<ExecutionId>();

        // Snapshot declaration order, for the same determinism reason every other round-level
        // append in MutationInterface follows it rather than a projected list's iteration order.
        foreach (var stepDefinition in snapshot.Steps)
        {
            var stepState = stepStateByStepId[stepDefinition.StepId];
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            if (!state.CancellationRequestedExecutionIds.Contains(executionId))
            {
                continue;
            }

            // A Process-bound target's request stays unfulfilled here — Phase 2 delivers it to Core.
            if (!workerBindings.TryGetValue(stepDefinition.Worker, out var binding) || binding is not WorkerBinding.NonProcess)
            {
                continue;
            }

            cancelled.Add(executionId);
        }

        foreach (var stepLessExecution in state.StepLessExecutions)
        {
            // Step-less executions are only ever minted against a non-process binding
            // (RecordSupplementaryExecutionAsync), so no binding lookup is needed here.
            if (state.CancellationRequestedExecutionIds.Contains(stepLessExecution.ExecutionId))
            {
                cancelled.Add(stepLessExecution.ExecutionId);
            }
        }

        return cancelled;
    }
}
