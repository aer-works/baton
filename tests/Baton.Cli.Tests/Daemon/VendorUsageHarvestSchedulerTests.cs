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

    /// <summary>
    /// Drives the exact sequence the coalesce rule exists for: a periodic harvest, then a lane exit
    /// that arms a post-exit trigger, then the tick on which that trigger comes due. Returns whether
    /// the post-exit tick harvested, plus that tick's gap from the periodic harvest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="jitter"/> is what moves the post-exit trigger across the coalesce boundary,
    /// and it has to: <c>JitterFor</c> clamps the budget to <c>min(jitter, baseline)</c>, so with the
    /// SHIPPED constants (PostExit == Coalesce == 60s) and zero jitter the post-exit instant is
    /// always <c>exit + 60s</c>, and the exit tick is always strictly after the periodic harvest (a
    /// periodic harvest needs <c>anyLiveLaneNow</c>, an exit needs <c>!anyLiveLaneNow</c>) -- so the
    /// gap always exceeds 60s and the branch is only reachable on negative jitter. That is the
    /// narrow corner these two arms exercise; it is a property of the shipped constants, disclosed
    /// rather than tuned away, since changing a background service's cadence is a change to real
    /// vendor session traffic that #1869's review scoped out.
    /// </para>
    /// </remarks>
    private static (bool Harvested, TimeSpan GapFromPriorHarvest) RunPeriodicThenPostExit(
        double jitter, TimeSpan exitAfterHarvest)
    {
        var scheduler = new VendorUsageHarvestScheduler(Periodic, Jitter, PostExit, Coalesce, jitterSource: () => jitter);
        var now = Start;

        Assert.False(scheduler.OnTick("claude", now, anyLiveLaneNow: true));

        // Periodic due = interval + min(Jitter, interval) * jitter.
        var harvestedAt = Start + Periodic + TimeSpan.FromSeconds(Math.Min(Jitter.TotalSeconds, Periodic.TotalSeconds) * jitter);
        Assert.True(scheduler.OnTick("claude", harvestedAt, anyLiveLaneNow: true));

        // The lane exits -- arms the post-exit trigger, harvests nothing itself.
        var exitedAt = harvestedAt + exitAfterHarvest;
        Assert.False(scheduler.OnTick("claude", exitedAt, anyLiveLaneNow: false));

        // Post-exit due = delay + min(Jitter, delay) * jitter.
        var postExitDueAt = exitedAt + PostExit + TimeSpan.FromSeconds(Math.Min(Jitter.TotalSeconds, PostExit.TotalSeconds) * jitter);
        var harvested = scheduler.OnTick("claude", postExitDueAt, anyLiveLaneNow: false);

        if (!harvested)
        {
            // Discriminator: prove the trigger was DUE and got coalesced away, not merely early. A
            // consumed trigger never fires later; a deferred one would fire on this next tick, which
            // is past both the due instant and the coalesce window.
            Assert.False(scheduler.OnTick("claude", postExitDueAt + Coalesce + Periodic, anyLiveLaneNow: false));
        }

        return (harvested, postExitDueAt - harvestedAt);
    }

    [Fact]
    public void PostExitTriggerDueInsideCoalesceWindow_IsCoalescedIntoTheRecentHarvest()
    {
        // jitter -0.5 pulls the post-exit instant to exit+30s; the exit lands 10s after the periodic
        // harvest, so the trigger comes due 40s after it -- inside the 60s window.
        var (harvested, gap) = RunPeriodicThenPostExit(jitter: -0.5, exitAfterHarvest: TimeSpan.FromSeconds(10));

        Assert.True(gap < Coalesce, $"fixture must place the trigger INSIDE the window; gap was {gap}");
        Assert.False(harvested);
    }

    [Fact]
    public void PostExitTriggerDueOutsideCoalesceWindow_FiresItsOwnHarvest()
    {
        // Polarity arm, identical sequence: the same trigger one second the other side of the
        // boundary (61s after the harvest) must fire. Without this, the assertion above is satisfied
        // by a scheduler that never harvests at all.
        var (harvested, gap) = RunPeriodicThenPostExit(jitter: 0, exitAfterHarvest: TimeSpan.FromSeconds(1));

        Assert.True(gap > Coalesce, $"fixture must place the trigger OUTSIDE the window; gap was {gap}");
        Assert.True(harvested);
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
