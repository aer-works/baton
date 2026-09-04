using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Fixture coverage for <see cref="ClaudeUsageSlashCommandSource.Parse"/> (issue #1391). The full-report
/// fixture is captured verbatim from this issue's own step-5 live check (<c>claude -p "/usage"
/// --output-format text</c>, 2.1.258, 2026-09-04) — the doc's 2026-08-28 capture
/// (docs/vendor-capabilities.md) is the degraded-Fable-line control below, since that capture's
/// week(Fable) line carries no resets clause where the live one does.
/// </summary>
public sealed class ClaudeUsageSlashCommandSourceTests
{
    private static readonly DateTimeOffset HarvestedAt = new(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);

    private const string LiveCapture20260904 = """
        You are currently using your subscription to power your Claude Code usage

        Current session: 8% used · resets Sep 4, 9:19pm (America/New_York)
        Current week (all models): 2% used · resets Sep 7, 5:59am (America/New_York)
        Current week (Fable): 3% used · resets Sep 7, 5:59am (America/New_York)

        What's contributing to your limits usage?
        Approximate, based on local sessions on this machine — does not include other devices or claude.ai. Behaviors are independent characteristics, not a breakdown.

        Last 24h · 9603 requests · 147 sessions
          73% of your usage was at >150k context
        """;

    [Fact]
    public void Parse_LiveCapture_ReturnsAllThreeWindowsWithResetsAndCaveat()
    {
        var snapshot = ClaudeUsageSlashCommandSource.Parse(LiveCapture20260904, HarvestedAt);

        Assert.Equal("claude", snapshot.Vendor);
        Assert.Equal(HarvestedAt, snapshot.HarvestedAt);
        Assert.Equal(3, snapshot.Windows.Count);

        var session = Assert.Single(snapshot.Windows, w => w.Name == "session");
        Assert.Equal(8, session.PercentUsed);
        Assert.NotNull(session.ResetsAt);
        Assert.Equal("Current session: 8% used · resets Sep 4, 9:19pm (America/New_York)", session.RawLine);

        var weekAll = Assert.Single(snapshot.Windows, w => w.Name == "week (all models)");
        Assert.Equal(2, weekAll.PercentUsed);
        Assert.NotNull(weekAll.ResetsAt);

        var weekFable = Assert.Single(snapshot.Windows, w => w.Name == "week (Fable)");
        Assert.Equal(3, weekFable.PercentUsed);
        Assert.NotNull(weekFable.ResetsAt);

        Assert.NotNull(snapshot.Caveat);
        Assert.StartsWith("Approximate,", snapshot.Caveat);
        Assert.Contains("does not include other devices or claude.ai.", snapshot.Caveat);
    }

    [Fact]
    public void Parse_ResetsAtResolvesToNearFutureInstant_NotPastYear()
    {
        var snapshot = ClaudeUsageSlashCommandSource.Parse(LiveCapture20260904, HarvestedAt);
        var session = snapshot.Windows.Single(w => w.Name == "session");

        // "Sep 4, 9:19pm (America/New_York)" resolved against a 2026-09-04T18:00Z harvest --
        // must land within a few hours of the harvest, in 2026, never rolled to 2027.
        Assert.Equal(2026, session.ResetsAt!.Value.Year);
        Assert.True(session.ResetsAt.Value >= HarvestedAt.AddDays(-3));
        Assert.True(session.ResetsAt.Value <= HarvestedAt.AddDays(3));
    }

    // Doc-measured 2026-08-28 capture (docs/vendor-capabilities.md "claude — everything needed,
    // headlessly"): the week(Fable) line carries NO resets clause, unlike the live 2026-09-04 capture
    // above -- proves the resets clause is read as optional per-line, not assumed present on every
    // window.
    private const string DocCapture20260828 = """
        Current session: 21% used · resets Jul 25, 12:09am (America/New_York)
        Current week (all models): 67% used · resets Jul 27, 5:59am (America/New_York)
        Current week (Fable): 0% used
        Last 24h · 1811 requests · 21 sessions
          88% of your usage came from subagent-heavy sessions
          82% of your usage was at >150k context
        """;

    [Fact]
    public void Parse_FableLineWithNoResetsClause_WindowPresentResetsAtAbsent()
    {
        var harvestedAt = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);
        var snapshot = ClaudeUsageSlashCommandSource.Parse(DocCapture20260828, harvestedAt);

        var weekFable = Assert.Single(snapshot.Windows, w => w.Name == "week (Fable)");
        Assert.Equal(0, weekFable.PercentUsed);
        Assert.Null(weekFable.ResetsAt);
        Assert.Equal("Current week (Fable): 0% used", weekFable.RawLine);

        // No "Approximate," line in this fixture -- caveat stays null rather than fabricated.
        Assert.Null(snapshot.Caveat);
    }

    [Fact]
    public void Parse_DegradedEmptyOutput_AllWindowsUnknown()
    {
        var snapshot = ClaudeUsageSlashCommandSource.Parse(string.Empty, HarvestedAt);

        Assert.Equal("claude", snapshot.Vendor);
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Caveat);
    }

    [Fact]
    public void Parse_PartialOutput_OnlyRecognizedWindowsPresent()
    {
        const string partial = """
            Current session: 8% used · resets Sep 4, 9:19pm (America/New_York)
            Some unrelated banner line that is not a usage window at all.
            """;

        var snapshot = ClaudeUsageSlashCommandSource.Parse(partial, HarvestedAt);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("session", window.Name);
        Assert.Equal(8, window.PercentUsed);
    }
}
