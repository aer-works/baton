using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;

namespace Baton.Outcomes;

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
/// is left untouched here — delivering it to a live Core execution is Phase 2's machinery. A
/// quota-parked target (#1607) is also left untouched here — that arrest path is
/// <c>MutationInterface.SettleParkedCancelIntentsAsync</c>'s, not this detector's (#1556 PR 1:
/// <see cref="ArrestableExecutions.All"/> yields a parked target too, filtered back out below).
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

        var cancelled = new List<ExecutionId>();

        // ArrestableExecutions.All is already in FlowState.Steps order then StepLessExecutions
        // order — the same determinism every other round-level append in MutationInterface follows.
        foreach (var target in ArrestableExecutions.All(state, snapshot))
        {
            if (!state.CancellationRequestedExecutionIds.Contains(target.ExecutionId))
            {
                continue;
            }

            if (target.StepId is not null)
            {
                // Step-tied: only a Running step bound to NonProcess is this detector's to finalize.
                // A quota-parked step's request stays unfulfilled here (SettleParkedCancelIntentsAsync
                // owns it), and so does a Process-bound target's (Phase 2 delivers it to Core).
                if (target.Status != StepStatus.Running
                    || !workerBindings.TryGetValue(target.Worker, out var binding)
                    || binding is not WorkerBinding.NonProcess)
                {
                    continue;
                }
            }

            // A step-less target (StepId is null) is only ever minted against a non-process binding
            // (RecordSupplementaryExecutionAsync), so no binding lookup is needed for it.
            cancelled.Add(target.ExecutionId);
        }

        return cancelled;
    }
}
