using System.Text.Json.Serialization;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// One step's machine-readable state, per <c>aer status --json</c>'s shape (#1356): a bare
/// <see cref="StepStatus"/> token, never the human prose <c>StatusCommand.FormatStepStatus</c> prints
/// (a parked/liveness-annotated sentence a machine consumer would have to parse back apart).
/// </summary>
public sealed record WorkflowStatusStepView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("execution")] string? Execution);

/// <summary>
/// The one JSON object <c>aer status --json</c> writes to stdout (#1356's machine completion
/// contract): <c>{state, steps:[{id, state, execution}], outputs:[...], error, try}</c>. Also what the
/// terminal sentinel (<c>terminal.json</c>, <see cref="TerminalSentinelWriter"/>) serializes, so a
/// file-watching agent and a polling <c>status --json</c> caller read the identical shape.
/// <c>Try</c> (#1382 F3) is additive to #1356's shape: the corrected-invocation text an
/// <see cref="Aer.Flow.AerFlowException.TryInvocation"/>-carrying refusal set, kept as its own field
/// rather than appended into <see cref="Error"/> so a consumer can tell diagnosis from remedy apart.
/// Only ever populated on the pre-ledger sentinel path (<see cref="TerminalSentinelWriter.WriteValidationRefusedAsync"/>) —
/// a normal ledger projection has no exception to carry one.
/// </summary>
public sealed record WorkflowStatusView(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorkflowStatusStepView> Steps,
    [property: JsonPropertyName("outputs")] IReadOnlyList<string> Outputs,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("try")] string? Try = null);

/// <summary>
/// Builds <see cref="WorkflowStatusView"/> from the same <see cref="FlowState"/>
/// <c>StatusCommand.PrintState</c>/<c>FlowStateReporter.Report</c> already render (one derivation,
/// two — now three, counting the terminal sentinel — renderings; #1356 requires never forking the
/// projection itself). Never re-reads <c>flow.jsonl</c> or <c>snapshot.json</c> on its own: callers
/// pass in the already-projected <see cref="FlowState"/>.
/// </summary>
public static class WorkflowStatusProjector
{
    public static WorkflowStatusView Project(FlowState state, WorkflowDefinitionSnapshot snapshot, string roomDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var stepDefByStepId = snapshot.Steps.ToDictionary(step => step.StepId);
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);

        var steps = new List<WorkflowStatusStepView>(state.Steps.Count);
        var outputs = new List<string>();
        string? firstFailureReason = null;

        foreach (var step in state.Steps)
        {
            steps.Add(new WorkflowStatusStepView(step.StepId.Value, step.Status.ToString(), step.LatestExecutionId?.Value));

            if (firstFailureReason is null && step.Status is StepStatus.Failed or StepStatus.Rejected
                && !string.IsNullOrWhiteSpace(step.LatestFailureReason))
            {
                firstFailureReason = step.LatestFailureReason;
            }

            // #740's rule via StepOutputResolver, the one place it is implemented (#1374 F5) — this
            // must never drift from FlowStateReporter's own printed paths for the same room.
            if (stepDefByStepId.TryGetValue(step.StepId, out var stepDef))
            {
                outputs.AddRange(StepOutputResolver.Resolve(step, stepDef, artifactsRootPath).Select(o => o.Path));
            }
        }

        return new WorkflowStatusView(WorkflowOutcome.Describe(state), steps, outputs, firstFailureReason);
    }
}
