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
/// for deriving over recording twice. <c>aer status</c> has no worker-binding context (by design — see
/// <c>StatusCommand</c>'s own remarks), so which vendor produced a given execution is not known here;
/// instead, every registered adapter's <see cref="IWorkerAdapter.TryParseFinalUsage"/> is tried against
/// each line, and at most one ever matches, because the vendors' terminal envelopes key on different
/// JSON properties (claude's top-level <c>type</c> vs. agy's top-level <c>event</c>) — asserted by
/// <c>ClaudeFinalUsageParsingTests</c>/<c>AgyFinalUsageParsingTests</c>' cross-vendor cases. A vendor
/// with no structured usage report at all (or an execution dispatched in plain-text mode, which is
/// stdout's default — see docs/vendor-capabilities.md) simply never matches, leaving the token/turn
/// fields absent.
/// </para>
/// </summary>
public static class ExecutionUsageProjector
{
    public static IReadOnlyDictionary<string, ExecutionUsageView> BuildByExecutionId(
        IReadOnlyList<LogEntry> entries,
        string artifactsRootPath,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(adapters);

        var startedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var exitedTimestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
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

        var result = new Dictionary<string, ExecutionUsageView>(StringComparer.Ordinal);
        foreach (var (executionId, startedAt) in startedTimestamps)
        {
            if (!exitedTimestamps.TryGetValue(executionId, out var exitedAt))
            {
                continue;
            }

            var wallClockMs = Math.Max(0, (long)(exitedAt - startedAt).TotalMilliseconds);
            var usage = TryReadWorkerUsage(artifactsRootPath, executionId, adapters);
            result[executionId] = new ExecutionUsageView(wallClockMs, usage?.TokensIn, usage?.TokensOut, usage?.Turns);
        }

        return result;
    }

    /// <summary>
    /// Scans the execution's captured stdout for the last line any registered adapter recognizes as
    /// its terminal usage report. Last-wins rather than first-wins: a vendor's own progress lines never
    /// match (see each adapter's <see cref="IWorkerAdapter.TryParseFinalUsage"/> remarks), so in
    /// practice at most one line ever matches at all, and last-wins is only a guard against a
    /// pathological multi-match rather than a load-bearing choice.
    /// </summary>
    private static WorkerUsage? TryReadWorkerUsage(
        string artifactsRootPath, string executionId, IReadOnlyDictionary<string, IWorkerAdapter> adapters)
    {
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

        WorkerUsage? found = null;
        foreach (var line in lines)
        {
            foreach (var adapter in adapters.Values)
            {
                if (adapter.TryParseFinalUsage(line, out var usage) && usage is not null)
                {
                    found = usage;
                }
            }
        }

        return found;
    }
}
