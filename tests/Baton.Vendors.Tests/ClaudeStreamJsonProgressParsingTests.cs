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
    public void TryParseProgressEvent_ErrorResultLine_ReturnsResultKindWithErrorSummary()
    {
        // Captured verbatim (a real, unauthenticated claude run). Since #1561, the "result"
        // envelope surfaces as a turn-completion status/error summary -- the one line that carries
        // WHY a lane failed -- even though ExecuteSessionTurnAsync separately reads the durable
        // reply from the declared output file rather than from this progress event.
        const string line = """
            {"type":"result","subtype":"success","is_error":true,"duration_ms":29,"num_turns":1,"result":"Not logged in","stop_reason":"stop_sequence","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("result", progressEvent!.Kind);
        Assert.Equal("error — Not logged in", progressEvent.Text);
    }

    [Fact]
    public void TryParseProgressEvent_SuccessfulResultLine_ReturnsResultKindWithSuccessText()
    {
        // Polarity check against the error-result test above: a successful turn renders too,
        // rather than silently vanishing the way every "result" line used to before #1561.
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"duration_ms":29,"num_turns":1,"result":"done","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}
            """;

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("result", progressEvent!.Kind);
        Assert.Equal("success", progressEvent.Text);
    }

    [Fact]
    public void TryParseProgressEvent_ResultLineMissingIsError_ReturnsFalse()
    {
        // #1561 second-reader review: every observed result envelope carries is_error (both
        // polarities). Without it, rendering "success" would be a guess, not an observation -- must
        // return false (unknown shape) so EchoStreamJsonLine's fallback echoes the raw line instead
        // of asserting a status it can't back up.
        const string line = """{"type":"result","subtype":"error_during_execution","session_id":"x"}""";

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.False(parsed);
        Assert.Null(progressEvent);
    }

    [Fact]
    public void TryParseProgressEvent_ErrorResultWithNonStringResultField_ReturnsResultKindWithFallbackText()
    {
        // #1561 second-reader review: `result` isn't always a string (the field is vendor-controlled
        // JSON) -- an object there must not throw InvalidOperationException off GetString(), which
        // would escape past EchoStreamJsonLine on the dispatch thread. Falls back to a generic label.
        const string line = """{"type":"result","is_error":true,"result":{"code":42}}""";

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("result", progressEvent!.Kind);
        Assert.Equal("error — no error detail in the result envelope", progressEvent.Text);
    }

    [Fact]
    public void TryParseProgressEvent_SystemHookLifecycleSubtype_ReturnsIgnoreKind()
    {
        // #1561 second-reader review: a `system` envelope this adapter recognizes (the top-level
        // `type`) but whose subtype carries no user-facing signal is a deliberate filter, not an
        // unknown shape -- Kind "ignore", not false, so EchoStreamJsonLine stays quiet on it rather
        // than dumping raw JSON the way the never-swallow fallback would for a genuinely unknown type.
        const string line = """{"type":"system","subtype":"hook_started","session_id":"x"}""";

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("ignore", progressEvent!.Kind);
    }

    [Fact]
    public void TryParseProgressEvent_AssistantThinkingOnlyBlock_ReturnsIgnoreKind()
    {
        // #1561 second-reader review: captured from the same #1540 lane run
        // docs/vendor-capabilities.md's evidence quote traces to -- an extended-thinking turn emits a
        // `thinking`-only assistant message once per turn on the primary path. Pre-fix this fell
        // through TryParseAssistantEvent's loop to `return false`, so the never-swallow fallback this
        // issue added would have dumped the raw envelope (base64 signature included) every turn.
        // Kind "ignore" keeps it quiet, matching pre-#1561 behavior.
        const string line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc123"}]}}""";

        var parsed = _adapter.TryParseProgressEvent(line, out var progressEvent);

        Assert.True(parsed);
        Assert.Equal("ignore", progressEvent!.Kind);
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
