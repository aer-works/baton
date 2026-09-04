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

        var weekly = Assert.Single(snapshot.Windows, w => w.Name == "Gemini Models · Weekly Limit");
        // 72% REMAINING -> 28% USED (Parse's own doc comment has the direction/why). Asserting the
        // converted value, not the raw 72, is the point.
        Assert.Equal(28, weekly.PercentUsed);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 19, 34, 12, TimeSpan.Zero), weekly.ResetsAt);

        var fiveHour = Assert.Single(snapshot.Windows, w => w.Name == "Gemini Models · Five Hour Limit");
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
    public void Parse_WindowNameCarriesTheSenseOfTheNumber_NeverSaysRemainingBesideAPercentUsed()
    {
        // #1869 review, HIGH: PercentUsed is percent USED, so a name still ending in agy's own word
        // "Remaining" renders in Fleet Glass as "Weekly Limit Remaining  95% used" -- an operator
        // reads 95% LEFT when 5% is left. Both directions are asserted: the label must have dropped
        // the word, AND the raw vendor line must still carry it, since an assertion on absence alone
        // would also pass if RawLine had been scrubbed or emptied.
        var snapshot = AgyUsageSlashCommandSource.Parse(
            DocCapture20260828 + "Claude and GPT models\tWeekly Limit Remaining\t5%\t2026-08-29T00:00:00Z\n",
            HarvestedAt);

        Assert.Equal(3, snapshot.Windows.Count);
        foreach (var window in snapshot.Windows)
        {
            Assert.NotNull(window.PercentUsed);
            Assert.DoesNotContain("Remaining", window.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Remaining", window.RawLine, StringComparison.Ordinal);
        }

        // The rest of the vendor's own wording survives -- only the contradicting token is dropped.
        Assert.Contains(snapshot.Windows, w => w.Name == "Claude and GPT models · Weekly Limit");
    }

    [Fact]
    public void Parse_WindowTextIsNothingButTheStrippedWord_FallsBackToTheFamilyAlone()
    {
        // Degenerate arm for StripRemaining: stripping must not leave a dangling "family · ".
        var snapshot = AgyUsageSlashCommandSource.Parse(
            "Gemini Models\tRemaining\t72%\t2026-08-29T19:34:12Z\n", HarvestedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("Gemini Models", window.Name);
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
        Assert.Equal("Gemini Models · Weekly Limit", window.Name);
    }

    [Fact]
    public async Task ReadAsync_CommandExitsNonZeroWithJunkOnStdout_ReturnsNullNotAnEmptySnapshot()
    {
        // #1869 review, MEDIUM: before this fix nothing subscribed to the Exited event, so an errored
        // vendor CLI parsed to a zero-window snapshot that the harvester then wrote OVER the last
        // good one. A real child process is spawned (not a stubbed runner) precisely because the
        // defect was in the event wiring, which a stub cannot exercise.
        var source = new AgyUsageSlashCommandSource(
            UsageSourceShell.Program, UsageSourceShell.JunkThenExit(exitCode: 1));

        Assert.Null(await source.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_CommandExitsZeroWithTheDocFixture_ParsesItsWindows()
    {
        // Polarity arm for the test above (its claude counterpart's comment has why that shape
        // discriminates). The fixture goes through a file rather than an echo because agy's rows are
        // TAB-separated and a shell's own argument splitting would eat them.
        var fixturePath = Path.Combine(Path.GetTempPath(), $"baton-agy-usage-fixture-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(fixturePath, DocCapture20260828, TestContext.Current.CancellationToken);
        try
        {
            var source = new AgyUsageSlashCommandSource(
                UsageSourceShell.Program, UsageSourceShell.PrintFile(fixturePath));

            var snapshot = await source.ReadAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(snapshot);
            Assert.Equal("agy", snapshot!.Vendor);
            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Contains(snapshot.Windows, w => w.Name == "Gemini Models · Weekly Limit" && w.PercentUsed == 28);
        }
        finally
        {
            FileCleanup.Delete(fixturePath);
        }
    }
}
