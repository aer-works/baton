using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

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
