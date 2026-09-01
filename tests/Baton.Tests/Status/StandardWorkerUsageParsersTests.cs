using Baton.Status;
using Xunit;

namespace Baton.Tests.Status;

/// <summary>
/// Coverage for <see cref="ClaudeUsageParser"/>/<see cref="AgyUsageParser"/> (issue #1360, extended by
/// #1569) — the parser pair passed explicitly via <see cref="StandardWorkerUsageParsers.Default"/> by
/// <c>FleetStatusTool</c> for active rooms. (A separate defect where <c>Program.cs</c>'s call to
/// <c>WorkflowStatusProjector.Project</c> passes no adapter registry and leaves <c>terminal.json</c>
/// without vendor token counts is tracked in #1590). A second, hand-duplicated pair lives on
/// <c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c> themselves (<c>Baton.Vendors.Tests</c>' own
/// <c>ClaudeFinalUsageParsingTests</c>/<c>AgyFinalUsageParsingTests</c> cover those) and feeds
/// <c>baton status --json</c> via <c>WorkerAdapterRegistry.Default</c> instead — <c>spec/baton.md</c>
/// §3 rules the two outputs one contract, so this file pins the same fixtures against this pair too
/// (see #1578, filed while working #1569, for the duplication itself and one already-measured
/// divergence between the pairs that this file does not attempt to resolve).
/// </summary>
public sealed class StandardWorkerUsageParsersTests
{
    [Fact]
    public void Claude_parser_reads_cache_and_thinking_fields_when_present()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"duration_api_ms":2475,"stop_reason":"end_turn","session_id":"f2790c72-2c95-4b91-9786-ce6d5ba3aea8","total_cost_usd":0.039611,"usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":38741,"output_tokens":17,"output_tokens_details":{"thinking_tokens":6}},"is_error":false,"num_turns":1,"subtype":"success","result":"1\n\n2\n\n3\n\n4\n\n5","type":"result"}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(2, usage!.TokensIn);
        Assert.Equal(17, usage.TokensOut);
        Assert.Equal(38741, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(6, usage.ThinkingTokens);
    }

    [Fact]
    public void Claude_parser_leaves_cache_and_thinking_fields_null_when_absent()
    {
        var parser = new ClaudeUsageParser();
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"done","usage":{"input_tokens":9,"output_tokens":4}}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(9, usage!.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.ThinkingTokens);
    }

    [Fact]
    public void Agy_parser_reads_cache_and_thinking_fields_when_present()
    {
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(14407, usage!.TokensIn);
        Assert.Equal(1173, usage.TokensOut);
        Assert.Equal(992, usage.ThinkingTokens);
        Assert.Equal(40765, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
    }

    [Fact]
    public void Agy_parser_leaves_cache_and_thinking_fields_null_when_absent()
    {
        var parser = new AgyUsageParser();
        const string line = """
            {"event":"result","result":{"status":"SUCCESS","response":"done","num_turns":1,"usage":{"input_tokens":9,"output_tokens":4}}}
            """;

        var parsed = parser.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(9, usage!.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.ThinkingTokens);
        Assert.Null(usage.CacheCreationTokens);
    }
}
