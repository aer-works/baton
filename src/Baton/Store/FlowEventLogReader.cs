using System.Text;
using System.Text.Json;
using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// Reads the combined <c>flow.jsonl</c> back into ordered event lists:
/// <see cref="ReadAllAsync"/> for Flow's own half, which the State Projector consumes,
/// <see cref="ReadAllCoreEventsAsync"/> for the Core Dispatcher's half (M7 Phase 6), which M10
/// Phase 3's crash reconciliation reads back for the causal link, <see cref="ReadSnapshotAsync"/>
/// for a caller needing both from a single read pass, and <see cref="ReadAllEntriesWithTimestampsAsync"/>
/// for callers that need entries with their writer-stamped timestamps (#745) — used by status
/// reporting to display per-step times. Pairs with <see cref="FlowEventLogWriter"/>, which guarantees
/// each entry is a single, complete, newline-terminated line.
/// </summary>
public sealed class FlowEventLogReader(string logFilePath) : IEventLogReader
{
    public async Task<IReadOnlyList<FlowEvent>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<FlowEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowLogEntry)
            {
                events.Add(flowLogEntry.Event);
            }
        }

        return events;
    }

    public async Task<IReadOnlyList<CoreEvent>> ReadAllCoreEventsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<CoreEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.CoreLogEntry coreLogEntry)
            {
                events.Add(coreLogEntry.Event);
            }
        }

        return events;
    }

    public async Task<EventLogSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await ReadSnapshotFromOffsetAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventLogSnapshot> ReadSnapshotFromOffsetAsync(long seekByteOffset, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(logFilePath))
        {
            return new EventLogSnapshot([], [], 0);
        }

        if (seekByteOffset <= 0)
        {
            return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var stream = OpenReadStream(logFilePath);
            var fileLength = stream.Length;

            if (seekByteOffset > fileLength)
            {
                Console.Error.WriteLine(
                    $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint ByteOffset ({seekByteOffset}) exceeds log length ({fileLength}).");
                return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            // Boundary validation: the byte at seekByteOffset - 1 must be '\n'. This runs BEFORE
            // the caught-up early return below, not only on the read-a-tail path: "checkpoint equals
            // log length, nothing appended since" is the most common call shape of all, and it was
            // the one branch that trusted an unvalidated offset outright (#971's second reader).
            stream.Seek(seekByteOffset - 1, SeekOrigin.Begin);
            int prevByte = stream.ReadByte();
            if (prevByte != '\n')
            {
                Console.Error.WriteLine(
                    $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint ByteOffset ({seekByteOffset}) does not land on a record boundary (previous byte 0x{prevByte:X2} != '\\n').");
                return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            if (seekByteOffset == fileLength)
            {
                return new EventLogSnapshot([], [], fileLength);
            }

            // Seek to tail start
            stream.Seek(seekByteOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            // Only lines terminated by '\n' are complete entries; a dangling suffix with no
            // terminator is a write still in flight (or a crash mid-append) and is not yet observable.
            var lastNewline = text.LastIndexOf('\n');
            var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
            var completeByteCount = Encoding.UTF8.GetByteCount(completeText);
            var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var flowEvents = new List<FlowEvent>(lines.Length);
            var coreEvents = new List<CoreEvent>(lines.Length);

            foreach (var line in lines)
            {
                LogEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options);
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine(
                        $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Mid-line corruption or unparseable line at seek target: {ex.Message}");
                    return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
                }

                if (entry is null)
                {
                    Console.Error.WriteLine(
                        "[ProjectionCheckpoint] Fallback to full replay LOUDLY: Line at seek target deserialized to null.");
                    return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
                }

                switch (entry)
                {
                    case LogEntry.FlowLogEntry flowLogEntry:
                        flowEvents.Add(flowLogEntry.Event);
                        break;
                    case LogEntry.CoreLogEntry coreLogEntry:
                        coreEvents.Add(coreLogEntry.Event);
                        break;
                }
            }

            return new EventLogSnapshot(flowEvents, coreEvents, seekByteOffset + completeByteCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FlowJournalHeldException)
        {
            // A cancellation is the caller's request, never a corrupt checkpoint — swallowing it
            // into a full replay would turn "stop now" into the most expensive read in the file.
            // Everything else (sharing violation, truncation under the seek, encoding surprise) is
            // exactly what the loud full-replay fallback exists for.
            Console.Error.WriteLine(
                $"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Exception during seek-to-tail read: {ex.Message}");
            return await ReadFullSnapshotInternalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads all log entries, including their writer-stamped timestamps. Used by status reporting
    /// to display per-step times derived from event log timestamps.
    /// </summary>
    public async Task<IReadOnlyList<LogEntry>> ReadAllEntriesWithTimestampsAsync(CancellationToken cancellationToken = default)
    {
        return await ReadAllEntriesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LogEntry>> ReadAllEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(logFilePath))
        {
            return [];
        }

        string text;
        await using (var stream = OpenReadStream(logFilePath))
        using (var reader = new StreamReader(stream))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var lastNewline = text.LastIndexOf('\n');
        var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var result = new List<LogEntry>(lines.Length);
        foreach (var line in lines)
        {
            LogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options);
            }
            catch (JsonException ex)
            {
                throw new FlowEventLogReadException($"Malformed line in the ledger: {line}", ex);
            }

            if (entry is null)
            {
                throw new FlowEventLogReadException($"Line in the ledger deserialized to null: {line}");
            }

            result.Add(entry);
        }

        return result;
    }

    private async Task<EventLogSnapshot> ReadFullSnapshotInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(logFilePath))
        {
            return new EventLogSnapshot([], [], 0, IsFallbackToFull: true);
        }

        string text;
        await using (var stream = OpenReadStream(logFilePath))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var lastNewline = text.LastIndexOf('\n');
        var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
        var completeByteCount = Encoding.UTF8.GetByteCount(completeText);
        var lines = completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var flowEvents = new List<FlowEvent>(lines.Length);
        var coreEvents = new List<CoreEvent>(lines.Length);

        foreach (var line in lines)
        {
            LogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options);
            }
            catch (JsonException ex)
            {
                throw new FlowEventLogReadException($"Malformed line in the ledger: {line}", ex);
            }

            if (entry is null)
            {
                throw new FlowEventLogReadException($"Line in the ledger deserialized to null: {line}");
            }

            switch (entry)
            {
                case LogEntry.FlowLogEntry flowLogEntry:
                    flowEvents.Add(flowLogEntry.Event);
                    break;
                case LogEntry.CoreLogEntry coreLogEntry:
                    coreEvents.Add(coreLogEntry.Event);
                    break;
            }
        }

        return new EventLogSnapshot(flowEvents, coreEvents, completeByteCount, IsFallbackToFull: true);
    }

    private static FileStream OpenReadStream(string logFilePath)
    {
        try
        {
            return new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException ex) when (FileHolderProbe.IsSharingViolation(ex))
        {
            // Name the holder while it is still held (the probe runs here, in-process, not in a
            // post-hoc step where a transient holder would already be gone). This turns the #398
            // Windows-CI flake from "used by another process, holder unknown" into a named culprit.
            throw new FlowJournalHeldException(
                $"'{logFilePath}' is held open by another process — usually this room's live " +
                "'baton run' engine, which keeps the ledger open for its whole run, though any " +
                "sibling baton command mid-append briefly holds it too. Retry once nothing else " +
                "holds the ledger; for a decision, the workflow's latest attempt must be Paused " +
                $"with no live 'baton run' (see 'baton status'). Current holder: {FileHolderProbe.DescribeHolders(logFilePath)}",
                ex);
        }
    }
}
