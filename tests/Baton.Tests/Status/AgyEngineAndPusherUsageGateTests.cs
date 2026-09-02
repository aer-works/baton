using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// #1686 review F4. The engine's predicate is on <see cref="AgyUsageParser.TryParseIncrementalUsage"/>'s
/// own doc, glass's on its own selftest's <c>real_agy_usage_line</c> — pinning agreement between the
/// two lives here so a future edit to either side that breaks it fails on both, not just the one that
/// changed. Reads the SAME real captured line glass's selftest reads.
/// </summary>
public sealed class AgyEngineAndPusherUsageGateTests
{
    // Verbatim the same line as pusher.py's `real_agy_usage_line` (from room `dispatch-implement-38c24d11`'s
    // real capture, step_index 1).
    private const string RealAgyUsageLine =
        """{"event":"step_update","step_update":{"state":"DONE","step_type":"agent_response","usage":{"input_tokens":14205,"output_tokens":443,"thinking_tokens":349,"cache_read_tokens":0,"total_tokens":14648}}}""";

    [Fact]
    public void The_engine_reads_billed_tokens_off_the_same_real_line_pusher_py_s_selftest_pins()
    {
        var parser = new AgyUsageParser();

        Assert.True(parser.TryParseIncrementalUsage(RealAgyUsageLine, out var usage));
        var billed = (usage!.TokensIn ?? 0) + (usage.TokensOut ?? 0);

        // pusher.py: `real_agy_counts.get("billedTokens") == 14205 + 443`
        Assert.Equal(14205 + 443, billed);
    }

    [Fact]
    public void Neither_side_reads_usage_off_a_DONE_tool_step_even_if_one_carried_it()
    {
        // pusher.py's own gate only reads usage on step_type == "agent_response" -- a DONE/"tool" step
        // contributes no token fields even if it hypothetically carried a usage object (unobserved in
        // the real evidence set, per spec/baton.md §3).
        const string doneToolWithUsage =
            """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool","tool_name":"x","usage":{"input_tokens":1,"output_tokens":1}}}""";

        var parser = new AgyUsageParser();
        Assert.False(parser.TryParseIncrementalUsage(doneToolWithUsage, out var usage));
        Assert.Null(usage);
    }
}
