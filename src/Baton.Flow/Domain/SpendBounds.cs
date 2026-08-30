namespace Baton.Flow.Domain;

/// <summary>
/// Spend bounds recorded in a grant. Exceeding any raises a Spend escalation.
/// </summary>
public sealed record SpendBounds(
    int MaxPerRunMinutes = 20,
    int MaxConcurrentRunsPerRoom = 3,
    int MaxDispatchesPerDayPerRoom = 25,
    int MaxFrontierReviewsPerDay = 10);
