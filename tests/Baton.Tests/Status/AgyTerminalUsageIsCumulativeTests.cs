using Baton.Mutation;
using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// #1706 review M4. The vendor fact under spec/baton.md §3's agy zero-under-read is measured in
/// <c>docs/vendor-capabilities.md</c> — the register states it, this pins it — against the real
/// captured line set it was measured on: <c>Fixtures/agy-38c24d11-agent-response-usage.jsonl</c>, room
/// <c>dispatch-implement-38c24d11</c>'s own 70 <c>agent_response</c> usage lines plus its terminal
/// <c>result</c>, copied verbatim from <c>~/.baton/rooms</c> with only the free-text <c>response</c>
/// field dropped. It replaced a synthetic fixture that asserted its own arithmetic.
/// <para>
/// The discriminating alternative ruled out here is "terminal = the LAST turn's usage": that reading
/// would put the terminal input at 5,164 rather than 595,684, so a fixture whose Σ merely happened to
/// match could not tell the two apart, and this one is separated by two orders of magnitude.
/// </para>
/// </summary>
public sealed class AgyTerminalUsageIsCumulativeTests
{
    internal const string FixtureFileName = "agy-38c24d11-agent-response-usage.jsonl";

    /// <summary>The room's own measured totals, from `docs/vendor-capabilities.md`'s table.</summary>
    private const long MeasuredTerminalBilled = 595_684 + 199_256;

    internal static string[] LoadRealAgyStream() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureFileName));

    [Fact]
    public void The_real_capture_carries_the_multi_turn_stream_this_claim_needs()
    {
        // The fixture's own control, checked ahead of anything concluded from it: a single-turn capture
        // cannot distinguish cumulative from last-turn at all, and every agy `result` line previously
        // checked into this tree is one. Also pins the `num_turns` trap named in the register: this
        // room's terminal reports num_turns 1 while carrying 70 agent responses, so `num_turns` must
        // never be used to select a multi-turn capture.
        var lines = LoadRealAgyStream();
        var parser = new AgyUsageParser();

        var usageLines = lines.Count(line => parser.TryParseIncrementalUsage(line, out var u) && u is not null);

        Assert.Equal(70, usageLines);
        Assert.Contains("\"num_turns\":1", lines[^1]);
    }

    [Fact]
    public void MEASURED_agy_s_terminal_result_usage_equals_the_running_sum_of_its_per_turn_lines()
    {
        var lines = LoadRealAgyStream();
        var parser = new AgyUsageParser();
        var monitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, billedRateLimit: null, parser);

        foreach (var line in lines)
        {
            monitor.OnStdoutLine(line);
        }

        Assert.True(parser.TryParseFinalUsage(lines[^1], out var terminal));
        var terminalBilled = (terminal!.TokensIn ?? 0) + (terminal.TokensOut ?? 0) + (terminal.CacheCreationTokens ?? 0);
        var liveBilled = monitor.SnapshotUsage().BilledTokens;

        Assert.Equal(MeasuredTerminalBilled, terminalBilled);
        Assert.Equal(MeasuredTerminalBilled, liveBilled);
        // The claim the register makes and spec/baton.md §3 leans on: on agy the under-read is a
        // measured zero.
        Assert.Equal(0, terminalBilled - liveBilled!.Value);
        // And it is NOT a floor -- the polarity arm against claude, whose every incremental reading is.
        Assert.False(monitor.SnapshotUsage().BilledIsFloor);
    }

    [Fact]
    public void DISCRIMINATING_the_terminal_is_not_the_LAST_turn_s_usage()
    {
        // The arm that makes the test above mean something. Were agy's terminal `usage` the final
        // turn's rather than the run's, spec/baton.md §3's zero would be false and
        // `billedUnderReadTokens` would go large and NEGATIVE on every real multi-turn agy room. This
        // room reads 5,164 input on its last turn against 595,684 cumulative.
        var lines = LoadRealAgyStream();
        var parser = new AgyUsageParser();

        var perTurn = lines
            .Where(line => parser.TryParseIncrementalUsage(line, out var u) && u is not null)
            .Select(line => { parser.TryParseIncrementalUsage(line, out var u); return u!; })
            .ToList();

        Assert.True(parser.TryParseFinalUsage(lines[^1], out var terminal));
        Assert.Equal(5_164, perTurn[^1].TokensIn);
        Assert.Equal(595_684, terminal!.TokensIn);
        Assert.NotEqual(perTurn[^1].TokensIn, terminal.TokensIn);
    }
}
