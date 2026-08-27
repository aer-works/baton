using System.Text.Json.Serialization;
using Aer.Adapters;
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
    [property: JsonPropertyName("execution")] string? Execution,
    // #1359: the execution `aer resume` continued, when Execution is a resume's own new attempt —
    // null for every ordinary dispatch/retry. Lets a status consumer render both executions of a
    // resumed step without a second lookup.
    [property: JsonPropertyName("linkedFrom")] string? LinkedFrom = null,
    // #1360: Execution's own usage -- absent (not present as a whole) when that execution has no
    // recorded start/exit pair to derive wall-clock from (still running, or Flow crashed before Core
    // recorded either lifecycle event). See ExecutionUsageProjector.
    [property: JsonPropertyName("usage")] ExecutionUsageView? Usage = null,
    // #1360: LinkedFrom's own usage, kept separate from Usage rather than merged -- a resumed step's
    // two executions are two distinct cost entries, not one to be added or overwritten.
    [property: JsonPropertyName("linkedFromUsage")] ExecutionUsageView? LinkedFromUsage = null);

/// <summary>
/// The one JSON object <c>aer status --json</c> writes to stdout (#1356's machine completion
/// contract): <c>{state, steps:[{id, state, execution, linkedFrom}], outputs:[...], error, try}</c>.
/// <c>linkedFrom</c> (#1359) is additive to #1356's shape, same as <c>Try</c> below — see
/// <see cref="WorkflowStatusStepView.LinkedFrom"/>. Also what the terminal sentinel
/// (<c>terminal.json</c>, <see cref="TerminalSentinelWriter"/>) serializes, so a file-watching agent
/// and a polling <c>status --json</c> caller read the identical shape.
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
/// pass in the already-projected <see cref="FlowState"/>, and (#1360) the raw <see cref="LogEntry"/>
/// list a caller already read for that same projection, when per-execution usage is wanted.
/// </summary>
public static class WorkflowStatusProjector
{
    /// <param name="entries">
    /// The same ledger entries the caller already read to produce <paramref name="state"/> (#1360) —
    /// source data for <see cref="ExecutionUsageProjector.BuildByExecutionId"/>. Omitted (or empty)
    /// yields a view with no <c>usage</c> on any step, never a fabricated one; a caller that has no
    /// use for usage data (or has not read the ledger for another reason) is not forced to.
    /// </param>
    /// <param name="adapters">
    /// Registered adapters (#1360) an execution's own dispatched worker is attributed to via
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c> — see
    /// <see cref="ExecutionUsageProjector"/>'s remarks for how attribution works and what happens
    /// without it. Defaults to <see cref="WorkerAdapterRegistry.Default"/>.
    /// </param>
    public static WorkflowStatusView Project(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        IReadOnlyList<LogEntry>? entries = null,
        IReadOnlyDictionary<string, IWorkerAdapter>? adapters = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var stepDefByStepId = snapshot.Steps.ToDictionary(step => step.StepId);
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);

        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
            entries ?? [], artifactsRootPath, adapters ?? WorkerAdapterRegistry.Default, roomDirectoryPath);

        var steps = new List<WorkflowStatusStepView>(state.Steps.Count);
        var outputs = new List<string>();
        string? firstFailureReason = null;

        foreach (var step in state.Steps)
        {
            var usage = step.LatestExecutionId is { } latest && usageByExecutionId.TryGetValue(latest.Value, out var latestUsage)
                ? latestUsage
                : null;
            var linkedFromUsage = step.LinkedFromExecutionId is { } linkedFrom && usageByExecutionId.TryGetValue(linkedFrom.Value, out var linkedUsage)
                ? linkedUsage
                : null;

            steps.Add(new WorkflowStatusStepView(
                step.StepId.Value, step.Status.ToString(), step.LatestExecutionId?.Value, step.LinkedFromExecutionId?.Value,
                usage, linkedFromUsage));

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
