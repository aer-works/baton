using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Flow.Outcomes;

/// <summary>
/// M10 Phase 3 (full robustness): reconciles a <see cref="WorkerBinding.Process"/> step
/// whose latest attempt is still projected <see cref="StepStatus.Running"/> — genuinely still
/// executing, or a prior pump crashed before recording its outcome, the two indistinguishable from
/// <see cref="FlowEvent"/>s alone — from the Core half of the log
/// (<see cref="CoreEvent.ExecutionStarted"/>/<see cref="CoreEvent.ExecutionExited"/>), which since
/// #971 arrives pre-aggregated: the caller merges the checkpoint's carried aggregates with the tail
/// read's <see cref="CoreEvent"/>s (<see cref="CoreEventAggregation.Merge"/>) and passes the merged
/// sets, so this detector sees the whole Core history however little of the log was re-read. Consulted at the top
/// of every scheduling round, the same derived-obligation pattern <see cref="NonProcessCompletionDetector"/>
/// and <see cref="NonProcessCancellationDetector"/> already follow, so a crash at any point below
/// simply re-evaluates the identical projected fact on the next mutation call. Non-process
/// targets are entirely out of scope here — Core never writes a <see cref="CoreEvent"/> for one, and
/// the two detectors above already own that tier.
/// </summary>
public static class ProcessCrashRecoveryDetector
{
    /// <summary>
    /// Classifies every <see cref="WorkerBinding.Process"/> step's unfinalized latest attempt into
    /// exactly one of four crash states:
    /// <list type="bullet">
    /// <item><see cref="ProcessCrashRecoveryObligations.ToResubmit"/> — no <see cref="CoreEvent.ExecutionStarted"/>
    /// and no unfulfilled cancellation: the safe pre-spawn crash state. Re-dispatch the same
    /// <see cref="ExecutionRequest"/>, no new accept event.</item>
    /// <item><see cref="ProcessCrashRecoveryObligations.ToFinalizeAsCancelled"/> — no
    /// <see cref="CoreEvent.ExecutionStarted"/> but an unfulfilled <see cref="FlowEvent.CancellationRequested"/>:
    /// the cancel wins — finalize cancelled, never dispatch.</item>
    /// <item><see cref="ProcessCrashRecoveryObligations.ToClassify"/> — <see cref="CoreEvent.ExecutionExited"/>
    /// recorded with no outcome yet: ran while Flow was down; classify now from the recorded
    /// exit exactly as if the completion had just arrived, regardless of any pending cancellation
    /// (too late if the exit wasn't itself a cancellation).</item>
    /// <item><see cref="ProcessCrashRecoveryObligations.ToFinalizeAsAbandoned"/> — <see cref="CoreEvent.ExecutionStarted"/>
    /// with no <see cref="CoreEvent.ExecutionExited"/>: the orphan (the third crash state).</item>
    /// </list>
    /// Every one of these excludes any <paramref name="inFlightExecutionIds"/> this same call is
    /// still genuinely awaiting, checked before any of the four states above — that dispatch's pump
    /// did not die, it is this pump, regardless of what the Core log does or doesn't yet show for it.
    /// Snapshot declaration order throughout, for the same determinism reason every other
    /// round-level obligation follows it.
    /// </summary>
    public static ProcessCrashRecoveryObligations GetObligations(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        IReadOnlySet<ExecutionId> startedExecutionIds,
        IReadOnlyDictionary<ExecutionId, CoreEvent.ExecutionExited> exitedByExecutionId,
        IReadOnlySet<ExecutionId> inFlightExecutionIds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentNullException.ThrowIfNull(startedExecutionIds);
        ArgumentNullException.ThrowIfNull(exitedByExecutionId);
        ArgumentNullException.ThrowIfNull(inFlightExecutionIds);

        var stepStateByStepId = state.Steps.ToDictionary(step => step.StepId);
        var toResubmit = new List<ExecutionId>();
        var toFinalizeAsCancelled = new List<ExecutionId>();
        var toClassify = new List<(ExecutionId ExecutionId, CoreEvent.ExecutionExited Exit)>();
        var toFinalizeAsAbandoned = new List<ExecutionId>();

        // Snapshot declaration order, for the same determinism reason every other round-level
        // append in MutationInterface follows it rather than a projected list's iteration order.
        foreach (var stepDefinition in snapshot.Steps)
        {
            var stepState = stepStateByStepId[stepDefinition.StepId];
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            // A dispatch this very call already has registered is not a crash-recovery candidate at
            // all, regardless of what the Core log does or doesn't yet show for it: this pump is
            // still genuinely awaiting it right now, not one that crashed mid-run and needs
            // reconciling by a later, different call.
            if (inFlightExecutionIds.Contains(executionId))
            {
                continue;
            }

            if (exitedByExecutionId.TryGetValue(executionId, out var exit))
            {
                toClassify.Add((executionId, exit));
                continue;
            }

            if (startedExecutionIds.Contains(executionId))
            {
                toFinalizeAsAbandoned.Add(executionId);
                continue;
            }

            // Non-process reconciliation is NonProcessCompletionDetector/NonProcessCancellationDetector's
            // job — Core never writes a CoreEvent for a binding with no process behind it. Reached
            // only for the pre-spawn states: the classify/abandon branches above act on recorded
            // Core evidence and never consult the binding (#724). A present-but-unresolvable
            // binding is allowed to throw out of here — a pre-spawn recovery genuinely needs the
            // binding to resubmit, and swallowing the refusal would leave the step Running forever
            // with no obligation and no explanation.
            if (!workerBindings.TryGetValue(stepDefinition.Worker, out var binding) || binding is not WorkerBinding.Process)
            {
                continue;
            }

            if (state.CancellationRequestedExecutionIds.Contains(executionId))
            {
                toFinalizeAsCancelled.Add(executionId);
            }
            else
            {
                toResubmit.Add(executionId);
            }
        }

        return new ProcessCrashRecoveryObligations(toResubmit, toFinalizeAsCancelled, toClassify, toFinalizeAsAbandoned);
    }
}

/// <summary>The four crash-state obligations <see cref="ProcessCrashRecoveryDetector.GetObligations"/> derives.</summary>
public sealed record ProcessCrashRecoveryObligations(
    IReadOnlyList<ExecutionId> ToResubmit,
    IReadOnlyList<ExecutionId> ToFinalizeAsCancelled,
    IReadOnlyList<(ExecutionId ExecutionId, CoreEvent.ExecutionExited Exit)> ToClassify,
    IReadOnlyList<ExecutionId> ToFinalizeAsAbandoned);
