using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <summary>
/// Records the moment a workflow step entered a paused state (#1197).
/// </summary>
public sealed record StepPauseMoment(
    ExecutionId ExecutionId,
    StepId StepId,
    DateTimeOffset? PausedAt);
