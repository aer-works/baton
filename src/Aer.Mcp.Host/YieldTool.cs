using System.Text.Json;
using Aer.Mcp;

namespace Aer.Mcp.Host;

/// <summary>
/// The <c>yield</c> tool (#585, decision 0035): a participant calls this when it believes a
/// multi-turn exchange should end. The dialogue worker's old text-sentinel match
/// (<c>DialogueRunner.TryStripStopSentinel</c>) was deleted in favor of this before that worker was
/// itself archived (#1408); its <c>DialogueYieldWiring</c> was the wiring that spawned one instance
/// of this tool's host per participant. Composed into a runnable server here, in the
/// composition-root project — <see cref="Aer.Mcp.McpServerHost"/> itself has no idea this tool
/// exists, per that class's own remarks.
/// <para>
/// Captures exactly one call: writes the received arguments as JSON to <see cref="captureFilePath"/>
/// and returns a synchronous acknowledgement. There is nothing held open — 0035 is explicit that
/// this is the easy case in 0029's mechanism table, not the permission gate's held-open one.
/// </para>
/// </summary>
public sealed class YieldTool(string captureFilePath) : IMcpTool
{
    public static readonly string[] AllowedOutcomes = ["concluded", "stalemate"];

    public string Name => "yield";

    public string Description =>
        "Call this when you believe the dialogue exchange should end. Provide 'outcome' as either " +
        "'concluded' (the exchange reached its purpose) or 'stalemate' (further turns would not help), " +
        "and an optional 'note' explaining why.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "outcome": { "type": "string", "enum": ["concluded", "stalemate"] },
            "note": { "type": "string" }
          },
          "required": ["outcome"],
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments)
    {
        if (File.Exists(captureFilePath))
        {
            // "Captures exactly one call" is enforced here, not just documented: a second tools/call
            // for 'yield' in the same turn must not silently replace the first participant's recorded
            // outcome with a later one the caller never asked for.
            return new McpToolCallResult("yield was already called once for this turn.", IsError: true);
        }

        if (!arguments.TryGetProperty("outcome", out var outcomeElement) || outcomeElement.ValueKind != JsonValueKind.String)
        {
            return new McpToolCallResult("'outcome' is required and must be a string.", IsError: true);
        }

        var outcome = outcomeElement.GetString()!;
        if (!AllowedOutcomes.Contains(outcome))
        {
            return new McpToolCallResult(
                $"'outcome' must be one of: {string.Join(", ", AllowedOutcomes)}.", IsError: true);
        }

        string? note = arguments.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
            ? noteElement.GetString()
            : null;

        var captured = new YieldCapture(outcome, note);
        var json = JsonSerializer.Serialize(captured);

        // Written to a temp file then moved into place so a reader (polling for this file after the
        // turn's process exits) never observes a partially written file.
        var tempPath = $"{captureFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, captureFilePath, overwrite: true);

        return new McpToolCallResult($"Recorded yield with outcome '{outcome}'.");
    }
}

/// <summary>The structured shape <see cref="YieldTool"/> writes to its capture file, read back by its caller.</summary>
public sealed record YieldCapture(string Outcome, string? Note);
