using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// One execution's usage, per <c>aer status --json</c>'s additive shape (issue #1360):
/// <c>{wallClockMs, tokensIn?, tokensOut?, turns?}</c>. <see cref="WallClockMs"/> is always present —
/// it is derived from the ledger's own <see cref="CoreEvent.ExecutionStarted"/>/
/// <see cref="CoreEvent.ExecutionExited"/> timestamps, which every completed execution has. The token
/// and turn fields are independently omitted from the serialized JSON (never emitted as <c>null</c>,
/// never fabricated as zero) when the vendor's captured stdout carried no such figure — see
/// <see cref="ExecutionUsageProjector"/> for how they are read.
/// </summary>
public sealed record ExecutionUsageView(
    [property: JsonPropertyName("wallClockMs")] long WallClockMs,
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn = null,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Turns = null);

/// <summary>
/// Builds one <see cref="ExecutionUsageView"/> per <see cref="ExecutionId"/> that has both a recorded
/// <see cref="CoreEvent.ExecutionStarted"/> and <see cref="CoreEvent.ExecutionExited"/> (issue #1360)
/// — an execution still running, or one that crashed before Core recorded either lifecycle event, has
/// no wall-clock to derive and is simply absent from the result rather than reported as zero.
/// <para>
/// Token/turn counts are read from the execution's already-captured <c>.stdout.log</c>
/// (<see cref="ExecutionStreamLogger"/>) — never a new ledger event, per the issue's own preference
/// for deriving over recording twice. <c>aer status</c> has no worker-binding context of its own (by
/// design — see <c>StatusCommand</c>'s own remarks), so this projector reconstructs just enough of it
/// from what it is already handed: <see cref="FlowEvent.ExecutionRequestAccepted"/> in
/// <paramref name="entries"/><!-- --> names the execution's worker role, and (when
/// <paramref name="roomDirectoryPath"/> is supplied and the room's <c>bindings.json</c> still exists)
/// that role's own config entry names the adapter it was actually dispatched through. Only that one
/// adapter's <see cref="IWorkerAdapter.TryParseFinalUsage"/> is tried, and only against the last
/// non-blank line of the captured stream — the terminal frame <see cref="IWorkerAdapter.TryParseFinalUsage"/>'s
/// own contract promises callers, never a content-sniff across every line for every registered
/// adapter. A worker whose stdout happens to contain a vendor-shaped usage line it did not itself
/// produce (an operator-supplied <c>command</c> step echoing a captured transcript, for instance)
/// therefore contributes no token/turn fields — attribution failing (no accepted-request record, no
/// bindings file, an adapter name the caller's <paramref name="adapters"/> does not carry) fails
/// closed the same way an unrecognized line does: absent, never fabricated.
/// </para>
/// </summary>
public static class ExecutionUsageProjector
{
    /// <param name="roomDirectoryPath">
    /// The room whose <c>bindings.json</c> attributes each execution to the adapter that actually
    /// dispatched it (issue #1360 F1) — omitted (the default) when a caller has no room path to offer,
    /// in which case no execution can be attributed and every result carries wall-clock only, the same
    /// fail-closed outcome as a room whose bindings file no longer exists.
    /// </param>
    public static IReadOnlyDictionary<string, ExecutionUsageView> BuildByExecutionId(
        IReadOnlyList<LogEntry> entries,
        string artifactsRootPath,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        string? roomDirectoryPath = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(adapters);

        var startedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var exitedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var workerNameByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                workerNameByExecutionId[accepted.Request.ExecutionId.Value] = accepted.Request.Worker;
            }

            if (entry is not LogEntry.CoreLogEntry { WriterUtcTimestamp: { } timestamp } coreEntry)
            {
                continue;
            }

            switch (coreEntry.Event)
            {
                case CoreEvent.ExecutionStarted started:
                    startedTimestamps[started.ExecutionId.Value] = timestamp;
                    break;
                case CoreEvent.ExecutionExited exited:
                    exitedTimestamps[exited.ExecutionId.Value] = timestamp;
                    break;
            }
        }

        var bindings = TryLoadBindings(roomDirectoryPath);

        var result = new Dictionary<string, ExecutionUsageView>(StringComparer.Ordinal);
        foreach (var (executionId, startedAt) in startedTimestamps)
        {
            if (!exitedTimestamps.TryGetValue(executionId, out var exitedAt))
            {
                continue;
            }

            var wallClockMs = (long)(exitedAt - startedAt).TotalMilliseconds;
            if (wallClockMs < 0)
            {
                // #1360 F6 (review): a clamp to 0 here would print the exact "zero standing in for
                // unknown" the issue rules out, indistinguishable from a genuinely instantaneous
                // execution. The only way this fires is a backwards clock step (NTP correction, VM
                // resume) mid-execution -- the honest response is to skip the entry, same as an
                // execution with no exit event yet.
                continue;
            }

            workerNameByExecutionId.TryGetValue(executionId, out var workerName);
            var usage = TryReadWorkerUsage(artifactsRootPath, executionId, workerName, bindings, adapters);
            result[executionId] = new ExecutionUsageView(wallClockMs, usage?.TokensIn, usage?.TokensOut, usage?.Turns);
        }

        return result;
    }

    /// <summary>
    /// Reads <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c> for attribution (issue #1360
    /// F1). Fails closed rather than throwing: a missing file, a missing room path, or a malformed
    /// config all yield an empty map, which <see cref="TryReadWorkerUsage"/> reads as "cannot
    /// attribute this execution" — a status read must never fail because a bindings file it has no
    /// stake in ownership of moved or changed shape since dispatch.
    /// </summary>
    private static IReadOnlyDictionary<string, WorkerBindingConfigEntry> TryLoadBindings(string? roomDirectoryPath)
    {
        if (roomDirectoryPath is null)
        {
            return EmptyBindings;
        }

        var bindingsPath = AerPaths.RoomBindingsFile(roomDirectoryPath);
        if (!File.Exists(bindingsPath))
        {
            return EmptyBindings;
        }

        try
        {
            var json = File.ReadAllText(bindingsPath);
            return WorkerBindingConfigParser.Parse(json, bindingsPath);
        }
        catch (Exception ex) when (ex is WorkerBindingConfigException or IOException)
        {
            return EmptyBindings;
        }
    }

    private static readonly IReadOnlyDictionary<string, WorkerBindingConfigEntry> EmptyBindings =
        new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal);

    /// <summary>
    /// Reads the execution's captured stdout and, only when the execution's worker role is attributed
    /// to a registered adapter via <paramref name="bindings"/>, tries that one adapter's
    /// <see cref="IWorkerAdapter.TryParseFinalUsage"/> against the last non-blank line — the terminal
    /// frame, per that method's own contract. No attribution (no accepted-request record for this
    /// execution, no bindings entry for its worker role, or an adapter name <paramref name="adapters"/>
    /// does not carry) means no line is ever inspected: absent, never a content-sniffed guess.
    /// </summary>
    private static WorkerUsage? TryReadWorkerUsage(
        string artifactsRootPath,
        string executionId,
        string? workerName,
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters)
    {
        if (workerName is null
            || !bindings.TryGetValue(workerName, out var bindingEntry)
            || !adapters.TryGetValue(bindingEntry.Adapter, out var adapter))
        {
            return null;
        }

        var stdoutPath = Path.Combine(
            ArtifactManager.ResolveOutputDirectory(artifactsRootPath, new ExecutionId(executionId)),
            ExecutionStreamLogger.StdoutLogFileName);

        if (!File.Exists(stdoutPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(stdoutPath);
        }
        catch (IOException)
        {
            // Held by a writer that has not yet reached MarkTerminal, or a transient sharing race --
            // not this projector's failure to surface; the caller simply sees no usage this time.
            return null;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return adapter.TryParseFinalUsage(line, out var usage) ? usage : null;
        }

        return null;
    }
}
