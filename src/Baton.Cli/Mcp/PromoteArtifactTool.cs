using System.Text.Json;
using Baton.Artifacts;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>promote-artifact</c> tool (#595): a worker calls this to copy a file out of its own
/// execution scratch directory into the room's <c>artifacts/</c>, through the exact same
/// <see cref="RoomArtifacts.Write"/> primitive <c>baton deliver</c> uses (#496/#1791) — versioned,
/// attributed, index-locked. Never a second writer: this tool builds the bytes and the attribution,
/// then hands both to <see cref="RoomArtifacts.Write"/> unchanged.
/// <para>
/// Composed into <see cref="McpServerHost"/> by <c>McpCommand</c>, gated by the same
/// <c>--memory-proposal-tool</c> opt-in <see cref="MemoryProposalTool"/> already uses — see
/// <c>McpCommand.cs</c> for why this reuses that switch instead of adding a second one.
/// </para>
/// </summary>
public sealed class PromoteArtifactTool(string roomDirectoryPath, ArtifactAttribution attribution) : IMcpTool
{
    /// <summary>
    /// A promoted file's byte cap (#595): large enough for a report, a log excerpt, or a small
    /// dataset a worker wants kept, small enough that one MCP call cannot silently balloon a room's
    /// disk footprint past what <c>RoomRetentionSweep</c> budgets around. 25 MiB, the same order of
    /// magnitude as <c>ExecutionStreamLogger.DefaultMaxSizeBytes</c>'s own rollover cap for a single
    /// stream log.
    /// </summary>
    public const long MaxSourceBytes = 25 * 1024 * 1024;

    public string Name => "promote-artifact";

    public string Description =>
        "Copy a file from your execution's own scratch directory into the room's artifacts/ as a " +
        "versioned, attributed artifact. Provide 'sourcePath' (an absolute path to an existing regular " +
        "file), 'artifactName' (the name to record it under -- no path separators, no '..'), and an " +
        "optional 'title' included in the confirmation text.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "sourcePath": { "type": "string" },
            "artifactName": { "type": "string" },
            "title": { "type": "string" }
          },
          "required": ["sourcePath", "artifactName"],
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments)
    {
        if (!TryGetRequiredString(arguments, "sourcePath", out var sourcePath, out var error))
        {
            return new McpToolCallResult(error!, IsError: true);
        }

        if (!Path.IsPathRooted(sourcePath))
        {
            return new McpToolCallResult($"'sourcePath' must be an absolute path; got '{sourcePath}'.", IsError: true);
        }

        if (!File.Exists(sourcePath))
        {
            return new McpToolCallResult($"'sourcePath' does not exist or is not a regular file: '{sourcePath}'.", IsError: true);
        }

        if (!TryGetRequiredString(arguments, "artifactName", out var artifactName, out error))
        {
            return new McpToolCallResult(error!, IsError: true);
        }

        if (artifactName.IndexOfAny(['/', '\\']) >= 0 || artifactName.Split('/', '\\').Contains(".."))
        {
            return new McpToolCallResult(
                $"'artifactName' must contain no path separators and no '..'; got '{artifactName}'.", IsError: true);
        }

        string? title = null;
        if (arguments.TryGetProperty("title", out var titleElement)
            && titleElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (titleElement.ValueKind != JsonValueKind.String)
            {
                return new McpToolCallResult("'title' must be a string when present.", IsError: true);
            }

            title = titleElement.GetString();
        }

        var sourceLength = new FileInfo(sourcePath).Length;
        if (sourceLength > MaxSourceBytes)
        {
            return new McpToolCallResult(
                $"'sourcePath' is {sourceLength} bytes, over the {MaxSourceBytes}-byte promote-artifact cap.", IsError: true);
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new McpToolCallResult($"Could not read 'sourcePath': {ex.Message}", IsError: true);
        }

        var result = RoomArtifacts.Write(roomDirectoryPath, artifactName, content, attribution);

        var titleSuffix = title is null ? "" : $" titled '{title}'";
        return new McpToolCallResult(
            $"Promoted '{sourcePath}' as artifact '{artifactName}'{titleSuffix}: {result.Outcome} at version " +
            $"{result.Version} ({result.CurrentPath}).");
    }

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
