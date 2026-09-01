using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Coverage for <see cref="AgyWorkerAdapter.TryParseFinalUsage"/> (issue #1360). The full-breakdown
/// fixture is captured verbatim from <c>AgyStreamJsonProgressParsingTests</c>' own real agy 1.1.11
/// capture; the total-only fixture is the other observed shape (see
/// <c>AssistantAnswerExtractionTests</c>/<c>AgyWorkerAdapterTests</c>) where agy reports only a
/// combined <c>total_tokens</c> with no input/output split.
/// </summary>
public sealed class AgyFinalUsageParsingTests
{
    private readonly AgyWorkerAdapter _adapter = new();

    [Fact]
    public void TryParseFinalUsage_ResultLineWithFullBreakdown_ReturnsInputOutputAndTurns()
    {
        const string line = """
            {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(14407, usage!.TokensIn);
        Assert.Equal(1173, usage.TokensOut);
        Assert.Equal(1, usage.Turns);
        Assert.Equal(992, usage.ThinkingTokens);
        Assert.Equal(40765, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
    }

    [Fact]
    public void TryParseFinalUsage_ResultLineWithOnlyTotalTokens_LeavesTokensInAndOutAbsent()
    {
        // A lone total is a real number but not a direction -- must not be guessed into either field.
        const string line = """
            {"event":"result","result":{"status":"SUCCESS","response":"done","num_turns":2,"usage":{"total_tokens":5}}}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Null(usage!.TokensIn);
        Assert.Null(usage.TokensOut);
        Assert.Equal(2, usage.Turns);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.ThinkingTokens);
    }

    [Fact]
    public void TryParseFinalUsage_ResultLineWithoutCacheOrThinkingFields_LeavesBothAbsent()
    {
        // Polarity's other arm, captured shape (docs/vendor-capabilities.md): a run reporting only
        // input/output/turns leaves cacheReadTokens/thinkingTokens null, never a fabricated zero.
        const string line = """
            {"event":"result","result":{"status":"SUCCESS","response":"done","num_turns":1,"usage":{"input_tokens":9,"output_tokens":4}}}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(9, usage!.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.ThinkingTokens);
        Assert.Null(usage.CacheCreationTokens);
    }

    [Fact]
    public void TryParseFinalUsage_NonResultEvent_ReturnsFalse()
    {
        const string line = """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}""";

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }

    [Fact]
    public void TryParseFinalUsage_ClaudeShapedLine_ReturnsFalse()
    {
        // Cross-vendor safety: a claude "type":"result" line must not be misread as agy's.
        const string line = """{"type":"result","num_turns":1,"usage":{"input_tokens":1,"output_tokens":1}}""";

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }

    [Fact]
    public void TryParseFinalUsage_BlankLine_ReturnsFalse()
    {
        var parsed = _adapter.TryParseFinalUsage(" ", out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }
}

/// <summary>
/// Coverage for <see cref="AgyWorkerAdapter.TryParseFinalResponse"/> (issue #1594) — the same terminal
/// line <see cref="AgyFinalUsageParsingTests"/> covers, read for <c>result.response</c> instead of
/// <c>result.usage</c>. The success fixture is the same real captured line those tests use.
/// </summary>
public sealed class AgyFinalResponseParsingTests
{
    private readonly AgyWorkerAdapter _adapter = new();

    [Fact]
    public void TryParseFinalResponse_SuccessResultLine_ReturnsTheResponseText()
    {
        const string line = """
            {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}
            """;

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.True(parsed);
        Assert.Equal("Created note.txt containing HELLO-WORLD.", response);
    }

    [Fact]
    public void TryParseFinalResponse_NonSuccessStatus_ReturnsFalse()
    {
        // #1561: a non-SUCCESS result's response is documented empty; even if a vendor build ever put
        // text there, an error status is not a worker's answer and must never be written into a
        // declared output as though it were one.
        const string line = """
            {"event":"result","result":{"status":"ERROR","response":"","error":"quota exhausted"}}
            """;

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_EmptyResponseText_ReturnsFalse()
    {
        const string line = """{"event":"result","result":{"status":"SUCCESS","response":""}}""";

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_NonResultEvent_ReturnsFalse()
    {
        const string line = """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}""";

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_ClaudeShapedLine_ReturnsFalse()
    {
        const string line = """{"type":"result","is_error":false,"result":"claude's answer"}""";

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_BlankLine_ReturnsFalse()
    {
        var parsed = _adapter.TryParseFinalResponse(" ", out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }
}
