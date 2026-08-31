using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Vendors;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Mcp;

/// <summary>
/// The <c>fleet_status</c> read-only MCP tool (Spike 1, #1392): scans rooms across the fleet,
/// leveraging the terminal sentinel fast-path for terminal rooms and projecting active rooms from
/// bound snapshots and Flow event logs. Returns a structured JSON array of per-room status.
/// </summary>
/// <remarks>
/// spec/baton.md §8: the directory scan (<see cref="BatonPaths.Rooms"/> plus caller-supplied
/// <c>roots</c>) is unioned with <see cref="RoomRegistryStore"/>'s registrations, so a room
/// dispatched into a project directory nobody passed as a <c>roots</c> entry is still found. The
/// union only ever adds rooms — a stale or unreadable registry falls back to exactly what the scan
/// alone would have returned, never fewer.
/// </remarks>
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
        if (Directory.Exists(BatonPaths.Rooms))
        {
            searchRoots.Add(BatonPaths.Rooms);
        }

        foreach (var extraRoot in extraRoots)
        {
            if (Directory.Exists(extraRoot))
            {
                searchRoots.Add(extraRoot);
            }
        }

        // spec/baton.md §8: the registry's project-root map, keyed the same way seenRooms/roomDir
        // comparisons already are, so a room found by BOTH the directory scan below AND a registry
        // entry (the common case — a room dispatched under the default BatonPaths.Rooms location still
        // gets registered) is decorated with its project, not just rooms the registry alone finds. A
        // registry entry whose directory no longer exists is dropped here rather than surfacing as a
        // phantom room or a spurious project label.
        IReadOnlyList<RoomRegistryEntry> registryEntries;
        try
        {
            registryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(BatonPaths.RoomRegistryFile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defense-in-depth for the registry's only-ever-adds-coverage contract: the store's own
            // catch list should make this unreachable, but if any exception shape slips it, losing
            // the whole call (directory-scan results included) to the host's generic catch-all would
            // be strictly worse than answering scan-only.
            registryEntries = [];
        }
        var projectByRoom = new Dictionary<string, string>(BatonPaths.RecordKeyComparer);
        foreach (var entry in registryEntries)
        {
            if (Directory.Exists(entry.RoomPath))
            {
                projectByRoom[entry.RoomPath] = entry.ProjectRoot;
            }
        }

        var seenRooms = new HashSet<string>(BatonPaths.RecordKeyComparer);
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
                var recordKey = BatonPaths.RecordKey(roomDir);
                if (!seenRooms.Add(recordKey))
                {
                    continue;
                }

                var roomStatus = await ProcessRoomAsync(roomDir, includeTerminal, cancellationToken).ConfigureAwait(false);
                if (roomStatus is not null)
                {
                    results.Add(DecorateWithProject(roomStatus, recordKey, projectByRoom));
                }
            }
        }

        // The registry's whole point (spec/baton.md §8): a room dispatched into a project directory never passed as
        // a scan root above is still invisible to the loop that just ran — pick up whatever the
        // registry names that the scan did not already cover.
        foreach (var (roomPath, projectRoot) in projectByRoom)
        {
            if (!seenRooms.Add(roomPath))
            {
                continue;
            }

            var roomStatus = await ProcessRoomAsync(roomPath, includeTerminal, cancellationToken).ConfigureAwait(false);
            if (roomStatus is not null)
            {
                results.Add(roomStatus with { Project = projectRoot });
            }
        }

        var json = JsonSerializer.Serialize(results, SerializerOptions);
        return new McpToolCallResult(json);
    }

    private static FleetRoomStatusView DecorateWithProject(
        FleetRoomStatusView roomStatus, string recordKey, IReadOnlyDictionary<string, string> projectByRoom) =>
        projectByRoom.TryGetValue(recordKey, out var projectRoot) ? roomStatus with { Project = projectRoot } : roomStatus;

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
                s.LinkedFromUsage,
                Liveness: s.Liveness
            )).ToList();

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: sentinel.State,
                Steps: sentinelSteps,
                Outputs: sentinel.Outputs,
                Error: sentinel.Error,
                Try: sentinel.Try,
                Rejected: sentinel.Rejected);
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
            // Without a parser registry the usage projector returns null for every
            // execution, so the tool would advertise usage it never populates.
            var view = WorkflowStatusProjector.Project(
                state, snapshot, roomDir, entries, StandardWorkerUsageParsers.Default);
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
                    stepView.LinkedFromUsage,
                    stepView.Liveness));
            }

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: outcome,
                Steps: steps,
                Outputs: view.Outputs,
                Error: view.Error,
                Try: view.Try,
                Rejected: view.Rejected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-room isolation: one unreadable room becomes its own error entry.
            // Cancellation is NOT a room defect — it propagates so the scan stops
            // instead of running to completion accumulating spurious errors.
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
    [property: JsonPropertyName("project")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Project = null,
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
    string? Try = null,
    // spec/baton.md §3/§6: the same WorkflowStatusView.Rejected FleetStatusTool already reads off
    // the shared projection (sentinel.Rejected / view.Rejected) -- copied, never re-derived. Omitted
    // (not emitted false) so its mere presence already answers "did a human reject a step here",
    // the same presence-signals-meaning convention Liveness below uses.
    [property: JsonPropertyName("rejected")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Rejected = false);

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
    ExecutionUsageView? LinkedFromUsage = null,
    // spec/baton.md §3/§6: the same WorkflowStatusStepView.Liveness FleetStatusTool already reads
    // off the shared projection (sentinel step's Liveness / stepView.Liveness) -- copied, never a
    // second EngineLivenessProbe call. Present only for a step this projection calls "Running",
    // the identical gate WorkflowStatusProjector.Project already applies before probing.
    [property: JsonPropertyName("liveness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Liveness = null);
