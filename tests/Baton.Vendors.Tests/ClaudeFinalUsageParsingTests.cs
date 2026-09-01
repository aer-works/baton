using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Coverage for <see cref="ClaudeWorkerAdapter.TryParseFinalUsage"/> (issue #1360). The success
/// fixture mirrors the shape docs/vendor-capabilities.md records as observed live (input/output
/// tokens plus num_turns on a stream-json result event); the error-turn fixture is captured verbatim
/// from <c>ClaudeStreamJsonProgressParsingTests</c>' own real, unauthenticated-run line, which has
/// num_turns but no usage object at all.
/// </summary>
public sealed class ClaudeFinalUsageParsingTests
{
    private readonly ClaudeWorkerAdapter _adapter = new();

    [Fact]
    public void TryParseFinalUsage_ResultLineWithUsageAndTurns_ReturnsBoth()
    {
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"duration_api_ms":1000,"num_turns":3,"result":"done","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","total_cost_usd":0.0021,"usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5}}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(100, usage!.TokensIn);
        Assert.Equal(50, usage.TokensOut);
        Assert.Equal(3, usage.Turns);
        Assert.Equal(10, usage.CacheCreationTokens);
        Assert.Equal(5, usage.CacheReadTokens);
    }

    [Fact]
    public void TryParseFinalUsage_ResultLineWithNoUsageObject_ReturnsTurnsOnlyWithTokensAbsent()
    {
        // Captured verbatim (see ClaudeStreamJsonProgressParsingTests) -- a real unauthenticated
        // run's result line, num_turns present, no usage key at all.
        const string line = """
            {"type":"result","subtype":"success","is_error":true,"duration_ms":29,"num_turns":1,"result":"Not logged in","stop_reason":"stop_sequence","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Null(usage!.TokensIn);
        Assert.Null(usage.TokensOut);
        Assert.Equal(1, usage.Turns);
    }

    [Fact]
    public void TryParseFinalUsage_ResultLineWithCacheAndThinkingFields_ReturnsAllThree()
    {
        // #1569: the nested usage.output_tokens_details.thinking_tokens path plus the two flat cache
        // siblings, captured verbatim off a real `claude -p ... --output-format stream-json --verbose`
        // result line -- see docs/vendor-capabilities.md's "Baton's usage field, per adapter" section
        // for this envelope's full provenance.
        const string line = """
            {"duration_api_ms":2475,"stop_reason":"end_turn","session_id":"f2790c72-2c95-4b91-9786-ce6d5ba3aea8","total_cost_usd":0.039611,"usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":38741,"output_tokens":17,"output_tokens_details":{"thinking_tokens":6}},"is_error":false,"num_turns":1,"subtype":"success","result":"1\n\n2\n\n3\n\n4\n\n5","type":"result"}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(2, usage!.TokensIn);
        Assert.Equal(17, usage.TokensOut);
        Assert.Equal(38741, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(6, usage.ThinkingTokens);
    }

    [Fact]
    public void TryParseFinalUsage_ResultLineWithoutCacheOrThinkingFields_LeavesAllThreeAbsent()
    {
        // Polarity's other arm: a result line that omits the new fields entirely must yield null for
        // each, never a fabricated zero -- same doctrine WorkerUsage.cs already states for TokensIn.
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"num_turns":2,"result":"done","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","usage":{"input_tokens":9,"output_tokens":4}}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.True(parsed);
        Assert.Equal(9, usage!.TokensIn);
        Assert.Equal(4, usage.TokensOut);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.ThinkingTokens);
    }

    [Fact]
    public void TryParseFinalUsage_NonResultLine_ReturnsFalse()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}
            """;

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }

    [Fact]
    public void TryParseFinalUsage_PlainTextStdout_ReturnsFalse()
    {
        // Text-mode (non-stream-json) dispatch: stdout is the model's plain answer, never JSON.
        var parsed = _adapter.TryParseFinalUsage("Here is the answer you asked for.", out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }

    [Fact]
    public void TryParseFinalUsage_BlankLine_ReturnsFalse()
    {
        var parsed = _adapter.TryParseFinalUsage("   ", out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }

    [Fact]
    public void TryParseFinalUsage_AgyShapedLine_ReturnsFalse()
    {
        // Cross-vendor safety: an agy "event":"result" line must not be misread as claude's.
        const string line = """{"event":"result","result":{"status":"SUCCESS","num_turns":1,"usage":{"input_tokens":1,"output_tokens":1}}}""";

        var parsed = _adapter.TryParseFinalUsage(line, out var usage);

        Assert.False(parsed);
        Assert.Null(usage);
    }
}

/// <summary>
/// Mirrors <see cref="AgyFinalResponseParsingTests"/> for the claude adapter (issue #1594):
/// exercises <see cref="ClaudeWorkerAdapter.TryParseFinalResponse"/>, which reads the top-level
/// <c>result</c> string off the same line <see cref="ClaudeFinalUsageParsingTests"/> already parses
/// for <c>usage</c>. Its success fixture is the real captured line docs/vendor-capabilities.md
/// records (#1540).
/// </summary>
public sealed class ClaudeFinalResponseParsingTests
{
    private readonly ClaudeWorkerAdapter _adapter = new();

    [Fact]
    public void TryParseFinalResponse_SuccessResultLine_ReturnsTheResultText()
    {
        const string line = """
            {"duration_api_ms":2550,"is_error":false,"result":"1\n\n2\n\n3\n\n4\n\n5","type":"result","duration_ms":3870}
            """;

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.True(parsed);
        Assert.Equal("1\n\n2\n\n3\n\n4\n\n5", response);
    }

    [Fact]
    public void TryParseFinalResponse_ErrorTurn_ReturnsFalse()
    {
        // Captured verbatim (see ClaudeStreamJsonProgressParsingTests).
        const string line = """
            {"type":"result","subtype":"success","is_error":true,"duration_ms":29,"num_turns":1,"result":"Not logged in","stop_reason":"stop_sequence","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}
            """;

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_MissingIsError_ReturnsFalse()
    {
        // is_error is required, not defaulted: its absence means an unfamiliar shape, not a
        // confirmed success -- same posture as TryParseResultEvent's own doc comment.
        const string line = """{"type":"result","result":"looks like an answer"}""";

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_NonResultLine_ReturnsFalse()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""";

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_AgyShapedLine_ReturnsFalse()
    {
        const string line = """{"event":"result","result":{"status":"SUCCESS","response":"agy's answer"}}""";

        var parsed = _adapter.TryParseFinalResponse(line, out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_BlankLine_ReturnsFalse()
    {
        var parsed = _adapter.TryParseFinalResponse("   ", out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void TryParseFinalResponse_NonStringTypeField_ReturnsFalseRatherThanThrowing()
    {
        // The claude-side twin of AgyFinalResponseParsingTests' non-string-discriminator regression
        // test -- same shape of bug, the other adapter's field name.
        var parsed = _adapter.TryParseFinalResponse("""{"type":123,"is_error":false,"result":"x"}""", out var response);

        Assert.False(parsed);
        Assert.Null(response);
    }
}
