using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="TokenBudgetMonitor"/> (#1623 ruling addendum) — pure accumulation logic
/// against real per-vendor parsers, no process involved. <c>MutationInterfaceTokenBudgetTests</c>
/// covers the wiring into a live dispatch.
/// </summary>
public sealed class TokenBudgetMonitorTests
{
    [Fact]
    public void ArrestRequested_fires_once_the_running_total_crosses_the_budget()
    {
        var monitor = new TokenBudgetMonitor(budget: 1000, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":400,"output_tokens":100}}}""");
        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);

        // Turn 2: input level 500, output 600 -> total = 500 + (100 + 600) = 1200 >= 1000
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":500,"output_tokens":600}}}""");

        Assert.True(monitor.Arrested);
        Assert.True(monitor.ArrestRequested.IsCancellationRequested);
    }

    [Fact]
    public void A_budget_never_reached_leaves_the_monitor_unarrested()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":400,"output_tokens":100}}}""");

        Assert.False(monitor.Arrested);
        Assert.False(monitor.ArrestRequested.IsCancellationRequested);
    }

    [Fact]
    public void SnapshotUsage_replaces_input_level_and_sums_output_tokens()
    {
        // #1623 / F1: input side is a LEVEL (the caller replaces, never sums, its running value),
        // matching tools/fleet-glass/pusher.py rule; output_tokens is summed.
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":100,"output_tokens":10}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":200,"output_tokens":20}}}""");

        var usage = monitor.SnapshotUsage();

        Assert.Equal(200, usage.TokensIn);
        Assert.Equal(200, usage.ContextLevelTokens);
        Assert.Equal(30, usage.TokensOut);
    }

    [Fact]
    public void Pusher_fixture_turn_scores_context_level_plus_output_tokens_not_six()
    {
        // #1623 / F1: tools/fleet-glass/pusher.py selftest fixture (input 2 / cache_creation 12066 / cache_read 15092 / output 4)
        // scores ~27k (27160 level + 4 output), not 6 tokens.
        var monitor = new TokenBudgetMonitor(budget: 30_000, new ClaudeUsageParser());

        const string pusherLine = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":12066,"cache_read_input_tokens":15092,"output_tokens":4,"service_tier":"standard"}}}
            """;

        monitor.OnStdoutLine(pusherLine);

        var usage = monitor.SnapshotUsage();
        // #1623 re-review N6: see SnapshotUsage's own doc for why TokensIn stays vendor-raw while the
        // accumulated level moves to ContextLevelTokens.
        Assert.Equal(2, usage.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Equal(15092, usage.CacheReadTokens);
        Assert.Equal(12066, usage.CacheCreationTokens);
        Assert.Equal(27160, usage.ContextLevelTokens);
        Assert.False(monitor.Arrested);

        // A second turn with same cache state and 4000 output tokens crosses 30k budget
        const string secondLine = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":27158,"output_tokens":4000,"service_tier":"standard"}}}
            """;
        monitor.OnStdoutLine(secondLine);
        Assert.True(monitor.Arrested);
    }

    [Fact]
    public void Same_turn_sequence_on_claude_and_agy_crosses_at_comparable_real_consumption()
    {
        // #1623 / F1: both vendors evaluate against the same quantity: context level + summed output.
        // A 3-turn sequence on ~25k context with 500 output tokens/turn reaches ~26.5k total on both.
        var claudeMonitor = new TokenBudgetMonitor(budget: 26_000, new ClaudeUsageParser());
        var agyMonitor = new TokenBudgetMonitor(budget: 26_000, new AgyUsageParser());

        // Turn 1: ~25k prompt, 500 output
        claudeMonitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":25000,"output_tokens":500,"cache_creation_input_tokens":0,"cache_read_input_tokens":0}}}""");
        agyMonitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","usage":{"input_tokens":25000,"output_tokens":500,"cache_read_tokens":0}}}""");

        Assert.False(claudeMonitor.Arrested);
        Assert.False(agyMonitor.Arrested);

        // Turn 2: context grows slightly to 25100, 400 output -> total 25100 + 900 = 26000 >= 26000 -> both arrest!
        claudeMonitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":100,"output_tokens":400,"cache_creation_input_tokens":0,"cache_read_input_tokens":25000}}}""");
        agyMonitor.OnStdoutLine("""{"event":"step_update","step_update":{"state":"DONE","usage":{"input_tokens":25100,"output_tokens":400,"cache_read_tokens":0}}}""");

        Assert.True(claudeMonitor.Arrested);
        Assert.True(agyMonitor.Arrested);
        Assert.Equal(25100, claudeMonitor.SnapshotUsage().ContextLevelTokens);
        Assert.Equal(900, claudeMonitor.SnapshotUsage().TokensOut);
        Assert.Equal(25100, agyMonitor.SnapshotUsage().ContextLevelTokens);
        Assert.Equal(900, agyMonitor.SnapshotUsage().TokensOut);
    }

    [Fact]
    public void Lines_that_do_not_parse_as_usage_are_ignored()
    {
        var monitor = new TokenBudgetMonitor(budget: 10, new ClaudeUsageParser());

        monitor.OnStdoutLine("not json");
        monitor.OnStdoutLine("""{"type":"system","subtype":"init"}""");

        Assert.False(monitor.Arrested);
        // #1623 re-review N6: TokensIn is the vendor-raw latest reading, null (not 0) when no usage
        // line has ever parsed -- ContextLevelTokens is the accumulator that defaults to 0.
        Assert.Null(monitor.SnapshotUsage().TokensIn);
        Assert.Equal(0, monitor.SnapshotUsage().ContextLevelTokens);
    }

    [Fact]
    public void SnapshotLastToolNames_keeps_only_the_most_recent_names_bounded()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, new AgyUsageParser());

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
        var monitor = new TokenBudgetMonitor(budget: 10, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"result","usage":{"input_tokens":100,"output_tokens":100}}""");

        Assert.False(monitor.Arrested);
        Assert.Null(monitor.SnapshotUsage().TokensIn);
        Assert.Equal(0, monitor.SnapshotUsage().ContextLevelTokens);
    }
}
