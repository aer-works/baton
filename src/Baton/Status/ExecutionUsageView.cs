using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// One execution's usage, per <c>baton status --json</c>'s additive shape (issue #1360, extended by
/// #1569): <c>{wallClockMs, tokensIn?, tokensOut?, turns?, cacheReadTokens?, cacheCreationTokens?,
/// thinkingTokens?}</c> — canonical field list at <c>spec/baton.md</c> §3, not restated further than
/// this type. <see cref="WallClockMs"/> is always present — it is derived from the ledger's own
/// <see cref="CoreEvent.ExecutionStarted"/>/<see cref="CoreEvent.ExecutionExited"/> timestamps, which
/// every completed execution has. Every other field is independently omitted from the serialized JSON
/// (never emitted as <c>null</c>, never fabricated as zero) when the vendor's captured stdout carried
/// no such figure — see <see cref="ExecutionUsageProjector"/> for how they are read. These fields are
/// per-execution attribution, not a complete burn figure: <c>spec/baton.md</c> §7 rules lane-log
/// accumulation is never the reset-time source of truth, and claude's own <c>tokensOut</c> is
/// separately measured to exclude subagent spend (<c>ClaudeWorkerAdapter.TryParseFinalUsage</c>'s own
/// doc comment, <c>src/Baton.Vendors/ClaudeWorkerAdapter.cs</c>).
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
    int? Turns = null,
    [property: JsonPropertyName("cacheReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheCreationTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens = null,
    [property: JsonPropertyName("thinkingTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens = null);

/// <summary>
/// Builds one <see cref="ExecutionUsageView"/> per <see cref="ExecutionId"/> that has both a recorded
/// <see cref="CoreEvent.ExecutionStarted"/> and <see cref="CoreEvent.ExecutionExited"/> (issue #1360)
/// — an execution still running, or one that crashed before Core recorded either lifecycle event, has
/// no wall-clock to derive and is simply absent from the result rather than reported as zero.
/// <para>
/// Token/turn counts are read from the execution's already-captured <c>.stdout.log</c>
/// (<see cref="ExecutionStreamLogger"/>) — never a new ledger event, per the issue's own preference
/// for deriving over recording twice. Which adapter's parser to trust is resolved by preferring the
/// accepted request's own recorded <see cref="ExecutionRequest.Adapter"/> — see that field's doc
/// comment (issue #1567) for why, and for the one path where it is not the guarantee it usually is.
/// Only the resolved adapter's <see cref="IWorkerUsageParser.TryParseFinalUsage"/> is tried, and
/// only against the last non-blank line of the captured stream.
/// </para>
/// </summary>
public static class ExecutionUsageProjector
{
    private const string RoomBindingsFileName = "bindings.json";

    public static IReadOnlyDictionary<string, ExecutionUsageView> BuildByExecutionId(
        IReadOnlyList<LogEntry> entries,
        string artifactsRootPath,
        IReadOnlyDictionary<string, IWorkerUsageParser>? adapters = null,
        string? roomDirectoryPath = null) =>
        BuildByExecutionId<IWorkerUsageParser>(entries, artifactsRootPath, adapters ?? StandardWorkerUsageParsers.Default, roomDirectoryPath);

    public static IReadOnlyDictionary<string, ExecutionUsageView> BuildByExecutionId<TParser>(
        IReadOnlyList<LogEntry> entries,
        string artifactsRootPath,
        IReadOnlyDictionary<string, TParser>? adapters = null,
        string? roomDirectoryPath = null)
        where TParser : IWorkerUsageParser
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var startedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var exitedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var workerNameByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var recordedAdapterByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                workerNameByExecutionId[accepted.Request.ExecutionId.Value] = accepted.Request.Worker;
                if (accepted.Request.Adapter is { Length: > 0 } recordedAdapter)
                {
                    recordedAdapterByExecutionId[accepted.Request.ExecutionId.Value] = recordedAdapter;
                }
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

            recordedAdapterByExecutionId.TryGetValue(executionId, out var recordedAdapter);
            var usage = TryReadWorkerUsage(artifactsRootPath, executionId, workerName, recordedAdapter, bindings, adapters);
            result[executionId] = new ExecutionUsageView(
                wallClockMs,
                usage?.TokensIn,
                usage?.TokensOut,
                usage?.Turns,
                usage?.CacheReadTokens,
                usage?.CacheCreationTokens,
                usage?.ThinkingTokens);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> TryLoadBindings(string? roomDirectoryPath)
    {
        if (roomDirectoryPath is null)
        {
            return EmptyBindings;
        }

        var bindingsPath = Path.Combine(roomDirectoryPath, RoomBindingsFileName);
        if (!File.Exists(bindingsPath))
        {
            return EmptyBindings;
        }

        try
        {
            using var stream = File.OpenRead(bindingsPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EmptyBindings;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("Adapter", out var adapterProp)
                    && adapterProp.ValueKind == JsonValueKind.String
                    && adapterProp.GetString() is { } adapterName
                    && !string.IsNullOrWhiteSpace(adapterName))
                {
                    result[prop.Name] = adapterName;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return EmptyBindings;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyBindings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static WorkerUsage? TryReadWorkerUsage<TParser>(
        string artifactsRootPath,
        string executionId,
        string? workerName,
        string? recordedAdapter,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, TParser>? adapters)
        where TParser : IWorkerUsageParser
    {
        // #1567: the recorded adapter wins whenever present -- see ExecutionRequest.Adapter's doc for
        // why, and for the resubmit-path case (#1583) where it isn't the guarantee it usually is. The
        // bindings.json fallback below covers lines that predate the field, and non-process
        // dispatches, which never carry one.
        var adapterName = recordedAdapter;
        if (adapterName is null && workerName is not null)
        {
            bindings.TryGetValue(workerName, out adapterName);
        }

        if (adapterName is null
            || adapters is null
            || !adapters.TryGetValue(adapterName, out var adapter))
        {
            return null;
        }

        var id = new ExecutionId(executionId);
        var stdoutPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRootPath, id), ExecutionStreamLogger.StdoutLogFileName);
        if (!File.Exists(stdoutPath))
        {
            // #1360 F7 (review): a retention sweep moves the whole execution directory -- .stdout.log
            // included -- to the pruned location (RoomRetentionSweep -> ArtifactPruner). Without this
            // fallback, terminal.json (written before any sweep) and a post-sweep status read of the
            // same unchanged room would disagree about a figure both once knew.
            stdoutPath = Path.Combine(ArtifactManager.ResolvePrunedOutputDirectory(artifactsRootPath, id), ExecutionStreamLogger.StdoutLogFileName);
            if (!File.Exists(stdoutPath))
            {
                return null;
            }
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(stdoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by a writer that has not yet reached MarkTerminal, a transient sharing race, or an
            // ACL'd stream log (UnauthorizedAccessException is not an IOException in .NET, so the
            // review's minor finding needed its own arm) -- none of these are this projector's failure
            // to surface; the caller simply sees no usage this time.
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
