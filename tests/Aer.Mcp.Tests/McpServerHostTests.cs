using System.Text;
using Aer.Mcp;
using Aer.Mcp.Host;

namespace Aer.Mcp.Tests;

public class McpServerHostTests
{
    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

        Assert.Single(responses);
        Assert.Equal("2024-11-05", (string)responses[0]["result"]!["protocolVersion"]!);
        Assert.Equal("aer-mcp-host-test", (string)responses[0]["result"]!["serverInfo"]!["name"]!);
    }

    [Fact]
    public async Task ToolsList_ReturnsRegisteredTool()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}");

        var tools = responses[0]["result"]!["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("yield", (string)tools[0]!["name"]!);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsIsError()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\",\"arguments\":{}}}");

        Assert.True((bool)responses[0]["result"]!["isError"]!);
    }

    [Fact]
    public async Task ToolsCall_KnownTool_InvokesToolAndReturnsContent()
    {
        var captureFile = Path.Combine(Path.GetTempPath(), $"aer-mcp-test-{Guid.NewGuid():N}.json");
        try
        {
            var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [new YieldTool(captureFile)]);
            var input = new StringReader(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"yield\",\"arguments\":{\"outcome\":\"concluded\"}}}\n");
            var output = new StringWriter();

            await host.RunAsync(input, output, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(captureFile));
            Assert.Contains("concluded", await File.ReadAllTextAsync(captureFile, TestContext.Current.CancellationToken));
            Assert.Contains("Recorded yield", output.ToString());
        }
        finally
        {
            if (File.Exists(captureFile))
            {
                FileCleanup.Delete(captureFile);
            }
        }
    }

    [Fact]
    public async Task Notification_NoId_ProducesNoResponse()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");

        Assert.Empty(responses);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFoundEnvelope()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"nope/nope\",\"params\":{}}");

        Assert.Single(responses);
        Assert.False(responses[0].ContainsKey("result"));
        Assert.Equal(-32601, (int)responses[0]["error"]!["code"]!);
        Assert.Equal("Method not found", (string)responses[0]["error"]!["message"]!);
    }

    [Fact]
    public async Task MalformedJsonLine_IsSkipped_LoopSurvivesAndAnswersTheNextLine()
    {
        // The first line is not valid JSON at all (an unterminated object) — must not throw out of
        // RunAsync, and the well-formed line after it still gets answered.
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");

        Assert.Single(responses);
        Assert.Equal(2, (int)responses[0]["id"]!);
        Assert.Single(responses[0]["result"]!["tools"]!.AsArray());
    }

    [Fact]
    public async Task ValidJsonNonObjectLine_IsSkipped_LoopSurvivesAndAnswersTheNextLine()
    {
        // A syntactically valid JSON line that parses to a bare array, not a JSON-RPC object.
        var responses = await RunAsync(
            "[1,2,3]\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");

        Assert.Single(responses);
        Assert.Equal(2, (int)responses[0]["id"]!);
    }

    [Fact]
    public async Task ExplicitNullArguments_BehavesIdenticallyToOmittedArguments()
    {
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"yield\",\"arguments\":null}}");

        // YieldTool requires 'outcome'; an empty-effective-arguments call must report that missing
        // field the same way whether 'arguments' was omitted or explicitly null — not throw, and not
        // silently succeed.
        Assert.True((bool)responses[0]["result"]!["isError"]!);
        Assert.Contains("outcome", (string)responses[0]["result"]!["content"]![0]!["text"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonStringMethod_DoesNotCrashLoop_ReturnsMethodNotFound()
    {
        // "method" is a number, not a string — a naive JsonNode.GetValue<string>() throws here.
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":42,\"params\":{}}");

        Assert.Single(responses);
        Assert.Equal(-32601, (int)responses[0]["error"]!["code"]!);
    }

    [Fact]
    public async Task NonStringParamsName_DoesNotCrashLoop_ReturnsIsErrorResult()
    {
        // "params.name" is a number, not a string — must not throw out of the request loop.
        var responses = await RunAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":42,\"arguments\":{}}}");

        Assert.Single(responses);
        Assert.True((bool)responses[0]["result"]!["isError"]!);
    }

    [Fact]
    public async Task ToolsCall_ToolThrows_ReturnsIsErrorResult_LoopSurvives()
    {
        var throwingTool = new ThrowingTool();
        var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [throwingTool]);
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"boom\",\"arguments\":{}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}\n");
        var output = new StringWriter();

        await host.RunAsync(input, output, TestContext.Current.CancellationToken);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var responses = lines.Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!.AsObject()).ToList();

        Assert.Equal(2, responses.Count);
        Assert.True((bool)responses[0]["result"]!["isError"]!);
        Assert.Contains("boom", (string)responses[0]["result"]!["content"]![0]!["text"]!, StringComparison.OrdinalIgnoreCase);
        // Second request still gets answered — the throw in request 1 did not kill the loop.
        Assert.Single(responses[1]["result"]!["tools"]!.AsArray());
    }

    private sealed class ThrowingTool : IMcpTool
    {
        public string Name => "boom";
        public string Description => "Always throws.";
        public string InputSchemaJson => "{\"type\":\"object\"}";

        public McpToolCallResult Call(System.Text.Json.JsonElement arguments) =>
            throw new InvalidOperationException("boom exploded");
    }

    [Fact]
    public async Task ToolsList_CarriesAnnotationsOnlyWhenDeclared()
    {
        var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [new AnnotatedTool(), new ThrowingTool()]);
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}\n");
        var output = new StringWriter();

        await host.RunAsync(input, output);

        var response = System.Text.Json.Nodes.JsonNode.Parse(output.ToString().Trim())!.AsObject();
        var tools = response["result"]!["tools"]!.AsArray();
        var annotated = tools.Single(t => (string)t!["name"]! == "annotated");
        var bare = tools.Single(t => (string)t!["name"]! == "boom");
        Assert.True((bool)annotated!["annotations"]!["readOnlyHint"]!);
        Assert.Null(bare!["annotations"]);
    }

    private sealed class AnnotatedTool : IMcpTool
    {
        public string Name => "annotated";
        public string Description => "Declares itself read-only.";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public string? AnnotationsJson => "{\"readOnlyHint\": true}";

        public McpToolCallResult Call(System.Text.Json.JsonElement arguments) =>
            new("{}");
    }

    [Fact]
    public async Task ToolsList_MalformedAnnotationsDegradeToNoneWithoutKillingTheHost()
    {
        var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [new BadAnnotationsTool(), new AnnotatedTool()]);
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}\n");
        var output = new StringWriter();

        await host.RunAsync(input, output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        var tools = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!["result"]!["tools"]!.AsArray();
        var bad = tools.Single(t => (string)t!["name"]! == "bad-annotations");
        Assert.Null(bad!["annotations"]);
        Assert.True((bool)tools.Single(t => (string)t!["name"]! == "annotated")!["annotations"]!["readOnlyHint"]!);
    }

    private sealed class BadAnnotationsTool : IMcpTool
    {
        public string Name => "bad-annotations";
        public string Description => "Carries a broken annotations literal.";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public string? AnnotationsJson => "{not valid json";

        public McpToolCallResult Call(System.Text.Json.JsonElement arguments) =>
            new("{}");
    }

    private static async Task<List<System.Text.Json.Nodes.JsonObject>> RunAsync(string requestLine)
    {
        // Path.GetTempFileName() would create the file up front, which YieldTool would then read as
        // "yield already called once" on the very first call — build an unused path instead.
        var captureFile = Path.Combine(Path.GetTempPath(), $"aer-mcp-test-{Guid.NewGuid():N}.json");
        try
        {
            var host = new McpServerHost("aer-mcp-host-test", "1.0.0", [new YieldTool(captureFile)]);
            var input = new StringReader(requestLine + "\n");
            var output = new StringWriter();

            await host.RunAsync(input, output);

            var text = output.ToString();
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!.AsObject()).ToList();
        }
        finally
        {
            if (File.Exists(captureFile))
            {
                FileCleanup.Delete(captureFile);
            }
        }
    }
}
