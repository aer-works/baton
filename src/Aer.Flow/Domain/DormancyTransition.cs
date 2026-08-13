namespace Aer.Flow.Domain;

/// <summary>
/// Records a transition into or out of turn host dormancy (#1178).
/// </summary>
public sealed record DormancyTransition(
    bool IsEntered,
    int ConsecutiveFailures,
    string? Detail,
    string? ClearedBy,
    DateTimeOffset Timestamp);
