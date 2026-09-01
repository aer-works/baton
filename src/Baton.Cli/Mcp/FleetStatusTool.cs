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
    // #1513: NOT a WorkflowOutcome member -- deliberately a fleet_status-only display word, so it
    // can never be confused for a ledger outcome by a consumer that already switches on
    // WorkflowOutcome's five (spec/baton.md §3). Distinct from "Failed": a stalled room is not
    // permanently done -- `baton resume` can revive it (a fresh `baton run` can too, but only
    // clears this reading once it actually dispatches; #1577 is the gap while it is still waiting
    // out the same backoff) -- this says "nothing is currently making progress", not "this cannot
    // succeed".
    private const string StalledDisplayState = "Stalled";

    // #1513: confirms EVERY step whose liveness this projection probes reads "dead" -- not merely
    // "none alive". Liveness is only ever populated (WorkflowStatusProjector.Project) for the exact
    // steps keeping the workflow un-terminal (a Running step, or a Failed step still carrying a
    // RetryNotBefore at all, expired or not -- see spec/baton.md §3), so this is already scoped to
    // the steps whose promise this room's Running reading rests on. Requiring "all dead" rather than
    // "none alive" matters for a multi-step DAG: a sibling step that has not started yet, or one
    // whose own liveness probe comes back "unknown" (a pre-#1375 ledger with no recorded identity, or
    // a Win32Exception probing a PID this process cannot inspect), must not let an unrelated sibling's
    // confirmed-dead engine downgrade the whole room -- "none alive" alone would. Fail-closed the
    // OTHER way here: uncertain (any "unknown", or no gated steps at all) stays "Running" rather than
    // risk a false "Stalled" an operator would wrongly abandon.
    private static bool IsConfirmedStalled(IReadOnlyList<FleetStepStatusView> steps)
    {
        var gated = steps.Where(s => s.Liveness is not null).ToList();
        return gated.Count > 0 && gated.All(s => s.Liveness == "dead");
    }

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

            // #1522 review finding 4: `terminal.json` is a frozen WorkflowStatusView snapshot, never
            // re-derived once written (TerminalSentinelWriter). A room that went terminal before
            // #1522 carries its old ConsecutiveFailureCount-derived Attempt/MaxAttempts forever,
            // by design -- this fast-path copies s.Attempt/s.MaxAttempts verbatim rather than
            // re-projecting, so it has no way to upgrade a stale sentinel's semantics after the fact.
            var sentinelSteps = sentinel.Steps.Select(s => new FleetStepStatusView(
                s.Id,
                s.State,
                s.Execution,
                s.LinkedFrom,
                Timestamp: null,
                s.Usage,
                s.LinkedFromUsage,
                Liveness: s.Liveness,
                Attempt: s.Attempt,
                MaxAttempts: s.MaxAttempts,
                FailureKind: s.FailureKind,
                RetryEligible: s.RetryEligible
            )).ToList();

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: sentinel.State,
                Steps: sentinelSteps,
                Outputs: sentinel.Outputs,
                Error: sentinel.Error,
                Try: sentinel.Try,
                Rejected: sentinel.Rejected,
                // The terminal fast path sits outside the per-room isolation try/catch below;
                // this read sits outside it safely because WorkerBindingConfigParser funnels every
                // data-driven failure into WorkerBindingConfigException, which TryReadRoomLabelAsync
                // catches and swallows (fail-open), matching the sentinel read above it.
                Label: await TryReadRoomLabelAsync(roomDir, cancellationToken).ConfigureAwait(false));
        }

        // 2. Active room: load snapshot + flow events and project
        var snapshotPath = Path.Combine(roomDir, BatonPaths.SnapshotFileName);
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
            var logPath = Path.Combine(roomDir, BatonPaths.FlowLogFileName);
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
                    stepView.Liveness,
                    stepView.Attempt,
                    stepView.MaxAttempts,
                    stepView.FailureKind,
                    stepView.RetryEligible));
            }

            var bindings = await TryLoadBindingsAsync(roomDir, cancellationToken).ConfigureAwait(false);
            var binding = TryResolveRunningBinding(bindings, steps, events);

            // #1513: the ledger's own `Running` (WorkflowOutcome.Describe/DeriveWorkflowStatus) means
            // "not terminal, and something could still make progress" -- true whether that something
            // is an in-flight process or a Failed step's still-unexpired RetryNotBefore. Neither
            // promise is backed by anything once the ONE process that would act on it is confirmed
            // dead -- spec/baton.md §7 has why there is nothing else to fall back on. This downgrade
            // is display-only, scoped to the fleet-facing view an operator actually reads (the
            // reported symptom -- "the room reads RUNNING forever on the fleet view"): it never
            // touches `outcome`/`state.Status` itself, so RunExitCodeResolver, TerminalSentinelWriter,
            // and every other WorkflowOutcome consumer keep reading exactly what they always did.
            var displayState = outcome == WorkflowOutcome.Running && IsConfirmedStalled(steps)
                ? StalledDisplayState
                : outcome;

            return new FleetRoomStatusView(
                Name: roomName,
                Path: roomDir,
                State: displayState,
                Steps: steps,
                Outputs: view.Outputs,
                Error: view.Error,
                Try: view.Try,
                Rejected: view.Rejected,
                Role: binding?.Role,
                Adapter: binding?.Entry.Adapter,
                Model: binding?.Entry.Model,
                Effort: binding?.Entry.Effort,
                TimeoutMs: (long?)binding?.Entry.Timeout.TotalMilliseconds,
                Label: ExtractRoomLabel(bindings));
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

    /// <summary>
    /// Loads and parses <c>bindings.json</c> if present, degrading to <c>null</c> on any missing file
    /// or load/parse error (fail-open display metadata contract, spec/baton.md §6 schema).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>?> TryLoadBindingsAsync(
        string roomDir, CancellationToken cancellationToken)
    {
        var bindingsPath = BatonPaths.RoomBindingsFile(roomDir);
        if (!File.Exists(bindingsPath))
        {
            return null;
        }

        try
        {
            return await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WorkerBindingConfigException or IOException or UnauthorizedAccessException)
        {
            // spec/baton.md §6 schema states the contract this degrades to.
            return null;
        }
    }

    /// <summary>
    /// Resolves the worker-binding config entry (issue #1503) for whichever step this room's
    /// projection currently calls <c>"Running"</c> — the same worker a caller would see live if they
    /// tailed <c>room_detail</c> right now. Picks the first Running step when a workflow has more than
    /// one in flight at once; a room row carries one binding, not a list. See spec/baton.md §6 schema
    /// for when this comes back absent and why.
    /// </summary>
    private static (string Role, WorkerBindingConfigEntry Entry)? TryResolveRunningBinding(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings,
        IReadOnlyList<FleetStepStatusView> steps,
        IReadOnlyList<FlowEvent> events)
    {
        var runningExecution = steps.FirstOrDefault(s => s.State == "Running" && s.Execution is not null)?.Execution;
        if (runningExecution is null)
        {
            return null;
        }

        string? role = null;
        foreach (var evt in events)
        {
            if (evt is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.ExecutionId.Value == runningExecution)
            {
                role = accepted.Request.Worker;
                break;
            }
        }

        if (role is null)
        {
            return null;
        }

        if (bindings is null)
        {
            return null;
        }

        return bindings.TryGetValue(role, out var entry) ? (role, entry) : null;
    }

    /// <summary>
    /// Extracts a room's <c>--label</c> (#1499) off its loaded <c>bindings.json</c> dictionary.
    /// </summary>
    private static string? ExtractRoomLabel(IReadOnlyDictionary<string, WorkerBindingConfigEntry>? bindings) =>
        bindings?.Values.Select(entry => entry.Label).FirstOrDefault(label => label is not null);

    /// <summary>
    /// Reads a room's <c>--label</c> (#1499) off its own <c>bindings.json</c> on the terminal sentinel
    /// fast path. Full rationale and the fail-open contract: spec/baton.md §6 schema.
    /// </summary>
    private static async Task<string?> TryReadRoomLabelAsync(string roomDir, CancellationToken cancellationToken)
    {
        var bindings = await TryLoadBindingsAsync(roomDir, cancellationToken).ConfigureAwait(false);
        return ExtractRoomLabel(bindings);
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
    bool Rejected = false,
    // #1503: worker role/adapter/model/effort/timeout for this room's Running step, read off
    // bindings.json via TryResolveRunningBindingAsync -- see spec/baton.md §6 schema for exactly
    // which step this reads, when the five fields come back absent, and why timeoutMs isn't a
    // countdown. Adapter/Model here are bindings.json's CURRENT values, not the recorded-at-accept
    // value Steps[].Usage is attributed by since #1567 (ExecutionRequest.Adapter) -- after a
    // failover rebind the two can name different vendors in the same view, neither labelled as such
    // (issue #1584, not fixed here).
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Effort = null,
    [property: JsonPropertyName("timeoutMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TimeoutMs = null,
    // #1499: read via TryReadRoomLabelAsync -- see that method's own doc, spec/baton.md §6 schema.
    [property: JsonPropertyName("label")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Label = null);

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
    // second EngineLivenessProbe call. Present only for a step WorkflowStatusProjector.Project
    // already probes -- a step this projection calls "Running", or (#1513) a "Failed" step still
    // carrying a RetryNotBefore -- the identical gate that projector applies before probing.
    [property: JsonPropertyName("liveness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Liveness = null,
    // #1509/#1522: copied verbatim from WorkflowStatusStepView.Attempt/.MaxAttempts -- see that record's
    // remarks for the derivation (lifetime execution count from StateProjector).
    [property: JsonPropertyName("attempt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Attempt = null,
    [property: JsonPropertyName("maxAttempts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxAttempts = null,
    // #1510: copied verbatim from WorkflowStatusStepView.FailureKind/.RetryEligible -- the engine's
    // own FailureClassification enum member name and RetryEngine.MayRetry's verdict, never
    // re-derived here.
    [property: JsonPropertyName("failureKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureKind = null,
    [property: JsonPropertyName("retryEligible")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RetryEligible = null);
