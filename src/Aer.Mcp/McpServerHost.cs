using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aer.Mcp;

/// <summary>
/// AER's own MCP server host (#585) — the "server" side of a stdio-transport Model Context Protocol
/// server: reads newline-delimited JSON-RPC 2.0 requests from <see cref="RunAsync"/>'s
/// <c>input</c>, dispatches <c>initialize</c>/<c>tools/list</c>/<c>tools/call</c>, and writes
/// newline-delimited JSON-RPC responses to its <c>output</c>. Generic on purpose: this class knows
/// nothing about <c>yield</c> or dialogue — a caller supplies the <see cref="IMcpTool"/> set a given
/// server instance exposes. That split is what lets 0029's later blocking-<c>tools/call</c> mechanism
/// reuse this exact host with a different tool, and is why per-participant attribution (#585's
/// "who called yield is structural, never inferred from text") works: each participant's vendor CLI
/// invocation is wired (via its own <c>--mcp-config</c>/workspace) to spawn its own instance of this
/// host, so the instance that received a call — not anything parsed from a turn's own text — is what
/// identifies the caller.
/// <para>
/// One request in, one line out: this host is a per-invocation stdio server, matching how a vendor
/// CLI itself spawns an MCP server subprocess for the lifetime of one <c>-p</c> turn — it does not
/// hold connections open across turns, and exits when its input stream closes.
/// <c>Aer.Workers.Dialogue.DialogueYieldWiring</c> spawns this host per participant: claude
/// participants reach it via <c>--mcp-config</c>/<c>--strict-mcp-config</c>, agy participants via a
/// per-run workspace's <c>.agents/mcp_config.json</c> + <c>--add-dir</c> (#585, decision 0035). This
/// class itself has no dependency on that wiring — it is usable standalone the moment a caller
/// supplies an <c>input</c>/<c>output</c> pair and a tool list.
/// </para>
/// <para>
/// Request handling never lets one bad request take down the loop: a request whose <c>method</c> or
/// <c>params.name</c> is not the JSON type expected is read defensively (never throws), and a tool's
/// <see cref="IMcpTool.Call"/> throwing is caught and turned into an <c>isError: true</c> result for
/// that one request rather than propagating out of <see cref="RunAsync"/>.
/// </para>
/// </summary>
public sealed class McpServerHost(string serverName, string serverVersion, IReadOnlyList<IMcpTool> tools)
{
    private const string ProtocolVersion = "2024-11-05";

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is not JsonObject requestObject)
            {
                // A syntactically valid JSON line that isn't a JSON-RPC object (e.g. a bare
                // scalar or array) has no id to answer against — drop it, keep reading.
                continue;
            }

            var method = TryGetString(TryGetProperty(requestObject, "method"));
            var hasId = requestObject.TryGetPropertyValue("id", out var idNode);

            // notifications/* carry no id and expect no response, per JSON-RPC 2.0.
            if (!hasId)
            {
                continue;
            }

            JsonNode result = method switch
            {
                "initialize" => BuildInitializeResult(),
                "tools/list" => BuildToolsListResult(),
                "tools/call" => await BuildToolsCallResultAsync(requestObject, cancellationToken).ConfigureAwait(false),
                _ => BuildMethodNotFound(),
            };

            var isError = method is null || (method != "initialize" && method != "tools/list" && method != "tools/call");
            var envelope = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode?.DeepClone(),
            };
            envelope[isError ? "error" : "result"] = result;

            await output.WriteLineAsync(envelope.ToJsonString()).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonNode BuildInitializeResult() => new JsonObject
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = serverVersion },
    };

    private JsonNode BuildToolsListResult()
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var entry = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchemaJson),
            };
            if (tool.AnnotationsJson is { } annotations)
            {
                try
                {
                    entry["annotations"] = JsonNode.Parse(annotations);
                }
                catch (System.Text.Json.JsonException)
                {
                    // A tool's malformed annotations literal must degrade to
                    // "no annotations advertised" — never take down the host.
                }
            }

            array.Add(entry);
        }

        return new JsonObject { ["tools"] = array };
    }

    private async Task<JsonNode> BuildToolsCallResultAsync(JsonNode request, CancellationToken cancellationToken)
    {
        var paramsNode = TryGetProperty(request, "params");
        var name = TryGetString(TryGetProperty(paramsNode, "name"));
        var tool = tools.FirstOrDefault(t => t.Name == name);

        if (tool is null)
        {
            return ToolCallError($"Unknown tool '{name}'.");
        }

        var argumentsNode = TryGetProperty(paramsNode, "arguments");
        using var argumentsDocument = argumentsNode is null
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(argumentsNode.ToJsonString());

        McpToolCallResult callResult;
        try
        {
            callResult = await tool.CallAsync(argumentsDocument.RootElement, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Deliberately broad: an IMcpTool implementation (e.g. YieldTool's file write) can throw
            // anything, and one bad tools/call must become this request's isError:true result, not
            // take down the stdio loop for every other participant/turn sharing this process.
            return ToolCallError($"Tool '{name}' threw {ex.GetType().Name}: {ex.Message}");
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = callResult.Text }),
            ["isError"] = callResult.IsError,
        };
    }

    private static JsonNode ToolCallError(string message) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    private static JsonNode BuildMethodNotFound() => new JsonObject
    {
        ["code"] = -32601,
        ["message"] = "Method not found",
    };

    private static JsonNode? TryGetProperty(JsonNode? node, string propertyName) =>
        node is JsonObject obj && obj.TryGetPropertyValue(propertyName, out var value) ? value : null;

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
