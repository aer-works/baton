namespace Baton.Vendors.Tests;

/// <summary>
/// #1848's admission gate. Every arm drives the REAL vendor parsers
/// (<see cref="ClaudeUsageSlashCommandSource.Parse"/>/<see cref="AgyUsageSlashCommandSource.Parse"/>)
/// rather than hand-built <see cref="VendorUsageWindow"/> values: the window-name table is the whole
/// coupling between #1869's harvest and this gate, and a test that constructs the window names itself
/// would keep passing while a parser renamed them out from under it.
/// </summary>
public class RunwayGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static VendorUsageSnapshot Claude(int weekAllModelsPct, int sessionPct, int weekFablePct = 3) =>
        ClaudeUsageSlashCommandSource.Parse(
            $"""
            Current session: {sessionPct}% used · resets Sep 5, 5:59pm (America/New_York)
            Current week (all models): {weekAllModelsPct}% used · resets Sep 9, 5:59am (America/New_York)
            Current week (Fable): {weekFablePct}% used
            Approximate, based on local sessions on this machine — does not include other devices or claude.ai.
            """,
            Now);

    private static VendorUsageSnapshot Agy(string weeklyRemaining, string fiveHourRemaining) =>
        AgyUsageSlashCommandSource.Parse(
            $"Gemini Models\tWeekly Limit Remaining\t{weeklyRemaining}\t2026-09-09T19:34:12Z\n"
            + $"Gemini Models\tFive Hour Limit Remaining\t{fiveHourRemaining}\t2026-09-05T19:34:12Z\n",
            Now);

    private static RunwayDecision Evaluate(string vendor, VendorUsageSnapshot? snapshot, RunwayThresholds? thresholds = null) =>
        RunwayGate.Evaluate(vendor, snapshot, thresholds ?? new RunwayThresholds(), Now);

    // ---- thresholds, both polarities, both axes -------------------------------------------------

    [Fact]
    public void Claude_week_one_below_the_threshold_admits()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 84, sessionPct: 10));

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Claude_week_at_the_threshold_holds()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 85, sessionPct: 10));

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("week (all models)", decision.Reason);
        Assert.Contains("85%", decision.Reason);
    }

    [Fact]
    public void Claude_session_one_below_the_threshold_admits()
    {
        Assert.Equal(RunwayDisposition.Admit, Evaluate("claude", Claude(weekAllModelsPct: 10, sessionPct: 89)).Disposition);
    }

    [Fact]
    public void Claude_session_at_the_threshold_holds()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 10, sessionPct: 90));

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("'session'", decision.Reason);
    }

    /// <summary>
    /// The cross-axis control: both single-axis boundary tests above still pass if the week threshold
    /// were wired to the session window, so each axis is also driven with the OTHER one at zero.
    /// </summary>
    [Fact]
    public void Each_axis_holds_on_its_own_window()
    {
        var weekOnly = Evaluate("claude", Claude(weekAllModelsPct: 90, sessionPct: 0));
        var sessionOnly = Evaluate("claude", Claude(weekAllModelsPct: 0, sessionPct: 95));

        Assert.Contains("week (all models)", weekOnly.Reason);
        Assert.Contains("'session'", sessionOnly.Reason);
        Assert.Equal(RunwayDisposition.Hold, weekOnly.Disposition);
        Assert.Equal(RunwayDisposition.Hold, sessionOnly.Disposition);
    }

    /// <summary>
    /// The polarity arm for the excluded window (operator ruling, 2026-09-05; spec/baton.md §7). A
    /// prefix/contains match on "week" would silently pull it into the decision.
    /// </summary>
    [Fact]
    public void Claude_week_Fable_at_99_percent_does_not_hold()
    {
        var decision = Evaluate("claude", Claude(weekAllModelsPct: 10, sessionPct: 10, weekFablePct: 99));

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.DoesNotContain(decision.Counters, c => c.Window.Contains("Fable", StringComparison.Ordinal));
    }

    [Fact]
    public void Configured_thresholds_replace_the_defaults()
    {
        var thresholds = new RunwayThresholds(WeekHoldPct: 50, SessionHoldPct: 60);

        Assert.Equal(RunwayDisposition.Hold, Evaluate("claude", Claude(60, 10), thresholds).Disposition);
        Assert.Equal(RunwayDisposition.Admit, Evaluate("claude", Claude(49, 59), thresholds).Disposition);
    }

    // ---- agy, and per-vendor isolation ------------------------------------------------------------

    [Fact]
    public void Agy_windows_are_matched_by_their_own_composed_names()
    {
        // 12% remaining is 88% used -- past the 85% week threshold.
        var held = Evaluate("agy", Agy(weeklyRemaining: "12%", fiveHourRemaining: "80%"));
        var admitted = Evaluate("agy", Agy(weeklyRemaining: "80%", fiveHourRemaining: "80%"));

        Assert.Equal(RunwayDisposition.Hold, held.Disposition);
        Assert.Contains("Weekly Limit", held.Reason);
        Assert.Equal(RunwayDisposition.Admit, admitted.Disposition);
        Assert.Equal(2, admitted.Counters.Count);
    }

    [Fact]
    public void A_claude_hold_does_not_hold_agy()
    {
        Assert.Equal(RunwayDisposition.Hold, Evaluate("claude", Claude(99, 99)).Disposition);
        Assert.Equal(RunwayDisposition.Admit, Evaluate("agy", Agy("90%", "90%")).Disposition);
    }

    // ---- every unreadable shape holds -------------------------------------------------------------

    [Fact]
    public void A_missing_snapshot_holds()
    {
        var decision = Evaluate("claude", snapshot: null);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("no readable usage snapshot", decision.Reason);
        Assert.Empty(decision.Counters);
    }

    [Fact]
    public void A_snapshot_whose_output_parsed_no_windows_holds()
    {
        var unrecognizable = ClaudeUsageSlashCommandSource.Parse("Usage is unavailable right now.", Now);

        var decision = Evaluate("claude", unrecognizable);

        Assert.Empty(unrecognizable.Windows);
        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("not readable", decision.Reason);
    }

    [Fact]
    public void A_recognized_window_with_no_percentage_holds()
    {
        // agy's percent is derived from its own "Remaining" column; a non-numeric column leaves it
        // null rather than zero (the "unparsed -> unknown, never a number" ruling), and unknown holds.
        var snapshot = Agy(weeklyRemaining: "n/a", fiveHourRemaining: "80%");

        var decision = Evaluate("agy", snapshot);

        Assert.Null(snapshot.Windows.Single(w => w.Name.Contains("Weekly", StringComparison.Ordinal)).PercentUsed);
        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("no percentage", decision.Reason);
    }

    [Fact]
    public void A_snapshot_older_than_the_age_limit_holds_however_low_the_counters_are()
    {
        var stale = ClaudeUsageSlashCommandSource.Parse(
            "Current session: 0% used\nCurrent week (all models): 0% used\n", Now.AddHours(-7));

        var decision = RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now);

        Assert.Equal(RunwayDisposition.Hold, decision.Disposition);
        Assert.Contains("stale counter", decision.Reason);

        // Control: the identical snapshot inside the age limit admits, so the arm above is about age.
        Assert.Equal(RunwayDisposition.Admit, RunwayGate.Evaluate("claude", stale, new RunwayThresholds(), Now.AddHours(-5)).Disposition);
    }

    // ---- unmeasured vendor -------------------------------------------------------------------------

    [Fact]
    public void A_vendor_with_no_usage_source_is_admitted_as_unmeasured()
    {
        var decision = Evaluate("codex", snapshot: null);

        Assert.Equal(RunwayDisposition.Admit, decision.Disposition);
        Assert.Equal(RunwayGate.UnmeasuredReason, decision.Reason);
        Assert.DoesNotContain("codex", RunwayGate.MeasuredVendors);
    }
}
