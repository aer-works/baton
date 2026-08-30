using System.Text.Json;
using Baton.Mcp;

namespace Baton.Mcp.Host;

/// <summary>
/// The <c>memory-edit-proposal</c> tool (#801, per #672's "Minimal-form design" comment item 3): a
/// worker calls this to propose an add/edit/delete against one fact file in the room's <c>memory/</c>
/// directory. Composed the same way <see cref="YieldTool"/> is (decision 0035) — this project's
/// composition root, not <see cref="Baton.Mcp.McpServerHost"/> itself, which has no idea this tool
/// exists.
/// <para>
/// <b>This tool never writes <c>memory/</c>.</b> Decision 0044 owns the rule (its point 3:
/// memory changes only by decision; this tool merely proposes). A call here only
/// captures the proposed edit to disk for a later escalation step (<c>MemoryProposalEscalation</c>,
/// <c>Baton</c>) to turn into room-journal held work an operator decides on.
/// </para>
/// <para>
/// Unlike <see cref="YieldTool"/>'s single capture file, a worker may propose more than one edit in a
/// turn, so each call writes its own uniquely named file into <paramref name="captureDirectoryPath"/>
/// rather than refusing a second call.
/// </para>
/// </summary>
public sealed class MemoryProposalTool(string captureDirectoryPath) : IMcpTool
{
    /// <summary>
    /// The subdirectory name this tool's captures land under, relative to the execution's own
    /// <c>BATON_OUTPUT_DIR</c> (#833). <c>Baton.Mcp.Host/Program.cs</c> is the only production caller
    /// that combines this with an output directory; mirrored as a literal in
    /// <see cref="Baton.Mutation.MemoryProposalEscalation"/>'s own constant of the same value
    /// (<c>Baton</c> cannot reference this project) -- the two must agree, which
    /// <c>MemoryProposalCaptureDirectoryNameTests</c> asserts on both sides.
    /// </summary>
    public const string CaptureDirectoryName = "memory-proposals";

    public static readonly string[] AllowedOperations = ["add", "edit", "delete"];

    public string Name => "memory-edit-proposal";

    public string Description =>
        "Propose an add, edit, or delete against one fact file in the room's memory/ directory. " +
        "This does not write memory -- it escalates the proposal for an operator to decide. Provide " +
        "'operation' ('add', 'edit', or 'delete'), 'targetPath' (the fact file's path, relative to " +
        "memory/), 'content' (the full proposed file content -- required for 'add'/'edit', omitted " +
        "for 'delete'), and 'rationale' (one line explaining why).";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "operation": { "type": "string", "enum": ["add", "edit", "delete"] },
            "targetPath": { "type": "string" },
            "content": { "type": "string" },
            "rationale": { "type": "string" }
          },
          "required": ["operation", "targetPath", "rationale"],
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments)
    {
        if (!TryGetRequiredString(arguments, "operation", out var operation, out var error))
        {
            return new McpToolCallResult(error!, IsError: true);
        }

        if (!AllowedOperations.Contains(operation))
        {
            return new McpToolCallResult(
                $"'operation' must be one of: {string.Join(", ", AllowedOperations)}.", IsError: true);
        }

        if (!TryGetRequiredString(arguments, "targetPath", out var targetPath, out error))
        {
            return new McpToolCallResult(error!, IsError: true);
        }

        // Structural validation only, per #801's scope -- this tool never touches memory/ itself, so
        // it cannot confirm targetPath resolves inside it. What it CAN and must reject is a path
        // shaped to escape a future consumer's own root join (0004's fail-closed posture).
        // Path.IsPathRooted answers for the RUNNING platform only -- 'C:/etc/passwd' is not rooted
        // on Unix -- so the guard names both platforms' rooted shapes itself; a proposal is data
        // that may be consumed on a different OS than the one that captured it.
        if (IsRootedOnAnyPlatform(targetPath) || targetPath.Split('/', '\\').Contains(".."))
        {
            return new McpToolCallResult(
                $"'targetPath' must be a relative path with no '..' segments; got '{targetPath}'.", IsError: true);
        }

        if (!TryGetRequiredString(arguments, "rationale", out var rationale, out error))
        {
            return new McpToolCallResult(error!, IsError: true);
        }

        string? content = null;
        if (arguments.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (contentElement.ValueKind != JsonValueKind.String)
            {
                return new McpToolCallResult(
                    "'content' must be a string when present.", IsError: true);
            }

            content = contentElement.GetString();
        }

        if (content is null && operation is "add" or "edit")
        {
            return new McpToolCallResult(
                $"'content' is required and must be a string when 'operation' is '{operation}'.", IsError: true);
        }

        var captured = new MemoryProposalCapture(operation, targetPath, content, rationale);

        Directory.CreateDirectory(captureDirectoryPath);
        var captureFilePath = Path.Combine(captureDirectoryPath, $"proposal-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(captured);

        // Written to a temp file then moved into place, matching YieldTool's own convention: a
        // reader (MemoryProposalEscalation) polling the directory never observes a partial write.
        var tempPath = $"{captureFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, captureFilePath, overwrite: true);

        return new McpToolCallResult($"Recorded a '{operation}' proposal for '{targetPath}'; escalated for operator decision.");
    }

    /// <summary>
    /// Rooted on Windows OR Unix, regardless of the running platform: a leading slash or
    /// backslash, or a drive-letter prefix. See the call site's remarks for why
    /// <see cref="Path.IsPathRooted(string?)"/> is not enough here.
    /// </summary>
    private static bool IsRootedOnAnyPlatform(string path) =>
        path.StartsWith('/') || path.StartsWith('\\')
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static bool TryGetRequiredString(JsonElement arguments, string propertyName, out string value, out string? error)
    {
        if (!arguments.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
        {
            value = string.Empty;
            error = $"'{propertyName}' is required and must be a non-empty string.";
            return false;
        }

        value = element.GetString()!;
        error = null;
        return true;
    }
}

/// <summary>The structured shape <see cref="MemoryProposalTool"/> writes per call, read back by <c>MemoryProposalEscalation</c>.</summary>
public sealed record MemoryProposalCapture(string Operation, string TargetPath, string? Content, string Rationale);
