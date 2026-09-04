namespace Baton.Cli.Daemon;

/// <summary>
/// Pure cadence decision for issue #1391's usage harvester — no process spawn, no file I/O, no clock
/// read of its own; <paramref name="now"/> is caller-supplied on every tick so
/// <c>VendorUsageHarvestSchedulerTests</c> can drive it with a fake clock deterministically. Kept
/// separate from <see cref="VendorUsageHarvester"/> (the <c>BackgroundService</c> that spawns
/// sources and persists snapshots) so the cadence rules — the part with the most ways to be subtly
/// wrong — are testable without a process double.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules (issue #1391, operator-approved 2026-09-04):</b> harvest every
/// <paramref name="periodicInterval"/> ONLY while at least one lane is live on that vendor; harvest
/// once <paramref name="postExitDelay"/> after any lane exits; jitter both by up to
/// <paramref name="jitter"/>; coalesce a trigger that lands within <paramref name="coalesceWindow"/>
/// of the most recent actual harvest into that harvest rather than firing a second one; back off to
/// no harvesting at all while idle (no live lane and no pending post-exit trigger).
/// </para>
/// <para>
/// <b>One <see cref="VendorState"/> per vendor tag</b> (<c>"claude"</c>/<c>"agy"</c>), lazily created
/// on first tick. <see cref="OnTick"/> is NOT reentrant-safe for the same vendor called
/// concurrently — <see cref="VendorUsageHarvester"/>'s own tick loop calls it sequentially, once per
/// vendor per tick, never overlapping.
/// </para>
/// </remarks>
public sealed class VendorUsageHarvestScheduler
{
    private readonly TimeSpan _periodicInterval;
    private readonly TimeSpan _jitter;
    private readonly TimeSpan _postExitDelay;
    private readonly TimeSpan _coalesceWindow;
    private readonly Func<double> _jitterSource;

    private readonly Dictionary<string, VendorState> _states = new(StringComparer.Ordinal);

    /// <param name="jitterSource">Returns a value in [-1, 1]; multiplied by the relevant delay's own
    /// jitter budget. Defaults to <see cref="Random.Shared"/>; a test supplies a fixed value for a
    /// deterministic due instant.</param>
    public VendorUsageHarvestScheduler(
        TimeSpan periodicInterval,
        TimeSpan jitter,
        TimeSpan postExitDelay,
        TimeSpan coalesceWindow,
        Func<double>? jitterSource = null)
    {
        _periodicInterval = periodicInterval;
        _jitter = jitter;
        _postExitDelay = postExitDelay;
        _coalesceWindow = coalesceWindow;
        _jitterSource = jitterSource ?? (() => Random.Shared.NextDouble() * 2 - 1);
    }

    /// <summary>
    /// Advances <paramref name="vendor"/>'s schedule by one tick and reports whether this tick should
    /// harvest. Called once per tick per vendor, regardless of whether a harvest fires — the idle
    /// backoff and the post-exit trigger both depend on seeing every tick's <paramref name="anyLiveLaneNow"/>
    /// reading, not just the ticks a caller happens to poll.
    /// </summary>
    public bool OnTick(string vendor, DateTimeOffset now, bool anyLiveLaneNow)
    {
        var state = _states.TryGetValue(vendor, out var existing) ? existing : _states[vendor] = new VendorState();

        var laneJustExited = state.WasLiveLastTick && !anyLiveLaneNow;
        state.WasLiveLastTick = anyLiveLaneNow;

        if (laneJustExited && state.PendingPostExitDueAt is null)
        {
            state.PendingPostExitDueAt = now + _postExitDelay + JitterFor(_postExitDelay);
        }

        if (anyLiveLaneNow && state.NextPeriodicDueAt is null)
        {
            state.NextPeriodicDueAt = now + _periodicInterval + JitterFor(_periodicInterval);
        }

        if (!anyLiveLaneNow)
        {
            // Idle backoff: no periodic schedule while no lane is live. A pending post-exit trigger
            // (just armed above, or still pending from an earlier tick) survives this -- it is a
            // one-shot obligation, not part of the periodic-while-live schedule.
            state.NextPeriodicDueAt = null;
        }

        var periodicDue = state.NextPeriodicDueAt is { } periodicAt && now >= periodicAt;
        var postExitDue = state.PendingPostExitDueAt is { } postExitAt && now >= postExitAt;

        if (!periodicDue && !postExitDue)
        {
            return false;
        }

        if (periodicDue)
        {
            state.NextPeriodicDueAt = anyLiveLaneNow ? now + _periodicInterval + JitterFor(_periodicInterval) : null;
        }

        if (postExitDue)
        {
            state.PendingPostExitDueAt = null;
        }

        // Coalesce: a trigger due within the coalesce window of the last ACTUAL harvest is satisfied
        // by that recent harvest rather than firing a second one on top of it. The due flags above are
        // still cleared/rescheduled either way -- a coalesced trigger is consumed, not deferred to the
        // next tick, since the recent harvest already produced a fresh-enough reading.
        if (state.LastHarvestedAt is { } last && now - last < _coalesceWindow)
        {
            return false;
        }

        state.LastHarvestedAt = now;
        return true;
    }

    private TimeSpan JitterFor(TimeSpan baseline)
    {
        var maxJitterSeconds = Math.Min(_jitter.TotalSeconds, baseline.TotalSeconds);
        return TimeSpan.FromSeconds(_jitterSource() * maxJitterSeconds);
    }

    private sealed class VendorState
    {
        public bool WasLiveLastTick;
        public DateTimeOffset? NextPeriodicDueAt;
        public DateTimeOffset? PendingPostExitDueAt;
        public DateTimeOffset? LastHarvestedAt;
    }
}
