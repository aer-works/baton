using System.IO;
using Baton.Vendors;
using Baton.Domain;
using Xunit;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="RunCommand"/>'s worker stdout echo rendering under <c>--echo-worker</c> (#882, #1540).
/// Verifies that stream-json bindings extract and render clean human-relevant content (assistant text and tool markers),
/// suppress internal JSON stream envelopes (system lifecycle, rate limits, turn results), and echo malformed or non-JSON
/// lines verbatim without swallowing them.
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
        const string line = """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"chunk"}}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(line, _claudeAdapter, writer);

        Assert.Equal("chunk", writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_IgnoresSystemInitAndStatusLines()
    {
        const string initLine = """{"type":"system","subtype":"init","session_id":"s-123","tools":["Bash"]}""";
        const string statusLine = """{"type":"system","subtype":"status","status":"requesting"}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(initLine, _claudeAdapter, writer);
        RunCommand.EchoStreamJsonLine(statusLine, _claudeAdapter, writer);

        Assert.Empty(writer.ToString());
    }

    [Fact]
    public void EchoStreamJsonLine_IgnoresResultAndRateLimitLines()
    {
        const string resultLine = """{"type":"result","subtype":"success","is_error":false,"result":"all done"}""";
        const string rateLimitLine = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""";
        using var writer = new StringWriter();

        RunCommand.EchoStreamJsonLine(resultLine, _claudeAdapter, writer);
        RunCommand.EchoStreamJsonLine(rateLimitLine, _claudeAdapter, writer);

        Assert.Empty(writer.ToString());
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
        // Streaming worker: system line filtered
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
            "Warning: something happened",
            """{"type":"assistant","raw":"json"}""",
            "Raw shell output",
            "Unknown worker line",
            "",
        ]);

        Assert.Equal(expected, writer.ToString());
    }
}
