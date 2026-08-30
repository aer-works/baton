namespace Baton.Flow.Projection;

/// <summary>
/// Source of a turn wake (#778 design addendum).
/// </summary>
public enum TurnWakeSource
{
    UserMessage,
    Machine,
}

/// <summary>
/// Specific throttle cap that refused a turn (#778 design addendum).
/// </summary>
public enum TurnRefusalReason
{
    MinInterval,
    HourlyCap,
    Dormant,
}

/// <summary>
/// Result of evaluating turn throttles: Allowed or Refused with a specific reason.
/// </summary>
public sealed record TurnPermission(
    bool IsAllowed,
    TurnRefusalReason? RefusalReason)
{
    public static TurnPermission Allowed { get; } = new(true, null);

    public static TurnPermission Refused(TurnRefusalReason reason) => new(false, reason);
}

/// <summary>
/// Pure decision function for room turn throttles (#778).
/// <c>(throttles, usage, wakeSource, now) -> TurnPermission</c>
/// </summary>
public static class RoomTurnDecider
{
    /// <summary>
    /// Evaluates whether a turn is permitted given current throttles, usage counters, wake source, and wall-clock time.
    /// <para>
    /// <b>Dormancy Rule:</b> If <paramref name="usage"/>.ConsecutiveFailedTurns &gt;= <paramref name="throttles"/>.FailedTurnsBeforeDormancy,
    /// occupant-model calls are refused with <see cref="TurnRefusalReason.Dormant"/> regardless of <paramref name="wakeSource"/>.
    /// (Lead's reading: user messages wake the product, which responds with dormancy state and swap control rather than an occupant turn).
    /// </para>
    /// <para>
    /// <b>User Wake Rule:</b> User-message wakes bypass <see cref="TurnRefusalReason.MinInterval"/> and <see cref="TurnRefusalReason.HourlyCap"/>,
    /// returning <see cref="TurnPermission.Allowed"/> unless dormant.
    /// </para>
    /// <para>
    /// <b>Machine Wake Rule:</b> Checked against minimum interval (<see cref="TurnRefusalReason.MinInterval"/>) and hourly cap (<see cref="TurnRefusalReason.HourlyCap"/>).
    /// </para>
    /// </summary>
    public static TurnPermission Decide(
        RoomTurnThrottles throttles,
        RoomTurnUsage usage,
        TurnWakeSource wakeSource,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(throttles);
        ArgumentNullException.ThrowIfNull(usage);

        // 1. Dormancy circuit breaker check (applies to ALL wake sources)
        if (usage.ConsecutiveFailedTurns >= throttles.FailedTurnsBeforeDormancy)
        {
            return TurnPermission.Refused(TurnRefusalReason.Dormant);
        }

        // 2. User wakes bypass machine caps
        if (wakeSource == TurnWakeSource.UserMessage)
        {
            return TurnPermission.Allowed;
        }

        // 3. Machine wakes: check minimum turn interval
        if (usage.LastMachineTurnAt.HasValue)
        {
            var elapsed = now - usage.LastMachineTurnAt.Value;
            if (elapsed < throttles.MinMachineTurnInterval)
            {
                return TurnPermission.Refused(TurnRefusalReason.MinInterval);
            }
        }

        // 4. Machine wakes: check hourly turn cap
        var turnsThisHour = usage.GetMachineTurnsThisHour(now);
        if (turnsThisHour >= throttles.MaxMachineTurnsPerHour)
        {
            return TurnPermission.Refused(TurnRefusalReason.HourlyCap);
        }

        return TurnPermission.Allowed;
    }
}
