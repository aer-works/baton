using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Templates;

namespace Baton.Store;

/// <summary>
/// The read-only, at-READ-time probe of a held-work workflow directory feeding
/// <see cref="RoomWakeDerivation"/> (#799). Mirrors <c>Baton.Cli.StatusCommand</c>'s own
/// snapshot.json + flow.jsonl read exactly — same files, same
/// <see cref="StateProjector.Project(System.Collections.Generic.IReadOnlyList{FlowEvent},WorkflowDefinitionSnapshot,ProjectionCheckpoint)"/>
/// terminal authority (post-#811 <c>DeriveWorkflowStatus</c>) — never a second reading of what
/// "terminal" means. Uses projection checkpoints (#903 Scope 1) when present for bounded O(tail) replay.
/// #1157 made it the terminal <i>instant</i> authority too (<see cref="WorkflowProbeResult.TerminalAtUtc"/>),
/// for the same reason: a second reader deciding for itself when a run ended is how the mtime proxy
/// got there in the first place.
/// Takes no <see cref="Baton.Concurrency.ConcurrencyGuard"/>: this can run
/// concurrently with the workflow's own live pump, the same read-only discipline <c>baton status</c>
/// already established.
/// </summary>
public static class WorkflowTerminalProbe
{
    public static async Task<WorkflowProbeResult> ProbeAsync(
        string workflowDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowDirectoryPath);

        var logPath = Path.Combine(workflowDirectoryPath, BatonPaths.FlowLogFileName);
        if (!File.Exists(logPath))
        {
            return new WorkflowProbeResult(JournalExists: false, IsTerminal: false);
        }

        var snapshotPath = Path.Combine(workflowDirectoryPath, BatonPaths.SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            return new WorkflowProbeResult(JournalExists: true, IsTerminal: false);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);

        // #1157: entries, not bare events -- the terminal instant lives on the envelope
        // (LogEntry.FlowLogEntry.WriterUtcTimestamp), so reading events alone throws away the very
        // field this probe now reports. Same single read, one projection input extracted from it.
        var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<FlowEvent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowEntry)
            {
                events.Add(flowEntry.Event);
            }
        }

        var checkpoint = ProjectionCheckpointStore.Load(workflowDirectoryPath);
        var state = StateProjector.Project(events, snapshot, checkpoint);
        var isTerminal = state.Status == WorkflowStatus.Terminal;

        // Gated on isTerminal rather than left to the resolver's own gate, so the "never invent an
        // instant for a room that has not ended" rule is visible at the field's construction site and
        // not only inside the thing it calls. Note the projections can in principle disagree -- this one
        // uses the room's ProjectionCheckpoint, the resolver's is a checkpoint-free full replay -- but
        // they cannot in practice: flow.jsonl is append-only (nothing in src/ rewrites or truncates it;
        // RoomJournalCompactor compacts room.jsonl), so a checkpoint is always a valid prefix fold of
        // the same events, and StateProjector already forces a loud full replay when its EventOffset
        // exceeds the log. If that ever stops holding, this reads NoTransitionEntry rather than
        // TransitionEntryUnstamped, so it cannot masquerade as a legacy journal.
        var instant = isTerminal
            ? TerminalInstantResolver.Resolve(entries, snapshot)
            : new TerminalInstant(null, TerminalInstantAbsence.NotTerminal);

        return new WorkflowProbeResult(
            JournalExists: true,
            IsTerminal: isTerminal,
            TerminalAtUtc: instant.AtUtc,
            TerminalAtAbsence: instant.Absence);
    }
}
