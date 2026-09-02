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

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":400,"output_tokens":200}}}""");

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
    public void SnapshotUsage_sums_across_every_observed_line_not_just_the_latest()
    {
        var monitor = new TokenBudgetMonitor(budget: 1_000_000, new ClaudeUsageParser());

        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":100,"output_tokens":10}}}""");
        monitor.OnStdoutLine("""{"type":"assistant","message":{"usage":{"input_tokens":200,"output_tokens":20}}}""");

        var usage = monitor.SnapshotUsage();

        Assert.Equal(300, usage.TokensIn);
        Assert.Equal(30, usage.TokensOut);
    }

    [Fact]
    public void Lines_that_do_not_parse_as_usage_are_ignored()
    {
        var monitor = new TokenBudgetMonitor(budget: 10, new ClaudeUsageParser());

        monitor.OnStdoutLine("not json");
        monitor.OnStdoutLine("""{"type":"system","subtype":"init"}""");

        Assert.False(monitor.Arrested);
        Assert.Equal(0, monitor.SnapshotUsage().TokensIn);
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
        Assert.Equal(0, monitor.SnapshotUsage().TokensIn);
    }
}
