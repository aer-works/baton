using System.IO;
using Baton.Vendors;
using Baton.Domain;
using Xunit;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="RunCommand.EchoStreamJsonLine"/> (#882, #1540, #1561). Issue #1561's
/// pre-fix switch silently dropped agy's incremental status events, any unrecognized envelope type,
/// and claude's failure-carrying <c>result</c> line; this file pins all three plus the polarity
/// checks around them.
/// </summary>
public class RunCommandEchoTests
{
    private readonly ClaudeWorkerAdapter _claudeAdapter = new();
    private readonly AgyWorkerAdapter _agyAdapter = new();

    [Fact]
    public void EchoStreamJsonLine_RendersAssistantTextMessage()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Drafting the plan now."}]}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _claudeAdapter, writer);

        Assert.Equal("Drafting the plan now." + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersAssistantToolUseMarker()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _claudeAdapter, writer);

        Assert.Equal("[tool: Bash]" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersPartialStreamEventDeltaWithoutNewline()
    {
        // #1561 finding 8: unreachable from a real dispatch today -- #1540 dropped
        // --include-partial-messages from every invocation, so no live claude CLI produces this
        // shape (see ClaudeWorkerAdapter.TryParseStreamEvent's own doc comment). Retained as
        // parser coverage: if the flag is ever reintroduced, this path is already proven correct.
        const string line = """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"chunk"}}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _claudeAdapter, writer);

        Assert.Equal("chunk", writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersSystemInitAndStatusLinesAsStatus()
    {
        // #1561 finding 1 (claude's own system:init/status share the same "status" Kind agy's
        // incremental events use): both used to vanish; both now render a dim one-liner.
        const string initLine = """{"type":"system","subtype":"init","session_id":"s-123","tools":["Bash"]}""";
        const string statusLine = """{"type":"system","subtype":"status","status":"requesting"}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(initLine, _claudeAdapter, writer);
        RunCommand.EchoStreamJsonLine(statusLine, _claudeAdapter, writer);

        Assert.Equal(
            "[status: Session started]" + Environment.NewLine + "[status: requesting]" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersAgyStepUpdateDoneAsStatus()
    {
        // #1561 finding 1: agy's incremental step_update events map to Kind "status", but the
        // pre-fix switch had no "status" arm, so --echo-worker on an agy lane printed nothing at
        // all until the whole run finished. Only the terminal `result` line (a separate test,
        // EchoStreamJsonLine_RendersAgyStreamJsonResultText) rendered.
        const string line = """{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _agyAdapter, writer);

        Assert.Equal("[status: tool]" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersSuccessfulResultLine()
    {
        // #1561 finding 3, positive polarity: a successful `result` line renders a clean status
        // line rather than either vanishing (the pre-fix behavior) or dumping raw JSON.
        const string resultLine = """{"type":"result","subtype":"success","is_error":false,"result":"all done"}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(resultLine, _claudeAdapter, writer);

        Assert.Equal("[result: success]" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersFailedResultLineWithErrorSummary()
    {
        // #1561 finding 3, negative polarity: this is the one line that carries WHY a lane
        // failed (`result`, the exact text OutcomeClassifier's quota fixtures match on). The
        // pre-fix switch had no arm for it -- TryParseProgressEvent returned false for every
        // `result` line -- so a quota-exhausted claude lane under --echo-worker showed the
        // assistant's partial text, then silence, then exit.
        const string resultLine = """{"type":"result","subtype":"error","is_error":true,"errorCode":"credits_required","result":"Subscription quota exhausted."}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(resultLine, _claudeAdapter, writer);

        Assert.Equal(
            "[result: error — Subscription quota exhausted.]" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersAgyFailedResultLineWithErrorSummary()
    {
        // #1561 second-reader review: agy's failure reason was still discarded after the initial
        // fix -- the "result" case matched only `result.response`, which is empty on a failed turn,
        // so a quota-exhausted agy lane's error text ("Individual quota reached...") never reached
        // --echo-worker either. Fixture verbatim from #1128's real captured refusal (execution
        // eca57a30).
        const string line = """{"event":"result","result":{"conversation_id":"eca57a30","status":"ERROR","response":"","error":"Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s."}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _agyAdapter, writer);

        Assert.Equal(
            "[result: error — Individual quota reached. Please upgrade your subscription to increase your limits. Resets in 1h39m10s.]" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_StaysQuietOnClaudeThinkingOnlyBlock()
    {
        // #1561 second-reader review: the new never-swallow fallback must not turn every
        // deliberately-filtered envelope into a raw-JSON dump. A thinking-only assistant message
        // (Kind "ignore") stays quiet, same as before this issue -- unlike a genuinely unrecognized
        // type, which echoes verbatim (see EchoStreamJsonLine_EchoesUnrecognizedEnvelopeTypeVerbatim
        // below for the contrasting case).
        const string line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc123"}]}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _claudeAdapter, writer);

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_StaysQuietOnAgyStepUpdateActiveEdge()
    {
        // Same "ignore" Kind, agy side: the ACTIVE edge of a step_update is deliberately filtered
        // (measured: most steps report only DONE), not unknown -- must stay quiet, not raw-dump.
        const string line = """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool"}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _agyAdapter, writer);

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_EchoesUnrecognizedEnvelopeTypeVerbatim()
    {
        // #1561 finding 2: a valid-JSON `type` no adapter recognizes at all (a real example: claude's
        // own `user` role, shown below) used to return false from the parser and vanish with no
        // trace, contradicting this method's own "never swallow" doc comment. It must now echo
        // verbatim, same as a malformed line.
        const string line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"abc","content":"ls output"}]}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _claudeAdapter, writer);

        Assert.Equal(line + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_NullAdapterEchoesVerbatim()
    {
        // #1561 finding 11: a lookup miss on the binding's adapter (unreachable in production --
        // WorkerBindingResolver.Resolve throws on the same lookup before any dispatch -- but worth
        // pinning in the direction that fails safe). Falls out of the same "no adapter parsed this"
        // fallback finding 2 introduced, rather than needing its own branch.
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, adapter: null, writer);

        Assert.Equal(line + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_EchoesRateLimitEventVerbatim()
    {
        // Polarity check alongside EchoStreamJsonLine_RendersSystemInitAndStatusLinesAsStatus: a
        // `type` the adapter genuinely does not parse still echoes verbatim -- it does not fall
        // silent just because a *different* type now renders specially.
        const string rateLimitLine = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(rateLimitLine, _claudeAdapter, writer);

        Assert.Equal(rateLimitLine + Environment.NewLine, writer.ToString());
    }

    [Theory]
    [InlineData("Warning: no stdin data received in 3s")]
    [InlineData("Fatal: segmentation fault (core dumped)")]
    [InlineData("{\"incomplete_json: ")]
    [InlineData("Plain unformatted console line")]
    public void EchoStreamJsonLine_EchoesMalformedOrNonJsonLinesVerbatim(string rawLine)
    {
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(rawLine, _claudeAdapter, writer);

        Assert.Equal(rawLine + Environment.NewLine, writer.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EchoStreamJsonLine_NoOpsOnEmptyOrWhitespaceLines(string whitespace)
    {
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(whitespace, _claudeAdapter, writer);

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_RendersAgyStreamJsonResultText()
    {
        const string line = """{"event":"result","result":{"response":"Agy completed response."}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _agyAdapter, writer);

        Assert.Equal("Agy completed response." + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void CreateEchoWorkerCallback_WithStreamJsonBinding_ProcessesLinesThroughRenderer()
    {
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["streamingWorker"] = new(
                "claude",
                new WorkerContract("worker", [], [], []),
                "prompt",
                TimeSpan.FromMinutes(5),
                StreamJson: true),
            ["plainWorker"] = new(
                "shell",
                new WorkerContract("worker", [], [], []),
                "prompt",
                TimeSpan.FromMinutes(5),
                StreamJson: false),
        };
        var adapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = _claudeAdapter,
        };

        using var writer = new StringWriter();
        var callback = RunCommand.CreateEchoWorkerCallback(bindings, adapters, writer);

        // Streaming worker: clean text from assistant message
        callback("streamingWorker", """{"type":"assistant","message":{"content":[{"type":"text","text":"Streamed text"}]}}""");
        // Streaming worker: system line rendered as a status heartbeat
        callback("streamingWorker", """{"type":"system","subtype":"init"}""");
        // Streaming worker: malformed line echoed verbatim
        callback("streamingWorker", "Warning: something happened");

        // Non-streaming worker: every line echoed verbatim
        callback("plainWorker", """{"type":"assistant","raw":"json"}""");
        callback("plainWorker", "Raw shell output");

        // Unknown worker: echoed verbatim
        callback("unknownWorker", "Unknown worker line");

        var expected = string.Join(Environment.NewLine, [
            "Streamed text",
            "[status: Session started]",
            "Warning: something happened",
            """{"type":"assistant","raw":"json"}""",
            "Raw shell output",
            "Unknown worker line",
            "",
        ]);

        Assert.Equal(expected, writer.ToString());
    }
}
