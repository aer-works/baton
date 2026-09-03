using Baton.Cli.Daemon;
using Baton.Status;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// <c>WatchSweep.GetReaperRetention</c> (M1, fix round, spec/baton.md §2) — same
/// <see cref="BatonEnvironmentSnapshot.BeginScope"/> isolation and clamp-pinning shape as
/// <c>RoomRetentionSweepTests</c>. The reaper's own removal behavior (fired-and-old removed,
/// fired-and-recent kept, pending-on-a-deleted-room removed) is <c>WatchStoreTests</c>'
/// <c>ReapAsync_*</c> tests, not repeated here.
/// </summary>
public sealed class WatchSweepTests
{
    [Fact]
    public void GetReaperRetention_NoOverride_ReturnsThePlaceholderDefault()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank);

        Assert.Equal(WatchSweep.PlaceholderDefaultReaperRetention, WatchSweep.GetReaperRetention());
    }

    [Fact]
    public void GetReaperRetention_ClampsPathologicalValue_InsteadOfOverflowing()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { WatchReaperRetentionHoursOverride = "1e300" });

        Assert.Equal(WatchSweep.MaxReaperRetention, WatchSweep.GetReaperRetention());
    }

    [Fact]
    public void GetReaperRetention_LiftsSubHourValue_ToMinReaperRetention()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { WatchReaperRetentionHoursOverride = "1e-9" });

        Assert.Equal(WatchSweep.MinReaperRetention, WatchSweep.GetReaperRetention());
    }

    [Fact]
    public void GetReaperRetention_ValidOverride_IsHonored()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { WatchReaperRetentionHoursOverride = "48" });

        Assert.Equal(TimeSpan.FromHours(48), WatchSweep.GetReaperRetention());
    }
}
