using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Flow.Domain;
using Aer.RoomSession;
using Aer.Ui;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// Serializes a <see cref="RoomProjection"/> (plus its <see cref="DagLayout"/>) into a stable,
/// comparable text form for the M14 Phase 5 golden-projection gate (issue #122) — UI spec §11 made
/// executable. Two things would otherwise defeat any golden comparison of the real read model: every
/// ID minted via <c>Guid.NewGuid()</c> at bind/dispatch time (<see cref="ExecutionId"/>,
/// <see cref="WorkflowDefinitionSnapshotId"/>, <see cref="DecisionId"/> — none of them a projected
/// fact, all of them different on every run of the same fixture) is rewritten here to a stable token
/// numbered by first appearance in the event log, which is itself deterministic (§11); and every
/// field backed by a <see cref="Dictionary{TKey,TValue}"/> or <see cref="HashSet{T}"/> (never a
/// documented-stable enumeration order in .NET) is explicitly re-sorted by key before serializing —
/// the same rule this milestone's other projectors already apply (see
/// <c>ArtifactLineageProjector</c>'s sorted file listing, <c>DagLayoutEngine</c>'s list-walk-only
/// ordering). Every field that is already List-backed and built by walking events/steps in order is
/// deliberately left in that order, not re-sorted — preserving it is exactly the determinism property
/// this gate exists to check, not an incidental detail to normalize away.
/// </summary>
internal static class GoldenProjectionCanonicalizer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Canonicalize(RoomProjection projection, DagLayout dagLayout)
    {
        var executionIdTokens = BuildExecutionIdTokens(projection);
        var decisionIdTokens = BuildDecisionIdTokens(projection.History.Decisions);

        string? Token(ExecutionId? id) => id is { } value ? executionIdTokens[value] : null;

        var document = new
        {
            Snapshot = new
            {
                SnapshotId = "snapshot-1",
                WorkflowTemplateId = projection.Snapshot.WorkflowTemplateId.Value,
                projection.Snapshot.WorkflowTemplateVersion,
                Steps = projection.Snapshot.Steps.Select(step => new
                {
                    StepId = step.StepId.Value,
                    step.Worker,
                    step.Inputs,
                    step.Outputs,
                    DependsOn = step.DependsOn.Select(d => d.Value),
                    step.RetryPolicy.MaxAttempts,
                    PausePoint = step.PausePoint is null
                        ? null
                        : new { SupersedeTargets = step.PausePoint.SupersedeTargets.Select(t => t.Value) },
                }),
            },
            State = new
            {
                projection.State.Status,
                Steps = projection.State.Steps.Select(step => new
                {
                    StepId = step.StepId.Value,
                    step.Status,
                    LatestExecutionId = Token(step.LatestExecutionId),
                    UpstreamExecutionIds = step.UpstreamExecutionIds
                        .OrderBy(kv => kv.Key.Value, StringComparer.Ordinal)
                        .Select(kv => new { StepId = kv.Key.Value, ExecutionId = executionIdTokens[kv.Value] }),
                    step.ConsecutiveFailureCount,
                    step.LatestFailureClassification,
                    step.PauseRecordedForLatestExecution,
                    step.PausedOutcome,
                    PendingSupplementaryExecutionId = Token(step.PendingSupplementaryExecutionId),
                    step.IsPendingSupersedeTarget,
                }),
                StepLessExecutions = projection.State.StepLessExecutions
                    .Select(execution => new { ExecutionId = executionIdTokens[execution.ExecutionId], execution.Worker }),
                CancellationRequestedExecutionIds = projection.State.CancellationRequestedExecutionIds
                    .Select(id => executionIdTokens[id])
                    .OrderBy(token => token, StringComparer.Ordinal),
            },
            History = new
            {
                AttemptsByStepId = projection.History.AttemptsByStepId
                    .OrderBy(kv => kv.Key.Value, StringComparer.Ordinal)
                    .Select(kv => new
                    {
                        StepId = kv.Key.Value,
                        Attempts = kv.Value.Select(ToAttemptDocument),
                    }),
                StepLessExecutions = projection.History.StepLessExecutions.Select(ToAttemptDocument),
                Decisions = projection.History.Decisions.Select(decision => new
                {
                    DecisionId = decisionIdTokens[decision.DecisionId],
                    ReferencedExecutionId = executionIdTokens[decision.ReferencedExecutionId],
                    decision.DecisionType,
                    TargetStepId = decision.TargetStepId?.Value,
                    SupplementaryExecutionId = Token(decision.SupplementaryExecutionId),
                    decision.Resolved,
                }),
            },
            Lineage = new
            {
                Executions = projection.Lineage.Executions.Select(execution => new
                {
                    ExecutionId = executionIdTokens[execution.ExecutionId],
                    StepId = execution.StepId?.Value,
                    execution.Worker,
                    execution.OutputFiles,
                    Inputs = execution.Inputs.Select(link => new
                    {
                        link.InputName,
                        ProducerStepId = link.ProducerStepId.Value,
                        ProducerExecutionId = executionIdTokens[link.ProducerExecutionId],
                    }),
                }),
            },
            DagLayout = new
            {
                Nodes = dagLayout.Nodes.Select(node => new
                {
                    StepId = node.StepId.Value,
                    node.Worker,
                    node.Rank,
                    node.Column,
                    node.HasPausePoint,
                    SupersedeTargets = node.SupersedeTargets.Select(t => t.Value),
                }),
                Edges = dagLayout.Edges.Select(edge => new { From = edge.From.Value, To = edge.To.Value, edge.IsSupersede }),
            },
        };

        return JsonSerializer.Serialize(document, Options);

        object ToAttemptDocument(ExecutionAttempt attempt) => new
        {
            ExecutionId = executionIdTokens[attempt.ExecutionId],
            attempt.Worker,
            attempt.Status,
            attempt.FailureClassification,
            attempt.IsNonProcess,
        };
    }

    private static IReadOnlyDictionary<ExecutionId, string> BuildExecutionIdTokens(RoomProjection projection)
    {
        var tokens = new Dictionary<ExecutionId, string>();
        foreach (var execution in projection.Lineage.Executions)
        {
            tokens.TryAdd(execution.ExecutionId, $"execution-{tokens.Count + 1}");
        }

        return tokens;
    }

    private static IReadOnlyDictionary<DecisionId, string> BuildDecisionIdTokens(IReadOnlyList<DecisionRecord> decisions)
    {
        var tokens = new Dictionary<DecisionId, string>();
        foreach (var decision in decisions)
        {
            tokens.TryAdd(decision.DecisionId, $"decision-{tokens.Count + 1}");
        }

        return tokens;
    }
}
