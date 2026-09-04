using System.Text.Json;
using Baton.Artifacts;
using Baton.Cli;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>promote-artifact</c> tool (#595): a worker calls this to copy a file out of its own
/// execution scratch directory into the room's <c>artifacts/</c>, through the exact same
/// <see cref="RoomArtifacts.Write"/> primitive <c>baton deliver</c> uses (#496/#1791) — versioned,
/// attributed, index-locked. Never a second writer: this tool builds the bytes and the attribution,
/// then hands both to <see cref="RoomArtifacts.Write"/> unchanged.
/// <para>
/// Composed into <see cref="McpServerHost"/> by <c>McpCommand</c> — see that file's own remarks for
/// which flag turns this tool on and why.
/// </para>
/// </summary>
public sealed class PromoteArtifactTool(string roomDirectoryPath, string scratchOutputDirectory, ArtifactAttribution attribution) : IMcpTool
{
    /// <summary>
    /// A promoted file's byte cap (#595): large enough for a report, a log excerpt, or a small
    /// dataset a worker wants kept, small enough that one MCP call cannot silently balloon a room's
    /// disk footprint past what <c>RoomRetentionSweep</c> budgets around. 25 MiB, the same order of
    /// magnitude as <c>ExecutionStreamLogger.DefaultMaxSizeBytes</c>'s own rollover cap for a single
    /// stream log.
    /// </summary>
    public const long MaxSourceBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Windows reserved device-name stems (case-insensitive, with or without an extension) -- writing
    /// under one of these throws an unhandled <see cref="IOException"/> deep inside
    /// <see cref="RoomArtifacts.Write"/>'s temp-file path rather than the structured refusal every
    /// other bad <c>artifactName</c> produces (#1824 review finding 3). Checked here rather than
    /// inside <see cref="RoomArtifacts"/>'s shared name normalization because that path already throws
    /// a raw <see cref="ArgumentException"/> for its own separator/'..' checks and this tool does not
    /// catch it -- keeping the check here keeps every promote-artifact refusal a structured
    /// <see cref="McpToolCallResult"/> instead of adding an untested exception-to-error mapping.
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNameStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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

        // #1824 review finding 2: this tool's spec paragraph promises promotion is scoped to the
        // caller's own execution scratch tree; nothing previously enforced that. OutboxPath.IsInside
        // resolves every link component-by-component before comparing, which is also what settles the
        // symlink low from the same review -- a source path laundered through a reparse point that
        // targets outside the scratch tree resolves outside it and is refused here, never followed.
        if (!OutboxPath.IsInside(sourcePath, scratchOutputDirectory))
        {
            return new McpToolCallResult(
                $"'sourcePath' must resolve inside this execution's own scratch directory " +
                $"('{scratchOutputDirectory}'); got '{sourcePath}'.", IsError: true);
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

        if (ReservedDeviceNameStems.Contains(Path.GetFileNameWithoutExtension(artifactName)))
        {
            return new McpToolCallResult(
                $"'artifactName' must not be a Windows reserved device name (CON, PRN, AUX, NUL, " +
                $"COM1-9, LPT1-9); got '{artifactName}'.", IsError: true);
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
