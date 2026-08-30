using Baton.Artifacts;
using Baton.Domain;
using Baton.Mutation;

namespace Baton.Outcomes;

/// <summary>
/// Capability 16's completion half: finds every unfinalized non-process execution —
/// a step bound to a <see cref="WorkerBinding.NonProcess"/>, or a step-less supplementary
/// execution — whose output directory now satisfies its <see cref="WorkerContract"/>. Consulted at
/// the top of every scheduling round, exactly like <see cref="Scheduling.PauseEngine"/>'s derived
/// obligation, so a crash between the artifact appearing on disk and this classification landing
/// simply re-evaluates the same disk state on the next mutation call. The filesystem is read here,
/// at classification time, only — replay never re-evaluates; the resulting
/// <see cref="FlowEvent.ExecutionSucceeded"/> is the durable truth thereafter.
/// </summary>
public static class NonProcessCompletionDetector
{
    /// <summary>
    /// Returns the <see cref="ExecutionId"/>s that satisfy their contract right now and
    /// therefore owe an <see cref="FlowEvent.ExecutionSucceeded"/> append. An unsatisfied contract
    /// means still pending — never <c>Failed</c> — since there is no exit signal to classify
    /// against.
    /// </summary>
    /// <exception cref="UnresolvedWorkerException">
    /// A step-less execution's <see cref="StepLessExecutionState.Worker"/> has no corresponding
    /// <see cref="WorkerBinding.NonProcess"/> among <paramref name="workerBindings"/> — unlike a
    /// step-tied execution (which may legitimately be a still-in-flight process dispatch), a
    /// step-less execution is only ever minted against a non-process binding, so a missing or
    /// mismatched one here can only be a caller configuration gap.
    /// </exception>
    public static IReadOnlyList<ExecutionId> GetSettledExecutions(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var stepStateByStepId = state.Steps.ToDictionary(step => step.StepId);
        var settled = new List<ExecutionId>();

        // Snapshot declaration order, for the same determinism reason every other round-level
        // append in MutationInterface follows it rather than a projected list's iteration order.
        foreach (var stepDefinition in snapshot.Steps)
        {
            var stepState = stepStateByStepId[stepDefinition.StepId];
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            // Only a step actually bound to a non-process worker can ever be "settled" by contract
            // satisfaction alone — a Running process-bound step is either still genuinely in
            // flight or crashed before its outcome was recorded, and is not this method's
            // concern either way; DispatchAndRecordOutcomeAsync's own in-flight task owns it.
            if (!workerBindings.TryGetValue(stepDefinition.Worker, out var binding) || binding is not WorkerBinding.NonProcess nonProcess)
            {
                continue;
            }

            if (IsSatisfied(nonProcess.Contract, executionId, artifactsRootPath))
            {
                settled.Add(executionId);
            }
        }

        foreach (var stepLessExecution in state.StepLessExecutions)
        {
            if (!workerBindings.TryGetValue(stepLessExecution.Worker, out var binding) || binding is not WorkerBinding.NonProcess nonProcess)
            {
                throw new UnresolvedWorkerException(
                    $"No non-process WorkerBinding registered for Worker '{stepLessExecution.Worker}' " +
                    $"(supplementary execution '{stepLessExecution.ExecutionId}').");
            }

            if (IsSatisfied(nonProcess.Contract, stepLessExecution.ExecutionId, artifactsRootPath))
            {
                settled.Add(stepLessExecution.ExecutionId);
            }
        }

        return settled;
    }

    private static bool IsSatisfied(WorkerContract contract, ExecutionId executionId, string artifactsRootPath) =>
        ContractValidator.IsSatisfied(contract, ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId));
}
