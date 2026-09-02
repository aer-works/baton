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
        // #1682: billed is additive across turns, NOT a level -- unlike the pre-#1682 arithmetic this
        // replaces. #1706: on claude the only measurable billed component is cache_creation (the
        // input/output figures on this line are placeholders and are no longer read), so the two turns
        // bill 500 and 1100; running sum 1600 >= 1000 crosses on turn 2.
        var monitor = new TokenBudgetMonitor(budget: 1000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":500,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":1100,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        Assert.True(monitor.Arrested);
        Assert.True(monitor.ArrestRequested.IsCancellationRequested);
        Assert.Equal(ArrestReason.TokenBudget, monitor.ArrestReasonValue);
        Assert.Equal(1600, monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void A_budget_never_reached_leaves_the_monitor_unarrested()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":500,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);
    }

    [Fact]
    public void SnapshotUsage_reports_the_context_level_display_field_unchanged_and_sums_billed_separately()
    {
        // Pins the two fields' distinct meaning: ContextLevelTokens is a LEVEL (replaced each line),
        // BilledTokens is a Σ. #1706: on claude the level is now cache_read + cache_creation, the
        // placeholder input_tokens having been dropped from the reading entirely -- and TokensIn/
        // TokensOut are null on the snapshot for the same reason.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":100,"output_tokens":3}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":20,"cache_read_input_tokens":200,"output_tokens":3}}}""");

        var usage = monitor.SnapshotUsage();

        Assert.Null(usage.TokensIn);
        Assert.Equal(220, usage.ContextLevelTokens);
        Assert.Null(usage.TokensOut);
        Assert.Equal(30, usage.BilledTokens);
        Assert.Equal(300, usage.CacheReadTokens);
        Assert.True(usage.BilledIsFloor);
    }

    [Fact]
    public void Billed_tokens_include_cache_creation_but_never_thinking()
    {
        // Pins the exclusion StandardWorkerUsageParsersTests' real-line test proves against actual
        // vendor data (spec/baton.md §3) -- cache_creation counts here, thinking never does. #1706:
        // the line's own input_tokens (2) and output_tokens (4) no longer contribute either, being
        // placeholders, so the whole billed figure on this vendor is the cache-creation column.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":12066,"cache_read_input_tokens":15092,"output_tokens":4}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Equal(12066, usage.BilledTokens);
        Assert.Equal(15092, usage.CacheReadTokens);
    }

    [Fact]
    public void CacheReadTokens_on_the_snapshot_is_a_running_sum_not_the_latest_reading()
    {
        // #1682: display-only Σ across every incremental line, unlike ContextLevelTokens which stays a level.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":3,"cache_read_input_tokens":100}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":3,"cache_read_input_tokens":50}}}""");

        Assert.Equal(150, monitor.SnapshotUsage().CacheReadTokens);
    }

    [Fact]
    public void A_room_that_never_crosses_the_billed_budget_can_still_be_caught_by_the_tool_step_cap()
    {
        // The other #1682 evidence room never crosses a plausible budget at all (spec/baton.md §3) --
        // this pins that the cap alone still catches that shape. #1686 review F2: agy's cap counts a
        // REAL tool call only at its terminal (DONE/ERROR) line, not its ACTIVE heartbeat -- each call
        // here fires both, only the DONE line increments the count.
        var monitor = new TokenBudgetMonitor(budget: 600_000, maxToolSteps: 80, new AgyUsageParser());

        for (var i = 0; i < 80; i++)
        {
            monitor.OnStdoutLine(
                """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""");
            monitor.OnStdoutLine(
                """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command"}}""");
        }

        Assert.False(monitor.Arrested);

        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""");
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"run_command"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(81, monitor.SnapshotToolStepCount());
        Assert.Null(monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void Agy_tool_step_cap_counts_the_terminal_line_only_not_the_ACTIVE_heartbeat()
    {
        // #1686 review F2: the pre-fix unit double-counted (ACTIVE + terminal) each real call. This
        // pins the fixed unit directly -- 3 ACTIVE-only heartbeats for calls that never reach a
        // terminal state must not arm a cap of 2.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 2, new AgyUsageParser());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"b"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"c"}}""");

        Assert.False(monitor.Arrested);
        Assert.Equal(0, monitor.SnapshotToolStepCount());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"b"}}""");
        Assert.False(monitor.Arrested);
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"ERROR","step_type":"tool","tool_name":"c"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);
        Assert.Equal(3, monitor.SnapshotToolStepCount());
    }

    [Fact]
    public void Claude_billed_tokens_dedupe_a_repeated_message_id_instead_of_summing_it_twice()
    {
        // #1686 review F6 -- ClaudeUsageParser.TryParseIncrementalUsage's own doc has the measured
        // shape this reproduces; summing every line without deduping over-counts.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":110,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":110,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":110,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_2","usage":{"input_tokens":2,"cache_creation_input_tokens":55,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Equal(110 + 55, usage.BilledTokens);
        // #1706: the placeholder output column is not read at all, so there is no Σ of it to report.
        Assert.Null(usage.TokensOut);
    }

    [Fact]
    public void The_dedupe_premise_holds_on_a_real_consecutive_pair_from_room_3dc5e21a()
    {
        // #1686 review F4: the premise that makes first-sighting dedupe correct -- that a repeated
        // message.id carries an IDENTICAL usage object across disjoint content-block chunks -- was
        // asserted, not recorded; every prior fixture used a synthetic msg_1 with identity true by
        // construction. This is a REAL consecutive pair, lines 5-6 of
        // `dispatch-implement-3dc5e21a`'s own `.stdout.log`
        // (`artifacts/execution_b3cdfeb7684f459a9af0baca24c6e1c3/.stdout.log`), trimmed to the `usage`
        // and `content[].type` fields per the review's own instruction. Measured: `usage` is IDENTICAL
        // between the two lines (input_tokens 2, cache_creation_input_tokens 39901,
        // cache_read_input_tokens 0, output_tokens 1 on both), and `content[].type` is DISJOINT ("text"
        // on the first, "tool_use" on the second) -- exactly the shape the dedupe assumes. This
        // confirms first-sighting dedupe is correct on the measured shape; it does not generalize past
        // this fixture (the review's own caveat).
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_011Cee7wqgwCecnuPg5NCH6y","content":[{"type":"text"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":39901,"cache_read_input_tokens":0,"output_tokens":1}}}""");
        monitor.OnStdoutLine(
            """{"type":"assistant","message":{"id":"msg_011Cee7wqgwCecnuPg5NCH6y","content":[{"type":"tool_use"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":39901,"cache_read_input_tokens":0,"output_tokens":1}}}""");

        var usage = monitor.SnapshotUsage();
        // Summed once, not twice: the second line's identical usage is deduped by shared message.id.
        // #1706: 39,901 rather than 2 + 1 + 39,901 -- the input_tokens 2 and output_tokens 1 visible in
        // both real lines above are the placeholders this issue measured, no longer read.
        Assert.Equal(39901, usage.BilledTokens);
        Assert.Null(usage.TokensOut);
        Assert.True(usage.BilledIsFloor);
    }

    [Fact]
    public void A_claude_usage_line_with_no_message_id_always_accumulates()
    {
        // A repeated-but-absent id must never be treated as "already seen" -- only an actual repeated
        // string dedupes.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":0,"output_tokens":3}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":0,"output_tokens":3}}}""");

        Assert.Equal(20, monitor.SnapshotUsage().BilledTokens);
    }

    [Fact]
    public void A_claude_usage_object_carrying_only_the_placeholder_columns_yields_no_reading_at_all()
    {
        // #1706, the deliberate consequence stated in ClaudeUsageParser.TryParseIncrementalUsage's own
        // doc: with input_tokens/output_tokens no longer read, a usage object carrying nothing else has
        // no figure left to report, and reporting a WorkerUsage of all nulls would put a fabricated 0
        // into the running Σ. That doc also records why this shape is unreachable on measured traffic;
        // this is pinned so the choice stays a decision rather than an accident.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, maxToolSteps: null, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":2,"output_tokens":3}}}""");

        var usage = monitor.SnapshotUsage();
        Assert.Null(usage.BilledTokens);
        Assert.Null(usage.ContextLevelTokens);
        Assert.False(usage.BilledIsFloor);
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
        // #1682: before this issue a monitor required a budget to be constructed at all. Terminal-state
        // lines per #1686 review F2's fixed unit.
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: 2, new AgyUsageParser());

        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"b"}}""");
        Assert.False(monitor.Arrested);
        monitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"c"}}""");
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
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"a"}}""");
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"b"}}""");

        Assert.True(monitor.Arrested);
        Assert.Equal(ArrestReason.ToolStepCap, monitor.ArrestReasonValue);

        // Further lines, including ones that would cross the budget, never flip the recorded reason.
        // #1686 review F6: needs "step_type":"agent_response" -- without it, F4's own gate
        // (StandardWorkerUsageParsers.cs's TryParseIncrementalUsage) rejects the line outright, so
        // this arm would keep passing even if the already-arrested guard below it were deleted.
        monitor.OnStdoutLine(
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"agent_response","usage":{"input_tokens":1000,"output_tokens":1000}}}""");
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
        // #1686 review F5: a stream with no usage line at all must not report a measured-zero context
        // level -- ContextLevelTokens now follows the same never-fabricated convention as BilledTokens
        // and CacheReadTokens below.
        Assert.Null(monitor.SnapshotUsage().ContextLevelTokens);
        Assert.Null(monitor.SnapshotUsage().BilledTokens);
        // #1686 review F7: a stream with no usage line at all must not report a measured-zero cache
        // read -- CacheReadTokens now follows the same never-fabricated convention as BilledTokens
        // above (TokenBudgetMonitor's own OnStdoutLine comment has the rule).
        Assert.Null(monitor.SnapshotUsage().CacheReadTokens);
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
        // #1686 review F5: no incremental usage line ever parsed on this stream, so ContextLevelTokens
        // stays null rather than a fabricated 0 -- same convention as the never-double-counted assertion
        // above.
        Assert.Null(monitor.SnapshotUsage().ContextLevelTokens);
    }
}
