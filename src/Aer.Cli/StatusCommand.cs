using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Outcomes;
using Aer.Flow.Projection;
using Aer.Flow.Status;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli;

/// <summary>
/// <c>aer status</c> (#730): a read-only projection of a room directory's recorded events —
/// "this session's workaround was hand-rolled monitors polling PIDs and tailing <c>flow.jsonl</c>
/// by path", which this replaces with the product's own register. Every field printed comes from
/// <see cref="StateProjector.Project"/> — the same projection <see cref="RunCommand"/>,
/// <see cref="CancelCommand"/> and <see cref="Aer.Ui.Core.RoomProjectionLoader"/> already call — so
/// there is exactly one place "what does this event log mean" is computed, never a second reader of
/// the format here.
/// <para>
/// Deliberately never takes <see cref="Aer.Flow.Concurrency.ConcurrencyGuard"/>'s lock and never
/// constructs a <see cref="FlowEventLogWriter"/>: this is the one command in <c>Aer.Cli</c> that can
/// run concurrently with a live <c>aer run</c> pump on the same room directory, which is the whole
/// point of a status/watch command. It also never resolves a worker binding (no <c>--bindings</c>
/// option exists on <see cref="StatusOptions"/> at all) — nothing here dispatches, so there is
/// nothing to bind.
/// </para>
/// <para>
/// #1356's one exception to "every field comes from <see cref="StateProjector.Project"/>": a room
/// with no <c>flow.jsonl</c> yet has nothing for that projection to read, so a pre-ledger failure is
/// answered from its terminal sentinel (<see cref="TerminalSentinelWriter"/>) instead — see the
/// early branch in <see cref="ExecuteAsync"/>. <c>--json</c> emits <see cref="WorkflowStatusView"/>
/// (<see cref="WorkflowStatusProjector"/>), built from that same projected state in the normal case.
/// </para>
/// </summary>
public static class StatusCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";

    /// <summary>
    /// How often <c>--follow</c> re-checks <c>flow.jsonl</c>'s length for growth. A modest,
    /// fixed interval rather than a <see cref="FileSystemWatcher"/> — file-system change
    /// notifications are unreliable across platforms (missed events on some network/CI
    /// filesystems, duplicate events on others), where a length poll on a plain
    /// <see cref="FileInfo"/> always tells the truth.
    /// </summary>
    private const int PollIntervalMs = 500;

    /// <exception cref="SnapshotLoadException">
    /// The room directory has no persisted snapshot — a nonexistent directory and an existing one
    /// that was never started via <c>aer run</c> fail identically here (both are just "no
    /// <c>snapshot.json</c> at this path"), or the persisted snapshot is malformed.
    /// </exception>
    /// <remarks>
    /// Cancellation is two contracts (#999): under <see cref="StatusOptions.Follow"/> it is how a
    /// follow ends, so this method returns cleanly; a cancelled one-shot probe throws
    /// <see cref="OperationCanceledException"/> instead — see the catch below for why.
    /// </remarks>
    public static async Task ExecuteAsync(
        StatusOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, LogFileName);

        // #1356 point 3: a room that fails during provisioning/validation may never get a
        // flow.jsonl (bindings/workflow validation can fail before snapshot.json exists too, e.g. a
        // dispatch materialization error) or may get one only much later. Its terminal sentinel is
        // then the only queryable answer, and it wins over the ledger precisely because there is no
        // ledger to be authoritative instead — once the room has a REAL ledger (RoomLedgerProbe,
        // #1374 F1 -- a zero-byte flow.jsonl left by a room-held refusal does not count), this branch
        // never runs again and the ledger (spec §7's system of record) is read below as usual.
        if (!RoomLedgerProbe.HasLedger(options.RoomDirectoryPath))
        {
            var sentinel = await TerminalSentinelWriter.TryReadAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
            if (sentinel is not null)
            {
                PrintSentinel(output, options.Json, sentinel);
                return;
            }
        }

        // Never Directory.CreateDirectory here (unlike RunCommand): a status probe against a room
        // that was never started must report the same typed failure, not conjure the directory
        // into existence as a side effect of looking at it.
        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'aer status' " +
                "projects a room 'aer run' has already started, and never binds one fresh.");
        }

        try
        {
            var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
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

            var checkpoint = ProjectionCheckpointStore.Load(options.RoomDirectoryPath);
            var state = StateProjector.Project(events, snapshot, checkpoint);

            if (options.Json)
            {
                // #1356 point 1: the SAME state just projected above, not a second read of the
                // ledger — one derivation, two renderings. Nothing else reaches stdout in this mode.
                // #1360: entries is the same list already read above, not a second ledger read.
                var view = WorkflowStatusProjector.Project(state, snapshot, options.RoomDirectoryPath, entries, WorkerAdapterRegistry.Default);
                output.WriteLine(JsonSerializer.Serialize(view));
                return;
            }

            PrintState(output, state, logPath, events, entries, options.RoomDirectoryPath);

            if (options.Follow)
            {
                var artifactsDir = Path.Combine(options.RoomDirectoryPath, Aer.Flow.Artifacts.ArtifactManager.ArtifactsDirectoryName);
                TailStreams(output, artifactsDir, new Dictionary<string, long>(StringComparer.Ordinal));
            }

            if (!options.Follow || state.Status == WorkflowStatus.Terminal)
            {
                return;
            }

            await FollowAsync(output, reader, snapshot, events.Count, logPath, options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (options.Follow && cancellationToken.IsCancellationRequested)
        {
            // #999: cancelling a follow is the normal way to stop it, whichever await the token
            // interrupts — FollowAsync's own delay-loop catch only covered the poll's Task.Delay,
            // so cancellation landing inside a journal read escaped as TaskCanceledException. A
            // cancelled NON-follow probe still throws: it produced no answer, and returning as if
            // it had would be fail-open.
        }
    }

    /// <summary>
    /// Polls <paramref name="logPath"/>'s length for growth, printing every event newer than
    /// <paramref name="printedEventCount"/> as it appears, until re-projecting reaches
    /// <see cref="WorkflowStatus.Terminal"/> or <paramref name="cancellationToken"/> is cancelled.
    /// Tails stdout/stderr streams of running executions interleaved with event lines.
    /// </summary>
    private static async Task FollowAsync(
        TextWriter output,
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        int printedEventCount,
        string logPath,
        string roomDirectoryPath,
        CancellationToken cancellationToken)
    {
        var lastObservedLength = -1L;
        var artifactsDir = Path.Combine(roomDirectoryPath, Aer.Flow.Artifacts.ArtifactManager.ArtifactsDirectoryName);
        var streamOffsets = new Dictionary<string, long>(StringComparer.Ordinal);

        while (true)
        {
            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var logFile = new FileInfo(logPath);
            var currentLength = logFile.Exists ? logFile.Length : 0;

            if (currentLength != lastObservedLength)
            {
                lastObservedLength = currentLength;

                var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                for (var i = printedEventCount; i < events.Count; i++)
                {
                    output.WriteLine(events[i]);
                }

                printedEventCount = events.Count;

                var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
                var state = StateProjector.Project(events, snapshot, checkpoint);
                TailStreams(output, artifactsDir, streamOffsets);

                if (state.Status == WorkflowStatus.Terminal)
                {
                    output.WriteLine($"Workflow status: {state.Status}");

                    // #1360 F5 (review): the one invocation shape where a human is actually watching
                    // for what a run cost never re-rendered the roll-up PrintState prints before a
                    // follow starts -- a fresh read here (once, at follow's own exit, not per poll)
                    // is cheaper than restructuring the loop above to carry timestamped LogEntry
                    // alongside the plain FlowEvent list it already tracks.
                    var finalEntries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
                    var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
                    var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
                        finalEntries, artifactsRootPath, WorkerAdapterRegistry.Default, roomDirectoryPath);
                    output.WriteLine(FormatUsageSummary(usageByExecutionId));
                    return;
                }
            }
            else
            {
                TailStreams(output, artifactsDir, streamOffsets);
            }
        }
    }

    // Public as a test seam, matching FormatStepStatus and EscapeNonPrintable: the reader-side
    // rollover behavior is asserted directly (the workflow review's medium finding).
    public static void TailStreams(TextWriter output, string artifactsDir, Dictionary<string, long> streamOffsets)
    {
        if (!Directory.Exists(artifactsDir))
        {
            return;
        }

        foreach (var execDir in Directory.GetDirectories(artifactsDir, "execution_*"))
        {
            TailStreamFile(
                output,
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StdoutLogFileName),
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StdoutRolloverFileName),
                streamOffsets);

            TailStreamFile(
                output,
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StderrLogFileName),
                Path.Combine(execDir, Aer.Flow.Dispatch.ExecutionStreamLogger.StderrRolloverFileName),
                streamOffsets);
        }
    }

    private static void TailStreamFile(TextWriter output, string logPath, string rolloverPath, Dictionary<string, long> streamOffsets)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        streamOffsets.TryGetValue(logPath, out var offset);

        // Rollover detection keys on the rollover FILE'S identity (its mtime advances every time
        // the writer rolls), never on a length comparison: a fresh file whose length equals the
        // stored offset made `length < offset` miss the rollover entirely and silently drop the
        // new content -- found by the reader-side test the workflow review demanded. The rollover
        // path doubles as its own dict key; log and rollover paths are distinct strings.
        if (File.Exists(rolloverPath))
        {
            streamOffsets.TryGetValue(rolloverPath, out var seenRolloverTicks);
            var rolloverFi = new FileInfo(rolloverPath);
            var ticks = rolloverFi.LastWriteTimeUtc.Ticks;
            if (ticks != seenRolloverTicks)
            {
                // The rolled file IS the previous current file: emit its unseen tail, then the
                // fresh file reads from the start.
                if (rolloverFi.Length > offset)
                {
                    ReadAndOutputBytes(output, rolloverPath, offset, rolloverFi.Length - offset);
                }

                offset = 0;
                streamOffsets[rolloverPath] = ticks;
            }
        }

        var fi = new FileInfo(logPath);
        if (fi.Length > offset)
        {
            var bytesRead = ReadAndOutputBytes(output, logPath, offset, fi.Length - offset);
            offset += bytesRead;
        }

        streamOffsets[logPath] = offset;
    }

    private static long ReadAndOutputBytes(TextWriter output, string path, long offset, long count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = fs.Read(buffer, totalRead, (int)(count - totalRead));
                if (read <= 0) break;
                totalRead += read;
            }

            if (totalRead > 0)
            {
                var escaped = EscapeNonPrintable(buffer.AsSpan(0, totalRead));
                output.Write(escaped);
            }

            return totalRead;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static string EscapeNonPrintable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(bytes.Length);
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var chars = new char[2];

        for (int i = 0; i < bytes.Length;)
        {
            int bytesUsed, charsUsed;
            bool completed;
            decoder.Convert(bytes.Slice(i, 1).ToArray(), 0, 1, chars, 0, 2, false, out bytesUsed, out charsUsed, out completed);

            if (charsUsed > 0)
            {
                for (int c = 0; c < charsUsed; c++)
                {
                    var ch = chars[c];
                    if (ch is '\n' or '\t' || IsPrintable(ch))
                    {
                        sb.Append(ch);
                    }
                    else
                    {
                        var code = (ushort)ch;
                        if (code <= 0xFF)
                        {
                            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{code:x2}");
                        }
                        else
                        {
                            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\x{code:x4}");
                        }
                    }
                }

                i += bytesUsed;
            }
            else
            {
                // charsUsed == 0 with the byte consumed means the decoder BUFFERED a valid-so-far
                // lead/continuation byte of a multi-byte sequence -- not an invalid byte. Emitting
                // an escape here duplicated every non-ASCII character as \xNN + the decoded char
                // (the workflow review's high finding). Advance silently; the decoder produces the
                // character when the sequence completes, and the flush below drains a sequence
                // truncated at end-of-input as U+FFFD (genuinely invalid bytes already surface as
                // U+FFFD through the decoder's replacement fallback).
                i++;
            }
        }

        var flushed = new char[2];
        decoder.Convert([], 0, 0, flushed, 0, 2, flush: true, out _, out var flushedChars, out _);
        for (int c = 0; c < flushedChars; c++)
        {
            sb.Append(flushed[c]);
        }

        return sb.ToString();
    }

    private static bool IsPrintable(char ch)
    {
        if (ch == ' ') return true;
        var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
        return cat is not (System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.Surrogate
            or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.SpaceSeparator);
    }

    /// <summary>
    /// Renders a room whose only queryable record is its terminal sentinel (no <c>flow.jsonl</c> —
    /// see the pre-ledger branch in <see cref="ExecuteAsync"/>). Mirrors <see cref="PrintState"/>'s
    /// first line in human mode; in <c>--json</c> mode re-serializes the already-parsed
    /// <paramref name="sentinel"/> rather than trusting its on-disk bytes verbatim. Only ever called
    /// with a sentinel <see cref="TerminalSentinelWriter.TryReadAsync"/> already parsed successfully —
    /// a malformed <c>terminal.json</c> comes back <c>null</c> from that call and is handled by the
    /// caller before this method is reached, not passed in here.
    /// </summary>
    private static void PrintSentinel(TextWriter output, bool json, WorkflowStatusView sentinel)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(sentinel));
            return;
        }

        output.WriteLine($"Workflow status: {sentinel.State}");
        if (!string.IsNullOrWhiteSpace(sentinel.Error))
        {
            output.WriteLine($"  {sentinel.Error}");
        }
    }

    private static void PrintState(
        TextWriter output, FlowState state, string logPath, IReadOnlyList<FlowEvent> events, IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath)
    {
        output.WriteLine($"Workflow status: {state.Status}");
        output.WriteLine($"Log last updated: {ResolveLogUpdatedAt(logPath)}");

        var eventTimestamps = WorkflowStatusProjector.ExtractEventTimestamps(entries);

        foreach (var step in state.Steps)
        {
            var executionText = step.LatestExecutionId?.ToString() ?? "none";
            var statusText = FormatStepStatus(step, events);
            var timeText = step.LatestExecutionId is not null && eventTimestamps.TryGetValue(step.LatestExecutionId.Value.Value, out var time)
                ? $" @ {time:O}"
                : string.Empty;
            output.WriteLine($"  {step.StepId}: {statusText} (execution={executionText}{timeText})");
        }

        foreach (var stepLess in state.StepLessExecutions)
        {
            output.WriteLine($"  (supplementary) {stepLess.Worker}: execution={stepLess.ExecutionId} pending");
        }

        // #1360: one rolled-up line for the whole room, never per step here -- a machine consumer
        // wanting per-execution figures already has them from `--json`'s usage/linkedFromUsage.
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
            entries, artifactsRootPath, WorkerAdapterRegistry.Default, roomDirectoryPath);
        output.WriteLine(FormatUsageSummary(usageByExecutionId));
    }

    /// <summary>
    /// The room-wide roll-up (#1360's "one rolled-up line in human aer status"). Sums the per-execution
    /// <see cref="ExecutionUsageView.WallClockMs"/> figures across every execution with both a start
    /// and exit event, since that half is always derivable; a token/turn figure is summed and its
    /// reporting count disclosed only when at least one execution actually carried it — an adapter (or
    /// a text-mode dispatch) that reports none is silence, not a printed zero.
    /// <para>
    /// Labelled "execution time", not "wall-clock" (#1360 F4, review): parallel steps' executions
    /// overlap in real time, so this sum can exceed the room's own actual elapsed time — it is
    /// aggregate execution time, the same quantity <see cref="ExecutionUsageView.WallClockMs"/> names
    /// per execution, not a claim about how long the room itself took end to end.
    /// </para>
    /// </summary>
    private static string FormatUsageSummary(IReadOnlyDictionary<string, ExecutionUsageView> usageByExecutionId)
    {
        if (usageByExecutionId.Count == 0)
        {
            return "Usage: no completed executions yet.";
        }

        var totalExecutionSeconds = usageByExecutionId.Values.Sum(u => u.WallClockMs) / 1000.0;
        var parts = new List<string>
        {
            $"{usageByExecutionId.Count} execution(s)",
            $"{totalExecutionSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s execution time",
        };

        AppendTokenPart(parts, usageByExecutionId, u => u.TokensIn, "tokens in");
        AppendTokenPart(parts, usageByExecutionId, u => u.TokensOut, "tokens out");

        var turnsReporting = usageByExecutionId.Values.Where(u => u.Turns is not null).ToList();
        if (turnsReporting.Count > 0)
        {
            parts.Add($"{turnsReporting.Sum(u => u.Turns!.Value)} turns ({turnsReporting.Count}/{usageByExecutionId.Count} reporting)");
        }

        return "Usage: " + string.Join(", ", parts);
    }

    private static void AppendTokenPart(
        List<string> parts,
        IReadOnlyDictionary<string, ExecutionUsageView> usageByExecutionId,
        Func<ExecutionUsageView, long?> selector,
        string label)
    {
        var reporting = usageByExecutionId.Values.Where(u => selector(u) is not null).ToList();
        if (reporting.Count == 0)
        {
            return;
        }

        var total = reporting.Sum(u => selector(u)!.Value);
        parts.Add($"{total} {label} ({reporting.Count}/{usageByExecutionId.Count} reporting)");
    }

    public static string FormatStepStatus(StepState step, IReadOnlyList<FlowEvent> events)
    {
        // A Failed step carrying a RetryNotBefore has a StepRetryScheduled recorded for it (#594)
        // -- the machine's own paced wait, whether an ordinary backoff or a quota park (#817).
        // StateProjector clears RetryNotBefore the moment a fresh ExecutionRequestAccepted lands
        // for the step, so latest-state-wins here for free: a step that has since retried or
        // succeeded never reaches this branch.
        // Post-#1115 / 0026 §5 (#1116): an un-obligated ExhaustedUntil step (null RetryNotBefore;
        // see MutationInterface.GetRetryObligations) renders
        // "parked (vendor quota) — reset unknown".
        if (step.Status == StepStatus.Failed)
        {
            if (step.LatestFailureClassification == FailureClassification.ExhaustedUntil && step.RetryNotBefore is null)
            {
                return "parked (vendor quota) — reset unknown";
            }

            if (step.RetryNotBefore is not null)
            {
                return FormatParkedStatus(step);
            }
        }

        // Probe ONLY steps claiming a live engine. Paused is a mask over an already-terminal
        // outcome (StateProjector) -- its engine has legitimately exited, and probing it stamped
        // every healthy paused step "crash recovery will classify" (the workflow review's high
        // finding). Pending has no execution yet, so no liveness claim applies there either.
        if (step.Status is not StepStatus.Running)
        {
            return step.Status.ToString();
        }

        if (step.LatestExecutionId is null)
        {
            return step.Status.ToString();
        }

        var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>()
            .FirstOrDefault(e => e.Request.ExecutionId == step.LatestExecutionId);

        var probeResult = EngineLivenessProbe.Probe(accepted?.EnginePid, accepted?.EngineStartTime);

        return probeResult.Status switch
        {
            EngineLivenessStatus.Alive => step.Status.ToString(),
            EngineLivenessStatus.Dead => $"{step.Status} — engine not alive; crash recovery will classify on next pump",
            EngineLivenessStatus.Unknown => $"liveness unknown ({probeResult.Why})",
            _ => $"liveness unknown ({probeResult.Why})",
        };
    }

    /// <summary>
    /// An operator reading status wants "when does work resume", not a UTC instant to convert by
    /// hand (#817) -- <see cref="StepState.RetryNotBefore"/> is rendered in local time, date
    /// always included: the dominant real park is a plan-cap wait that can cross midnight or span
    /// days (0026), where a bare clock time is ambiguous. A constant format also keeps rendering
    /// independent of when status is run, which a same-day/other-day fork would not. The
    /// classification is <see cref="StepState.LatestFailureClassification"/> as recorded on the
    /// attempt <see cref="FlowEvent.StepRetryScheduled"/> is pacing, mapped to the operator-facing
    /// word: <see cref="FailureClassification.ExhaustedUntil"/> is the vendor-quota wait 0026
    /// introduced; everything else eligible to reach here (<see cref="FailureClassification.Retryable"/>
    /// or absent, per <see cref="Aer.Flow.Scheduling.RetryEngine.MayRetry"/>) is an ordinary
    /// backoff.
    /// </summary>
    private static string FormatParkedStatus(StepState step)
    {
        var classification = step.LatestFailureClassification == FailureClassification.ExhaustedUntil
            ? "vendor quota"
            : "retryable";
        var localRetryTime = step.RetryNotBefore!.Value.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        return $"parked ({classification}) — retries {localRetryTime}";
    }

    /// <summary>
    /// <c>flow.jsonl</c>'s own last-write time (UTC), append-only so this is exactly "when the
    /// last event landed" — the closest honest answer available. Per-step timestamps are sourced
    /// from <see cref="LogEntry.WriterUtcTimestamp"/> instead, which stamps each envelope at write
    /// time (#745). Printed once here at the whole-log grain, per-step times are rendered in
    /// <c>PrintState</c> via <c>ExtractEventTimestamps</c>.
    /// </summary>
    private static string ResolveLogUpdatedAt(string logPath) => File.Exists(logPath)
        ? File.GetLastWriteTimeUtc(logPath).ToString("O")
        : "never (no ledger yet)";
}

