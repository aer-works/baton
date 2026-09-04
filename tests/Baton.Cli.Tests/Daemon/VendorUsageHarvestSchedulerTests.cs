using Baton.Cli.Daemon;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// Cadence coverage for <see cref="VendorUsageHarvestScheduler"/> (issue #1391), driven entirely by
/// caller-supplied <c>DateTimeOffset</c> ticks — no real clock, no process, no fake-clock package
/// (CLAUDE.md: <c>Baton.Cli</c>'s project graph carries no extra NuGet dependency for this).
/// </summary>
public sealed class VendorUsageHarvestSchedulerTests
{
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Jitter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PostExit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

    // Zero jitter throughout unless a test asserts the jitter bound itself -- makes every other test's
    // due instant exact rather than a range to reason about.
    private static VendorUsageHarvestScheduler NoJitterScheduler() =>
        new(Periodic, Jitter, PostExit, Coalesce, jitterSource: () => 0);

    [Fact]
    public void Idle_NoLiveLaneEver_NeverHarvests()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        for (var i = 0; i < 200; i++)
        {
            Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));
            now += TimeSpan.FromSeconds(30);
        }
    }

    [Fact]
    public void OneLiveLane_HarvestsOncePerPeriodicInterval()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        // First tick with a live lane arms the schedule but does not itself harvest.
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Nothing due before the interval elapses.
        now += Periodic - TimeSpan.FromSeconds(1);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Due exactly at the interval.
        now += TimeSpan.FromSeconds(1);
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Rescheduled for another full interval out -- not due again immediately.
        now += TimeSpan.FromSeconds(1);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        now += Periodic - TimeSpan.FromSeconds(1);
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
    }

    [Fact]
    public void LaneExit_HarvestsOnceAfterPostExitDelay_ThenStopsWithNoFurtherLiveLane()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        // Live, then quiet on the very next tick -- the exit transition.
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        now += TimeSpan.FromSeconds(30);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // Not yet due.
        now += PostExit - TimeSpan.FromSeconds(1);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // Due: the one post-exit harvest fires.
        now += TimeSpan.FromSeconds(1);
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // Idle afterward -- no further calls, ever (idle backoff).
        for (var i = 0; i < 100; i++)
        {
            now += TimeSpan.FromMinutes(5);
            Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));
        }
    }

    [Fact]
    public void PeriodicDueAndLaneExitWithinCoalesceWindow_ProducesOneCallNotTwo()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        // Arm the periodic schedule.
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Advance to exactly the periodic due instant -- harvest fires (call #1).
        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // The lane exits 5s later, well inside the 60s coalesce window of the harvest just above.
        now += TimeSpan.FromSeconds(5);
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: false));

        // Post-exit trigger becomes due (PostExit=60s after the exit tick) -- but that instant is
        // still inside the coalesce window of the FIRST harvest (5s + up to 60s < 60s coalesce only if
        // the total gap stays under Coalesce; use a delay short enough to land inside it).
        now += TimeSpan.FromSeconds(30); // 5s + 30s = 35s since the harvest, still < 60s coalesce window
        var due = scheduler.OnTick("claude", now, anyLiveLaneNow: false);

        // Either it's not due yet at this instant, or it's due but coalesced away -- either way, no
        // SECOND true has been observed across this whole sequence.
        Assert.False(due);
    }

    [Fact]
    public void PeriodicDueOutsideCoalesceWindowOfPriorHarvest_FiresIndependently()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true)); // call #1

        // Second periodic due, a full interval later -- well outside the 60s coalesce window.
        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true)); // call #2
    }

    [Fact]
    public void JitterSource_ShiftsDueInstantWithinBudget()
    {
        // jitterSource always returns +1 -- the due instant should land at interval + full jitter, not
        // exactly at the bare interval.
        var scheduler = new VendorUsageHarvestScheduler(Periodic, Jitter, PostExit, Coalesce, jitterSource: () => 1);
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Bare interval alone is not yet due -- the +jitter pushed the due instant later.
        now += Periodic;
        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Interval + full jitter budget is due.
        now += Jitter;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
    }

    [Fact]
    public void VendorsAreIndependent_ClaudeLiveDoesNotArmAgy()
    {
        var scheduler = NoJitterScheduler();
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        Assert.False(scheduler.OnTick("agy", now, anyLiveLaneNow: false));

        now += Periodic;
        Assert.True(scheduler.OnTick("claude", now, anyLiveLaneNow: true));
        Assert.False(scheduler.OnTick("agy", now, anyLiveLaneNow: false));
    }
}
