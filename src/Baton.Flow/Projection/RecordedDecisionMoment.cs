using Baton.Flow.Domain;

namespace Baton.Flow.Projection;

/// <summary>
/// Records the moment an external decision was recorded against a paused step (#1197).
/// </summary>
public sealed record RecordedDecisionMoment(
    DecisionId DecisionId,
    ExecutionId ReferencedExecutionId,
    DecisionType DecisionType,
    StepId? TargetStepId,
    ExecutionId? SupplementaryExecutionId,
    DeciderInfo Decider,
    DateTimeOffset? RecordedAt);
