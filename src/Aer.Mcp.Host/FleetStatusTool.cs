using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Status;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Mcp;

namespace Aer.Mcp.Host;

/// <summary>
/// The <c>fleet_status</c> read-only MCP tool (Spike 1, #1392): scans rooms across the fleet,
/// leveraging the terminal sentinel fast-path for terminal rooms and projecting active rooms from
/// bound snapshots and Flow event logs. Returns a structured JSON array of per-room status.
/// </summary>
public sealed class FleetStatusTool : IMcpTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public string Name => "fleet_status";

    public string Description =>
        "Read-only snapshot of room statuses across the fleet, including state, timestamps, usage, and outputs.";

    public string? AnnotationsJson => """{"readOnlyHint": true}""";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "roots": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional extra directories containing rooms to scan."
            },
            "include_terminal": {
              "type": "boolean",
              "description": "Whether to include terminal rooms in the output. Defaults to true."
            }
          },
          "additionalProperties": false
        }
        """;

    public McpToolCallResult Call(JsonElement arguments) =>
        CallAsync(arguments, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<McpToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var includeTerminal = true;
        var extraRoots = new List<string>();

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("include_terminal", out var includeTerminalElem)
                && (includeTerminalElem.ValueKind == JsonValueKind.True || includeTerminalElem.ValueKind == JsonValueKind.False))
            {
                includeTerminal = includeTerminalElem.GetBoolean();
            }

            if (arguments.TryGetProperty("roots", out var rootsElem) && rootsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var rootItem in rootsElem.EnumerateArray())
                {
                    if (rootItem.ValueKind == JsonValueKind.String && rootItem.GetString() is { } rootPath && !string.IsNullOrWhiteSpace(rootPath))
                    {
                        extraRoots.Add(rootPath);
                    }
                }
            }
        }

        var searchRoots = new List<string>();
        if (Directory.Exists(AerPaths.Rooms))
        {
            searchRoots.Add(AerPaths.Rooms);
        }

        foreach (var extraRoot in extraRoots)
        {
            if (Directory.Exists(extraRoot))
            {
                searchRoots.Add(extraRoot);
            }
        }

        var seenRooms = new HashSet<string>(AerPaths.RecordKeyComparer);
        var results = new List<FleetRoomStatusView>();

        foreach (var searchRoot in searchRoots)
        {
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(searchRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

            foreach (var roomDir in subDirs)
            {
                var recordKey = AerPaths.RecordKey(roomDir);
                if (!seenRooms.Add(recordKey))
                {
                    continue;
                }

                var roomStatus = await ProcessRoomAsync(roomDir, includeTerminal, cancellationToken).ConfigureAwait(false);
                if (roomStatus is not null)
                {
                    results.Add(roomStatus);
                }
            }
        }

        var json = JsonSerializer.Serialize(results, SerializerOptions);
        return new McpToolCallResult(json);
    }

    private static async Task<FleetRoomStatusView?> ProcessRoomAsync(
        string roomDir, bool includeTerminal, CancellationToken cancellationToken)
    {
        var roomName = Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDir));

        // 1. Fast-path: check terminal sentinel
        var sentinel = await TerminalSentinelWriter.TryReadAsync(roomDir, cancellationToken).ConfigureAwait(false);
        if (sentinel is not null)
        {
            if (!includeTerminal)
            {
                return null;
            }

            var sentinelSteps = sentinel.Steps.Select(s => new FleetStepStatusView(
                s.Id,
                s.State,
                s.Execution,
                s.LinkedFrom,
                Timestamp: null,
                s.Usage,
                s.LinkedFromUsage
            )).ToList();

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: sentinel.State,
                Steps: sentinelSteps,
                Outputs: sentinel.Outputs,
                Error: sentinel.Error,
                Try: sentinel.Try);
        }

        // 2. Active room: load snapshot + flow events and project
        var snapshotPath = Path.Combine(roomDir, "snapshot.json");
        if (!File.Exists(snapshotPath))
        {
            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                Error: $"Room directory '{roomDir}' has no bound snapshot.");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var logPath = Path.Combine(roomDir, "flow.jsonl");
            var reader = new FlowEventLogReader(logPath);
            var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);

            var events = new List<FlowEvent>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is LogEntry.FlowLogEntry flowLogEntry)
                {
                    events.Add(flowLogEntry.Event);
                }
            }

            var checkpoint = ProjectionCheckpointStore.Load(roomDir);
            var state = StateProjector.Project(events, snapshot, checkpoint);

            if (!includeTerminal && state.Status == WorkflowStatus.Terminal)
            {
                return null;
            }

            var outcome = WorkflowOutcome.Describe(state);
            var view = WorkflowStatusProjector.Project(state, snapshot, roomDir, entries);
            var eventTimestamps = WorkflowStatusProjector.ExtractEventTimestamps(entries);

            var steps = new List<FleetStepStatusView>(view.Steps.Count);
            foreach (var stepView in view.Steps)
            {
                string? timestamp = stepView.Execution is not null && eventTimestamps.TryGetValue(stepView.Execution, out var dt)
                    ? dt.ToString("O")
                    : null;

                steps.Add(new FleetStepStatusView(
                    stepView.Id,
                    stepView.State,
                    stepView.Execution,
                    stepView.LinkedFrom,
                    timestamp,
                    stepView.Usage,
                    stepView.LinkedFromUsage));
            }

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: outcome,
                Steps: steps,
                Outputs: view.Outputs,
                Error: view.Error,
                Try: view.Try);
        }
        catch (Exception ex)
        {
            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                Error: ex.Message);
        }
    }
}

/// <summary>
/// Status of a single room within a fleet status report.
/// </summary>
public sealed record FleetRoomStatusView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? State = null,
    [property: JsonPropertyName("steps")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<FleetStepStatusView>? Steps = null,
    [property: JsonPropertyName("outputs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Outputs = null,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error = null,
    [property: JsonPropertyName("try")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Try = null);

/// <summary>
/// Status of a single workflow step within a fleet room status report.
/// </summary>
public sealed record FleetStepStatusView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("execution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Execution = null,
    [property: JsonPropertyName("linkedFrom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LinkedFrom = null,
    [property: JsonPropertyName("timestamp")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Timestamp = null,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? Usage = null,
    [property: JsonPropertyName("linkedFromUsage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? LinkedFromUsage = null);
