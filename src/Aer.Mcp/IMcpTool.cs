using System.Text.Json;

namespace Aer.Mcp;

/// <summary>
/// One tool a <see cref="McpServerHost"/> exposes over the Model Context Protocol (#585). Deliberately
/// the only extension point this library defines — the host (this project) never names a concrete
/// tool; composing a specific tool (e.g. <c>yield</c>, in <c>Aer.Mcp.Host</c>) into a runnable server
/// is a composition root's job, kept out of here so the host stays reusable for whatever MCP tool AER
/// builds next (0029's own eventual blocking <c>tools/call</c> mechanism is the next known consumer).
/// </summary>
public interface IMcpTool
{
    /// <summary>The tool name a client's <c>tools/call</c> request names to invoke this tool.</summary>
    string Name { get; }

    /// <summary>Human-readable description returned from <c>tools/list</c>.</summary>
    string Description { get; }

    /// <summary>The JSON Schema (as raw JSON text) describing this tool's <c>arguments</c> shape.</summary>
    string InputSchemaJson { get; }

    /// <summary>
    /// MCP tool annotations (as raw JSON text) advertised from <c>tools/list</c>, or null to omit
    /// the field. A read-only tool should declare <c>{"readOnlyHint": true}</c> — MCP clients treat
    /// unannotated tools as possibly-writing and may interpose a per-call confirmation, which a
    /// polled display consumer cannot afford (#1392).
    /// </summary>
    string? AnnotationsJson => null;

    /// <summary>
    /// Executes the tool for one <c>tools/call</c> request. <paramref name="arguments"/> is the
    /// request's <c>arguments</c> object, unparsed — each tool owns its own argument shape.
    /// </summary>
    McpToolCallResult Call(JsonElement arguments);

    /// <summary>
    /// Executes the tool asynchronously for one <c>tools/call</c> request. Default implementation delegates to synchronous <see cref="Call"/>.
    /// </summary>
    Task<McpToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(Call(arguments));
}

/// <summary>The outcome of one <see cref="IMcpTool.Call"/> — becomes a <c>tools/call</c> response's <c>content</c>.</summary>
/// <param name="Text">Plain-text content returned to the calling model.</param>
/// <param name="IsError">Whether this call failed — becomes the response's <c>isError</c> flag.</param>
public sealed record McpToolCallResult(string Text, bool IsError = false);
