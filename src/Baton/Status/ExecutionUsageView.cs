using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;

namespace Baton.Status;

/// <summary>
/// One execution's usage, per <c>baton status --json</c>'s additive shape (issue #1360, extended by
/// #1569). Canonical field list and wire contract at <c>spec/baton.md</c> §3, not restated here.
/// <see cref="WallClockMs"/> is always present — it is derived from the ledger's own
/// <see cref="CoreEvent.ExecutionStarted"/>/<see cref="CoreEvent.ExecutionExited"/> timestamps, which
/// every completed execution has. Every other field is independently omitted from the serialized JSON
/// (never emitted as <c>null</c>, never fabricated as zero) when the vendor's captured stdout carried
/// no such figure — see <see cref="ExecutionUsageProjector"/> for how they are read. These fields are
/// per-execution attribution, not a complete burn figure — see <c>spec/baton.md</c> §3/§7 for why.
/// <para>
/// #1706's reconciliation triple. <see cref="BilledTokens"/> is the AUTHORITATIVE per-execution billed
/// figure — <c>TokensIn + TokensOut + CacheCreationTokens</c> off the terminal line, which on claude is
/// now the whole-tree <c>modelUsage</c> read (<c>ClaudeUsageParser.TryParseFinalUsage</c>).
/// <see cref="LiveBilledTokens"/> is what <see cref="Mutation.TokenBudgetMonitor"/> — the real one,
/// replayed over the same captured stream, never a second implementation of its arithmetic — saw while
/// the execution was running, i.e. the quantity a budget actually arrested on.
/// <see cref="BilledUnderReadTokens"/> is their difference: how much of this room's real spend the live
/// budget could not see. All three are omitted together unless both figures were computable; why the
/// difference is still emitted at zero, and why this is derived on read rather than journaled, are
/// <c>spec/baton.md</c> §3's own statement of the wire contract, not restated here.
/// </para>
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
    long? ThinkingTokens = null,
    [property: JsonPropertyName("billedTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledTokens = null,
    [property: JsonPropertyName("liveBilledTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? LiveBilledTokens = null,
    [property: JsonPropertyName("billedUnderReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledUnderReadTokens = null);

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
        BuildByExecutionId<IWorkerUsageParser>(entries, artifactsRootPath, adapters, roomDirectoryPath);

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

            // #1583: a rebound resubmission overrides the frozen ExecutionRequest's recorded adapter.
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.StepRebound rebound })
            {
                if (rebound.NewAdapter is { Length: > 0 } newAdapter)
                {
                    recordedAdapterByExecutionId[rebound.ForExecutionId.Value] = newAdapter;
                }
                else
                {
                    recordedAdapterByExecutionId.Remove(rebound.ForExecutionId.Value);
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

            workerNameByExecutionId.TryGetValue(executionId, out var workerName);
            recordedAdapterByExecutionId.TryGetValue(executionId, out var recordedAdapter);
            var reading = TryReadWorkerUsage(artifactsRootPath, executionId, workerName, recordedAdapter, bindings, adapters);
            var usage = reading?.Terminal;
            // #1706: the terminal billed total, on the SAME arithmetic WorkerUsage.BilledTokens
            // documents (input + output + cache_creation, never cache_read). Null unless the terminal
            // line reported at least one of those three -- never a fabricated zero.
            long? billed = usage is null || (usage.TokensIn is null && usage.TokensOut is null && usage.CacheCreationTokens is null)
                ? null
                : (usage.TokensIn ?? 0) + (usage.TokensOut ?? 0) + (usage.CacheCreationTokens ?? 0);
            var liveBilled = reading?.LiveBilled;
            result[executionId] = new ExecutionUsageView(
                wallClockMs,
                usage?.TokensIn,
                usage?.TokensOut,
                usage?.Turns,
                usage?.CacheReadTokens,
                usage?.CacheCreationTokens,
                usage?.ThinkingTokens,
                billed,
                billed is null ? null : liveBilled,
                billed is { } terminalBilled && liveBilled is { } live ? terminalBilled - live : null);
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

    /// <summary>
    /// #1706: one captured stream read once, yielding both the terminal reading and the live-billed Σ
    /// the budget monitor would have accumulated over the same bytes. Kept together because they come
    /// from the same file read — splitting them would read a multi-megabyte log twice to answer one
    /// question.
    /// <para>
    /// Cost, since <c>fleet_status</c> polls this: the replay hands every captured line to the vendor
    /// parser, which parses it up to three times (tool name, tool-step count, incremental usage). Before
    /// #1706 this projector parsed exactly one line per execution. Bounded by the stream logger's own
    /// 8 MiB-plus-one-rollover ceiling rather than by anything here, and measured at ~9 MB for the
    /// largest room on the machine this was developed against.
    /// </para>
    /// </summary>
    private sealed record UsageReading(WorkerUsage? Terminal, long? LiveBilled);

    private static UsageReading? TryReadWorkerUsage<TParser>(
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

        if (adapterName is null)
        {
            return null;
        }

        // Overload resolution against an unbound TParser can never apply the non-generic overload's
        // `?? StandardWorkerUsageParsers.Default` fallback (invariance -- see #1590) -- so a null
        // registry is resolved against the built-in parsers here instead, once, regardless of which
        // overload the caller went through.
        IWorkerUsageParser? adapter = adapters is not null
            ? adapters.TryGetValue(adapterName, out var registered) ? registered : null
            : StandardWorkerUsageParsers.Default.TryGetValue(adapterName, out var defaultParser) ? defaultParser : null;

        if (adapter is null)
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

        WorkerUsage? terminal = null;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            terminal = adapter.TryParseFinalUsage(line, out var usage) ? usage : null;
            break;
        }

        // #1706 review: the replay must span the WHOLE stream, and `.stdout.log` is only its tail once
        // ExecutionStreamLogger has rolled over at 8 MiB (its single `.stdout.log.1`, written FIRST and
        // therefore replayed first). Reading the current file alone was harmless while this projector
        // needed exactly one line -- the terminal `result`, always in the current file -- and became a
        // defect the moment #1706 added an accumulation over every line: measured on a real rolled room
        // (`dispatch-implement-fd196a41`), the current file alone yields 30,593 against a terminal
        // 356,563, a fabricated 91% "under-read" that is pure rollover artifact and would have been
        // read as this vendor's worst measured room. A missing or unreadable rollover file contributes
        // nothing rather than failing the read -- it is absent on every execution that never grew past
        // the threshold, which is nearly all of them.
        var rolloverPath = Path.Combine(Path.GetDirectoryName(stdoutPath)!, ExecutionStreamLogger.StdoutRolloverFileName);
        string[] rolledLines = [];
        if (File.Exists(rolloverPath))
        {
            try
            {
                rolledLines = File.ReadAllLines(rolloverPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same posture as the current-file arm above -- except that here the honest response is
                // to report NO live figure at all rather than a partial one, since a partial Σ over the
                // tail alone is exactly the fabricated under-read this whole comment exists about.
                return new UsageReading(terminal, null);
            }
        }

        // #1706: the REAL monitor, no triggers armed, replayed over the same captured stream -- so the
        // live figure this reports and the one a running execution actually arrests on cannot drift
        // apart, which two separate implementations of the same Σ inevitably would.
        //
        // It must be handed the SAME parser the live monitor was handed, which is
        // StandardWorkerUsageParsers.Default[adapterName] (MutationInterface.DispatchAndRecordOutcomeAsync),
        // NOT the adapter resolved above. Those differ, and silently: IWorkerUsageParser's
        // TryParseIncrementalUsage/CountToolSteps have default implementations returning false/0, and
        // the vendor ADAPTERS delegate only TryParseFinalUsage -- so replaying through an adapter reads
        // zero usage lines and reports no live figure at all, on every real execution. Caught by
        // ExecutionUsageProjectorTests' own #1706 arms, which go through WorkerAdapterRegistry.Default
        // exactly as `baton status` does. When the vendor is not one this engine ships a parser for,
        // the resolved adapter is still tried rather than skipping the replay outright.
        var replayParser = StandardWorkerUsageParsers.Default.TryGetValue(adapterName, out var liveParser) ? liveParser : adapter;
        var replayMonitor = new TokenBudgetMonitor(budget: null, maxToolSteps: null, replayParser);
        foreach (var line in rolledLines)
        {
            replayMonitor.OnStdoutLine(line);
        }

        foreach (var line in lines)
        {
            replayMonitor.OnStdoutLine(line);
        }

        return new UsageReading(terminal, replayMonitor.SnapshotUsage().BilledTokens);
    }
}
