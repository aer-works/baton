namespace Baton.Projection;

/// <summary>
/// Operator-visible settings record for host-level turn throttles (#778).
/// </summary>
public sealed record RoomTurnThrottles(
    TimeSpan MinMachineTurnInterval,
    int MaxMachineTurnsPerHour,
    int FailedTurnsBeforeDormancy)
{
    public static TimeSpan DefaultMinMachineTurnInterval { get; } = TimeSpan.FromSeconds(60);
    public const int DefaultMaxMachineTurnsPerHour = 10;
    public const int DefaultFailedTurnsBeforeDormancy = 3;

    public static RoomTurnThrottles Default { get; } = new(
        DefaultMinMachineTurnInterval,
        DefaultMaxMachineTurnsPerHour,
        DefaultFailedTurnsBeforeDormancy);
}
