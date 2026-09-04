using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Fixture coverage for <see cref="AgyUsageSlashCommandSource.Parse"/> (issue #1391) — built from the
/// two real Gemini Models rows the source's own doc comment cites; see that comment for why the doc's
/// Claude/GPT rows are excluded.
/// </summary>
public sealed class AgyUsageSlashCommandSourceTests
{
    private static readonly DateTimeOffset HarvestedAt = new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    private const string DocCapture20260828 = "Gemini Models\tWeekly Limit Remaining\t72%\t2026-08-29T19:34:12Z\n" +
        "Gemini Models\tFive Hour Limit Remaining\t42%\t2026-08-28T16:36:17Z\n";

    [Fact]
    public void Parse_TwoRealRows_PercentRemainingConvertedToPercentUsed()
    {
        var snapshot = AgyUsageSlashCommandSource.Parse(DocCapture20260828, HarvestedAt);

        Assert.Equal("agy", snapshot.Vendor);
        Assert.Equal(2, snapshot.Windows.Count);

        var weekly = Assert.Single(snapshot.Windows, w => w.Name == "Gemini Models · Weekly Limit Remaining");
        // 72% REMAINING -> 28% USED (Parse's own doc comment has the direction/why). Asserting the
        // converted value, not the raw 72, is the point.
        Assert.Equal(28, weekly.PercentUsed);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 19, 34, 12, TimeSpan.Zero), weekly.ResetsAt);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Name == "Gemini Models · Five Hour Limit Remaining");
        Assert.Equal(58, fiveHour.PercentUsed); // 42% remaining -> 58% used
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 16, 36, 17, TimeSpan.Zero), fiveHour.ResetsAt);

        // No documented agy-specific caveat text -- never fabricated to mirror claude's shape.
        Assert.Null(snapshot.Caveat);
    }

    [Fact]
    public void Parse_RemainingDirection_NearFullAccountReadsNearFullUsed()
    {
        // Control arm asserting the OPPOSITE polarity: a family reporting 5% remaining (nearly
        // exhausted) must read as 95% used, not 5% used -- the defect a silent remaining/used mix-up
        // would produce.
        const string nearlyExhausted = "Claude and GPT models\tWeekly Limit Remaining\t5%\t2026-08-29T00:00:00Z\n";

        var snapshot = AgyUsageSlashCommandSource.Parse(nearlyExhausted, HarvestedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal(95, window.PercentUsed);
    }

    [Fact]
    public void Parse_DegradedEmptyOutput_AllWindowsUnknown()
    {
        var snapshot = AgyUsageSlashCommandSource.Parse(string.Empty, HarvestedAt);

        Assert.Equal("agy", snapshot.Vendor);
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Caveat);
    }

    [Fact]
    public void Parse_PartialOutput_MalformedRowSkippedRecognizedRowKept()
    {
        const string partial = "Gemini Models\tWeekly Limit Remaining\t72%\t2026-08-29T19:34:12Z\n" +
            "this line has no tabs at all\n";

        var snapshot = AgyUsageSlashCommandSource.Parse(partial, HarvestedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("Gemini Models · Weekly Limit Remaining", window.Name);
    }
}
