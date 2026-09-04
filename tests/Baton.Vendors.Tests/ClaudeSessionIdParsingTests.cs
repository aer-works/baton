using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Coverage for <see cref="ClaudeWorkerAdapter.TryParseSessionId"/> (issue #1841): the read-side
/// capture of claude's own session id. The method's documentation owns the recorded evidence and
/// explains why the parser deliberately accepts only the init envelope exercised here.
/// </summary>
public sealed class ClaudeSessionIdParsingTests
{
    private readonly ClaudeWorkerAdapter _adapter = new();

    [Fact]
    public void TryParseSessionId_SystemInitLineWithSessionId_ReturnsIt()
    {
        const string line = """
            {"type":"system","subtype":"init","cwd":"C:\\repo","session_id":"0cf3ee6d-fad8-4281-88ce-0dc9b70d8f50","tools":[],"model":"claude-sonnet-5"}
            """;

        var parsed = _adapter.TryParseSessionId(line, out var sessionId);

        Assert.True(parsed);
        Assert.Equal("0cf3ee6d-fad8-4281-88ce-0dc9b70d8f50", sessionId);
    }

    [Fact]
    public void TryParseSessionId_SystemInitLineWithoutSessionId_ReturnsFalse()
    {
        const string line = """{"type":"system","subtype":"init","cwd":"C:\\repo"}""";

        var parsed = _adapter.TryParseSessionId(line, out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryParseSessionId_NonInitSystemLine_ReturnsFalse()
    {
        // A recognized system envelope whose subtype isn't init -- deliberately not parsed for a
        // session id, mirroring TryParseSystemEvent's own subtype switch.
        const string line = """{"type":"system","subtype":"hook_started","session_id":"s-1"}""";

        var parsed = _adapter.TryParseSessionId(line, out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryParseSessionId_TerminalResultLine_ReturnsFalse()
    {
        // No recorded fixture shows a "type":"result" line carrying session_id, so this arm is
        // deliberately unparsed (claim-scope: don't invent the shape). Polarity check: even though
        // this line carries a session_id field, it must not be read from this envelope.
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s-1"}
            """;

        var parsed = _adapter.TryParseSessionId(line, out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryParseSessionId_AssistantLine_ReturnsFalse()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""";

        var parsed = _adapter.TryParseSessionId(line, out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryParseSessionId_MalformedJson_ReturnsFalseRatherThanThrowing()
    {
        var parsed = _adapter.TryParseSessionId("{not json", out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryParseSessionId_BlankLine_ReturnsFalse()
    {
        var parsed = _adapter.TryParseSessionId("   ", out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }

    [Fact]
    public void TryParseSessionId_EmptySessionIdString_ReturnsFalse()
    {
        const string line = """{"type":"system","subtype":"init","session_id":""}""";

        var parsed = _adapter.TryParseSessionId(line, out var sessionId);

        Assert.False(parsed);
        Assert.Null(sessionId);
    }
}
