using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Ui.Core;

/// <summary>
/// One room directory's lightweight status (M24 Phase 5, #278's fleet list) — friendly
/// name, a template id or "interactive session" label, a plain status line, paused-step count,
/// archived state, and the creation/last-updated timestamps (#322) that let a client sort by
/// recency and render relative times ("2h ago"). Deliberately not a <see cref="RoomProjection"/>:
/// a fleet list showing every known room at once can't afford
/// <see cref="RoomProjectionLoader.LoadAsync"/>'s full per-execution history/artifact-lineage
/// projection cost for every item.
/// </summary>
/// <param name="Created">When this room was first created (UTC).</param>
/// <param name="Updated">When this room last changed (UTC) — the key the fleet list orders by.</param>
/// <param name="IsSession">
/// Whether this directory is an interactive session (chat-shaped) rather than a workflow (DAG-shaped)
/// — the structural fact behind <paramref name="TypeLabel"/>, surfaced separately because #336's
/// switcher routes the detail pane on it. <paramref name="TypeLabel"/> is a *display* string
/// ("interactive session", or a workflow's template id), so routing on it would mean string-matching
/// a rendered label; this is the same fact the loader already computes to build that label.
/// </param>
/// <param name="LastActivityAt">
/// Timestamp of the newest event in the room's journal (#640) — used for sorting fleet items by recency of actual activity.
/// </param>
public sealed record RoomFleetItem(
    string RoomDirectoryPath,
    string FriendlyName,
    string TypeLabel,
    string StatusText,
    int PausedStepCount,
    bool IsArchived,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    bool IsSession = false,
    DateTimeOffset? LastActivityAt = null,
    string? SessionId = null,
    RoomCardStatus? Status = null);

/// <summary>
/// The seam this phase exists to prove (issue #118): opens a real room directory using exactly
/// the read-model library calls Flow's own write path uses — <see cref="SnapshotBinder.LoadFromFileAsync"/>
/// for the bound snapshot (AER Flow spec §11.2), <see cref="FlowEventLogReader"/> for the Flow
/// Event Store (§5.1), and <see cref="StateProjector.Project"/> to reconstruct <see cref="Aer.Flow.Domain.FlowState"/>
/// (§12) — never a reimplementation of any of it. <see cref="ExecutionHistoryProjector.Project"/>
/// (M14 Phase 2, issue #119) reads the same event list a second time for the fuller per-execution
/// history <see cref="Aer.Flow.Domain.FlowState"/> alone doesn't carry, and
/// <see cref="ArtifactLineageProjector.Project"/> (M14 Phase 4, issue #121) reads it a third time,
/// plus the artifacts directory, for per-execution artifact provenance. A UI built this way inherits
/// §11's determinism guarantee by construction, per UI spec §11.
/// </summary>
public static class RoomProjectionLoader
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
    private const string ArtifactsDirectoryName = Aer.Flow.Artifacts.ArtifactManager.ArtifactsDirectoryName;

    /// <summary>
    /// A room's display name: the leaf of its directory path. The one canonical derivation — every
    /// surface that names a room calls this: the switcher (through <see cref="LoadFleetStatusAsync"/>),
    /// its needs-you filter's inline items (through <c>RoomCardViewModel.TitleFor</c>), and the desktop chat header — so a
    /// room can never show two different names. Re-deriving it independently on a second surface is the
    /// exact "same fact, its own vocabulary" trap that put Cancelled back to "Finished" (#461/#976).
    /// </summary>
    public static string FriendlyNameFor(string roomDirectoryPath) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectoryPath));

    /// <exception cref="InvalidRoomDirectoryException">
    /// <paramref name="roomDirectoryPath"/> has no persisted snapshot — UI spec §3.1's
    /// self-describing-directory contract confirmed by contents, not assumed from a path.
    /// </exception>
    /// <exception cref="SnapshotLoadException">The persisted snapshot is malformed.</exception>
    /// <exception cref="FlowEventLogReadException">The persisted Flow Event Store is malformed.</exception>
    public static async Task<RoomProjection> LoadAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roomDirectoryPath);

        var snapshotPath = Path.Combine(roomDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            throw new InvalidRoomDirectoryException(
                $"Not a room directory (no '{SnapshotFileName}' found): '{roomDirectoryPath}'");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var logPath = Path.Combine(roomDirectoryPath, LogFileName);
        var reader = new FlowEventLogReader(logPath);
        var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<FlowEvent>(entries.Count);
        var stepPauseMoments = new List<StepPauseMoment>();
        var recordedDecisionMoments = new List<RecordedDecisionMoment>();

        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowLogEntry)
            {
                events.Add(flowLogEntry.Event);
                DateTimeOffset? timestamp = flowLogEntry.WriterUtcTimestamp.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(flowLogEntry.WriterUtcTimestamp.Value, DateTimeKind.Utc))
                    : null;

                switch (flowLogEntry.Event)
                {
                    case FlowEvent.WorkflowPaused paused:
                        stepPauseMoments.Add(new StepPauseMoment(paused.ExecutionId, paused.StepId, timestamp));
                        break;
                    case FlowEvent.ExternalDecisionRecorded decision:
                        recordedDecisionMoments.Add(new RecordedDecisionMoment(
                            decision.DecisionId,
                            decision.ReferencedExecutionId,
                            decision.DecisionType,
                            decision.TargetStepId,
                            decision.SupplementaryExecutionId,
                            decision.EffectiveDecider,
                            timestamp));
                        break;
                }
            }
        }

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var state = StateProjector.Project(events, snapshot, checkpoint);
        var history = ExecutionHistoryProjector.Project(events, snapshot);

        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactsDirectoryName);
        var lineage = ArtifactLineageProjector.Project(events, snapshot, artifactsRootPath);

        var (pendingPermission, permissionAnswers, dormancyTransitions) =
            await LoadJournalStateAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);

        return new RoomProjection(
            snapshot, state, history, lineage,
            pendingPermission, permissionAnswers, dormancyTransitions,
            stepPauseMoments, recordedDecisionMoments);
    }

    /// <summary>
    /// Projects the room's <c>room.jsonl</c> journal — a SEPARATE event store from the
    /// <c>flow.jsonl</c> the rest of <see cref="LoadAsync"/> reads (#445/#390) — for the one gate a
    /// worker may currently be blocked on, the answer history (#1142), and dormancy transitions (#1178).
    /// Most rooms never write a RoomEvent, so the file is usually absent; absence is an empty projection,
    /// never a throw. The room-journal filename is the literal <c>Aer.Daemon</c> writes and reads
    /// it under (there is no shared constant to cite yet).
    /// </summary>
    private static async Task<(PendingPermission? Pending, IReadOnlyList<PermissionAnswer> Answers, IReadOnlyList<DormancyTransition> Transitions)> LoadJournalStateAsync(
        string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var roomLogPath = Path.Combine(roomDirectoryPath, "room.jsonl");
        if (!File.Exists(roomLogPath))
        {
            return (null, [], []);
        }

        var reader = new RoomEventLogReader(roomLogPath);
        var roomEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var projected = RoomProjector.Project(roomEvents);
        return (projected.PendingPermission, projected.PermissionAnswers, projected.DormancyTransitions);
    }

    /// <summary>
    /// The fleet list's per-item load (M24 Phase 5, #278): skips <see cref="ExecutionHistoryProjector"/>
    /// and <see cref="ArtifactLineageProjector"/> entirely (the latter does real per-execution
    /// artifact-directory <see cref="File"/> I/O — the actual expensive part) and reads only
    /// <see cref="StateProjector.Project"/>'s status/paused-step count. The <see cref="FlowEventLogReader"/>
    /// read itself still happens — that's unavoidable for a correct status — this only skips the
    /// two additional, more expensive re-folds of the same event list. #1112 added one more read:
    /// <see cref="LoadJournalStateAsync"/> (a <c>room.jsonl</c> existence check plus, when
    /// present, one full read of that journal), so a live permission ask can rank as NeedsYou here —
    /// the same per-room cost the full <see cref="LoadAsync"/> path already pays for the same field.
    /// </summary>
    public static async Task<RoomFleetItem> LoadFleetStatusAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roomDirectoryPath);

        var friendlyName = FriendlyNameFor(roomDirectoryPath);
        var isArchived = RoomLifecycle.IsArchived(roomDirectoryPath);
        var isSession = await InteractiveSessionMaterializer.ReadRoomKindAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false) == RoomKind.Interactive;
        // The session id a phone row taps into (row-as-place, #1044), read from the same .aer/room.json
        // marker via the canonical LoadMetadataAsync — the idiom DaemonBroadcast's SessionId sibling uses
        // (#262). Null for a workflow room.
        var sessionId = isSession
            ? (await InteractiveSessionMaterializer.LoadMetadataAsync(
                Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName), cancellationToken).ConfigureAwait(false))?.SessionId
            : null;
        var (created, updated) = await ResolveTimestampsAsync(roomDirectoryPath, isSession, cancellationToken).ConfigureAwait(false);

        var snapshotPath = Path.Combine(roomDirectoryPath, SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            // A materialized interactive session with no initial message never actually runs (a
            // known quirk, not an error -- see DaemonIntegrationTests' WebSocketSnapshot_* remarks).
            // A DAG room directory with no snapshot yet shouldn't exist by construction, but is
            // represented the same defensive way rather than thrown on.
            // For a room with no journal/snapshot yet, LastActivityAt falls back to created timestamp
            // (scoped strictly to the pre-first-event window).
            return new RoomFleetItem(
                roomDirectoryPath, friendlyName, isSession ? "interactive session" : "workflow", // vocabulary-ok: internal type label
                isSession ? "Not yet run" : "Not yet run", PausedStepCount: 0, isArchived, created, updated,
                isSession, LastActivityAt: created, SessionId: sessionId);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var typeLabel = isSession ? "interactive session" : snapshot.WorkflowTemplateId.Value; // vocabulary-ok: internal type label

        var logPath = Path.Combine(roomDirectoryPath, LogFileName);
        var reader = new FlowEventLogReader(logPath);
        var entries = await reader.ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);

        var events = new List<FlowEvent>(entries.Count);
        DateTimeOffset? newestEventTimestamp = null;

        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowLogEntry)
            {
                events.Add(flowLogEntry.Event);
            }

            var ts = entry switch
            {
                LogEntry.FlowLogEntry f => f.WriterUtcTimestamp,
                LogEntry.CoreLogEntry c => c.WriterUtcTimestamp,
                LogEntry.RoomLogEntry r => r.WriterUtcTimestamp,
                _ => null
            };

            if (ts.HasValue)
            {
                var dto = new DateTimeOffset(DateTime.SpecifyKind(ts.Value, DateTimeKind.Utc));
                if (!newestEventTimestamp.HasValue || dto > newestEventTimestamp.Value)
                {
                    newestEventTimestamp = dto;
                }
            }
        }

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var state = StateProjector.Project(events, snapshot, checkpoint);
        var pausedStepCount = state.Steps.Count(s => s.Status == StepStatus.Paused);
        var (pendingPermission, _, _) = await LoadJournalStateAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);

        // Reuse the ONE status derivation (#616/#976 — never a second copy) so the fleet reads
        // "Waiting for your reply" for a chat turn and "Waiting for your review" for a real gate,
        // instead of raw WorkflowStatus. DeriveStatus reads only State + Snapshot; the empty
        // history/lineage here are the same minimal projection StatusDerivationTests builds.
        var (statusText, status) = RoomCardViewModel.DeriveStatus(new RoomProjection(
            snapshot, state,
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]),
            pendingPermission),
            pendingPermission);

        // Fallback for a room with no journal events/timestamps yet: prefer created timestamp
        // (scoped strictly to the pre-first-event window).
        var lastActivityAt = newestEventTimestamp ?? created;

        return new RoomFleetItem(roomDirectoryPath, friendlyName, typeLabel, statusText, pausedStepCount, isArchived, created, updated, isSession, lastActivityAt, sessionId, status);
    }

    /// <summary>
    /// The <c>created</c>/<c>updated</c> timestamps the fleet contract carries (#322), resolved from
    /// the best available source per entry type.
    /// <para>
    /// An interactive session's <c>.aer/room.json</c> carries serialized <see cref="SessionMetadata.CreatedAt"/>/
    /// <see cref="SessionMetadata.UpdatedAt"/> values -- a genuine durable in-data source (UpdatedAt
    /// is bumped on every turn by Aer.Daemon's turn executor), so it is preferred outright, and it is
    /// present even for a never-run session that has no snapshot yet.
    /// </para>
    /// <para>
    /// A DAG room's per-step timestamps are now carried in <see cref="LogEntry.WriterUtcTimestamp"/>
    /// on each envelope (#745). Workflow-level timestamps still resolve from filesystem times: <see cref="WorkflowDefinitionSnapshot"/>
    /// has no time field, so <c>snapshot.json</c>'s last-write time is a stable <c>created</c>, and <c>flow.jsonl</c>'s is
    /// the last-event-appended <c>updated</c>. Last-write time is used over creation time because birth time is unreliable on
    /// some Linux/CI filesystems whereas mtime always exists. The directory's own times are the last resort when neither file exists.
    /// </para>
    /// </summary>
    private static async Task<(DateTimeOffset Created, DateTimeOffset Updated)> ResolveTimestampsAsync(
        string roomDirectoryPath, bool isSession, CancellationToken cancellationToken)
    {
        if (isSession)
        {
            var sessionMetadataPath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(sessionMetadataPath, cancellationToken).ConfigureAwait(false);
            if (metadata is not null)
            {
                return (metadata.CreatedAt, metadata.UpdatedAt);
            }
        }

        var snapshotPath = Path.Combine(roomDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(roomDirectoryPath, LogFileName);

        var created = File.Exists(snapshotPath)
            ? File.GetLastWriteTimeUtc(snapshotPath)
            : Directory.GetCreationTimeUtc(roomDirectoryPath);

        var updated = File.Exists(logPath)
            ? File.GetLastWriteTimeUtc(logPath)
            : File.Exists(snapshotPath)
                ? File.GetLastWriteTimeUtc(snapshotPath)
                : Directory.GetLastWriteTimeUtc(roomDirectoryPath);

        return (new DateTimeOffset(created), new DateTimeOffset(updated));
    }
}
