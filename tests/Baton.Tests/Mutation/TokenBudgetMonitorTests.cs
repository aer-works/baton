using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="TokenBudgetMonitor"/> (#1623 ruling addendum, revised by #1682, issue title
/// in <c>TokenBudgetMonitor</c>'s own doc) — pure accumulation logic against real per-vendor parsers,
/// no process involved. <c>MutationInterfaceTests</c> covers the wiring into a live dispatch.
/// </summary>
public sealed class TokenBudgetMonitorTests
{
    [Fact]
    public void ArrestRequested_fires_once_the_running_SUM_of_billed_tokens_crosses_the_budget()
    {
        // #1682: billed is additive across turns (input + output per line), NOT a level -- unlike the
        // pre-#1682 arithmetic this replaces. Turn 1 billed 400+100=500; turn 2 billed 500+600=1100;
        // running sum 500+1100=1600 >= 1000 crosses on turn 2.
        var monitor = new TokenBudgetMonitor(budget: 1000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":400,"output_tokens":100}}}""");
        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":500,"output_tokens":600}}}""");

        Assert.True(monitor.Arrested);
        Assert.True(monitor.ArrestRequested.IsCancellationRequested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        Assert.Equal(1600, monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void A_budget_never_reached_leaves_the_monitor_unarrested()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":400,"output_tokens":100}}}""");

        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);
    }

    [Fact]
    public void SnapshotUsage_reports_the_context_level_display_field_unchanged_and_sums_billed_separately()
    {
        // Pins the two fields' distinct meaning: ContextLevelTokens is unchanged, BilledTokens is new.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":100,"output_tokens":10}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":200,"output_tokens":20}}}""");

        var usage = monitor.SnapshotUsage();

        Assert.Equal(200, usage.TokensIn);
        Assert.Equal(200, usage.ContextLevelTokens);
        Assert.Equal(30, usage.TokensOut);
        Assert.Equal(330, usage.BilledTokens);
    }

    [Fact]
    public void Billed_tokens_include_cache_creation_but_never_thinking()
    {
        // Pins the exclusion StandardWorkerUsageParsersTests' real-line test proves against actual
        // vendor data (spec/baton.md §3) -- cache_creation counts here, thinking never does.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":12066,"cache_read_input_tokens":15092,"output_tokens":4}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Equal(2 + 4 + 12066, usage.BilledTokens);
        Assert.Equal(15092, usage.CacheReadTokens);
    }

    [Fact]
    public void CacheReadTokens_on_the_snapshot_is_a_running_sum_not_the_latest_reading()
    {
        // #1682: display-only Σ across every incremental line, unlike ContextLevelTokens which stays a level.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":100}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":50}}}""");

        Assert.Equal(150, monitor.SnapshotUsage().CacheReadTokens);
    }

    [Fact]
    public void A_room_that_never_crosses_the_billed_budget_can_still_be_caught_by_the_tool_step_cap()
    {
        // The other #1682 evidence room never crosses a plausible budget at all (spec/baton.md §3) --
        // this pins that the cap alone still catches that shape.
        var monitor = new TokenBudgetMonitor(budget: 600_000, maxToolSteps: 80, new AgyUsageParser());

        for (var i = 0; i < 80; i++)
        {
            monitor.OnStdoutLine(
                """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""");
        }

        Assert.False(monitor.Arrested);

        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(81, monitor.SnapshotToolStepCount());
        Assert.Null(monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void The_tool_step_cap_fires_at_cap_plus_one_with_zero_usage_lines()
    {
        // Must 6c: independent of usage parsing entirely -- every line here fails TryParseIncrementalUsage.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 5, new ClaudeUsageParser());

        for (var i = 0; i < 5; i++)
        {
            monitor.OnStdoutLine(
                """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}}]}}""");
            Assert.False(monitor.Arrested);
        }

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}}]}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(6, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void A_multi_tool_claude_turn_counts_every_block_toward_the_cap()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 1, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"},{"type":"tool_use","name":"Read"}]}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(2, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void A_monitor_with_only_a_tool_step_cap_and_no_budget_still_watches()
    {
        // #1682: before this issue a monitor required a budget to be constructed at all.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 2, new AgyUsageParser());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"b"}}""");
        Assert.False(monitor.Arrested);
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"c"}}""");
        Assert.True(monitor.Arrested);
    }

    [Fact]
    public void A_monitor_with_neither_budget_nor_cap_never_arrests()
    {
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, new ClaudeUsageParser());

        for (var i = 0; i < 1000; i++)
        {
            monitor.OnStdoutLine(
                """{"type":"assistant","message":{"usage":{"input_tokens":100000,"output_tokens":100000}}}""");
        }

        Assert.False(monitor.Arrested);
    }

    [Fact]
    public void Whichever_trigger_fires_first_wins_and_the_other_never_overwrites_it()
    {
        // Tool-step cap set low enough to fire before the budget, on a stream that would also cross
        // the budget eventually -- the reason recorded must be the one that actually fired first.
        var monitor = new TokenBudgetMonitor(budget: 100, maxToolSteps: 1, new AgyUsageParser());

        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"b"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);

        // Further lines, including ones that would cross the budget, never flip the recorded reason.
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","usage":{"input_tokens":1000,"output_tokens":1000}}}""");
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
    }

    [Fact]
    public void Lines_that_do_not_parse_as_usage_are_ignored()
    {
        var monitor = new TokenBudgetMonitor(budget: 10, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("not json");
        monitor.OnStdoutLine("""{"type":"system","subtype":"init"}""");

        Assert.False(monitor.Arrested);
        Assert.Null(monitor.SnapshotUsage().TokensIn);
        Assert.Equal(0, monitor.SnapshotUsage().ContextLevelTokens);
        Assert.Null(monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void SnapshotLastToolNames_keeps_only_the_most_recent_names_bounded()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new AgyUsageParser());

        for (var i = 0; i < 15; i++)
        {
            monitor.OnStdoutLine(
                "{\"event\":\"step_update\",\"step_update\":{\"state\":\"ACTIVE\",\"step_type\":\"tool\",\"tool_name\":\"tool-" + i + "\"}}");
        }

        var names = monitor.SnapshotLastToolNames();

        Assert.Equal(10, names.Count);
        Assert.Equal("tool-14", names[^1]);
        Assert.Equal("tool-5", names[0]);
    }

    [Fact]
    public void The_terminal_result_line_is_never_double_counted_as_incremental_usage()
    {
        // Polarity: TryParseIncrementalUsage deliberately returns false for a "type":"result" line
        // (StandardWorkerUsageParsersTests pins the parser directly); this pins the monitor honours
        // that and never sums the terminal figure a second time.
        var monitor = new TokenBudgetMonitor(budget: 10, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"result","usage":{"input_tokens":100,"output_tokens":100}}""");

        Assert.False(monitor.Arrested);
        Assert.Null(monitor.SnapshotUsage().TokensIn);
        Assert.Equal(0, monitor.SnapshotUsage().ContextLevelTokens);
    }
}
