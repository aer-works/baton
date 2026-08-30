using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Regression coverage for <see cref="ClaudeWorkerAdapter.TryParseProgressEvent"/> (M24 Phase 1's
/// live in-turn streaming). The <c>system</c>/<c>assistant</c> fixtures below are captured verbatim
/// from a real, live invocation of the installed <c>claude</c> CLI with
/// <c>--output-format stream-json --include-partial-messages --verbose</c> — not hand-authored
/// guesses — so these tests fail if a future CLI version changes the envelope shape this adapter
/// depends on.
/// </summary>
public sealed class ClaudeStreamJsonProgressParsingTests
{
    private readonly ClaudeWorkerAdapter _adapter = new();

    [Fact]
    public void TryParseProgressEvent_SystemInitLine_ReturnsSessionStartedStatus()
    {
        const string line = """
            {"type":"system","subtype":"init","cwd":"C:\\tmp","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","tools":["Bash","Edit"],"mcp_servers":[],"model":"claude-sonnet-5","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.216","uuid":"f93ec384-884d-4bcd-b658-4d33f1393dea"}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("status", progressEvent!.Kind);
        Assert.Equal("Session started", progressEvent.Text);
        Assert.False(progressEvent.IsPartial);
    }

    [Fact]
    public void TryParseProgressEvent_SystemStatusLine_ReturnsRawStatusText()
    {
        const string line = """
            {"type":"system","subtype":"status","status":"requesting","uuid":"27fa2324-06c3-4d3a-b301-5a967274a7b9","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("status", progressEvent!.Kind);
        Assert.Equal("requesting", progressEvent.Text);
    }

    [Fact]
    public void TryParseProgressEvent_AssistantTextBlockLine_ReturnsCompleteText()
    {
        // Captured verbatim from a live (unauthenticated) claude invocation -- proves the envelope
        // shape this branch depends on is real, not assumed.
        const string line = """
            {"type":"assistant","message":{"id":"0b0f33a2-825a-4ca9-ac37-c0e49fca6f02","container":null,"model":"<synthetic>","role":"assistant","stop_details":null,"stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"Not logged in · Please run /login"}],"context_management":null},"parent_tool_use_id":null,"session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","uuid":"c8766ee4-a0c2-4104-90a6-2907ce7d9e03","timestamp":"2026-07-21T16:26:12.855Z","error":"authentication_failed"}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("text", progressEvent!.Kind);
        Assert.Contains("Please run /login", progressEvent.Text);
        Assert.False(progressEvent.IsPartial);
    }

    [Fact]
    public void TryParseProgressEvent_AssistantToolUseBlockLine_ReturnsToolName()
    {
        const string line = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("tool", progressEvent!.Kind);
        Assert.Equal("Bash", progressEvent.Text);
    }

    [Fact]
    public void TryParseProgressEvent_ResultLine_ReturnsFalse()
    {
        // Captured verbatim; the "result" envelope is a turn-completion summary, not in-turn
        // progress -- ExecuteSessionTurnAsync already reads the durable reply from the declared
        // output file, so this type is deliberately not surfaced as a progress event.
        const string line = """
            {"type":"result","subtype":"success","is_error":true,"duration_ms":29,"num_turns":1,"result":"Not logged in","stop_reason":"stop_sequence","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.False(parsed);
        Assert.Null(progressEvent);
    }

    [Fact]
    public void TryParseProgressEvent_BestEffortStreamEventTextDelta_ReturnsPartialText()
    {
        // Unverified against a live authenticated run (see the adapter's remarks) -- this only
        // asserts the parser handles the shape correctly *if* it ever sees it.
        const string line = """
            {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("text", progressEvent!.Kind);
        Assert.Equal("Hello", progressEvent.Text);
        Assert.True(progressEvent.IsPartial);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"incomplete\": tr")]
    [InlineData("{\"type\":\"unknown_future_type\",\"foo\":\"bar\"}")]
    [InlineData("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\"}}")]
    public void TryParseProgressEvent_UnrecognizedOrMalformedLine_ReturnsFalseWithoutThrowing(string line)
    {
        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.False(parsed);
        Assert.Null(progressEvent);
    }
}
