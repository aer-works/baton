namespace Baton.Projection;

/// <summary>
/// Engine bookkeeping counters for machine turns and failures under <c>{room}/.baton/turn-usage.json</c> (#778).
/// <para>
/// <b>Location rationale:</b> Lives under <c>{room}/.baton/</c> because usage tracking is internal engine
/// state, unlike operator-edited <c>turn-throttles.json</c> at room root.
/// </para>
/// </summary>
public sealed record RoomTurnUsage(
    IReadOnlyList<DateTimeOffset> RecentMachineTurnTimestamps,
    DateTimeOffset? LastMachineTurnAt,
    int ConsecutiveFailedTurns)
{
    public static RoomTurnUsage Empty { get; } = new(
        Array.Empty<DateTimeOffset>(),
        null,
        0);

    /// <summary>
    /// Computes the rolling number of machine turns started within the 1-hour window prior to <paramref name="now"/>.
    /// </summary>
    public int GetMachineTurnsThisHour(DateTimeOffset now)
    {
        var windowStart = now - TimeSpan.FromHours(1);
        return RecentMachineTurnTimestamps.Count(t => t > windowStart && t <= now);
    }

    /// <summary>
    /// Returns an updated <see cref="RoomTurnUsage"/> recording a new machine turn starting at <paramref name="now"/>.
    /// Filters out timestamps older than 1 hour to keep the list bounded.
    /// </summary>
    public RoomTurnUsage RecordMachineTurnStarted(DateTimeOffset now)
    {
        var windowStart = now - TimeSpan.FromHours(1);
        var updatedTimestamps = RecentMachineTurnTimestamps
            .Where(t => t > windowStart && t <= now)
            .Append(now)
            .ToList()
            .AsReadOnly();

        return this with
        {
            RecentMachineTurnTimestamps = updatedTimestamps,
            LastMachineTurnAt = now
        };
    }

    /// <summary>
    /// Increments the consecutive failed turn counter.
    /// </summary>
    public RoomTurnUsage RecordTurnFailed()
    {
        return this with
        {
            ConsecutiveFailedTurns = ConsecutiveFailedTurns + 1
        };
    }

    /// <summary>
    /// Resets the consecutive failed turn counter to zero on turn commit.
    /// </summary>
    public RoomTurnUsage RecordTurnCommitted()
    {
        return this with
        {
            ConsecutiveFailedTurns = 0
        };
    }
}
