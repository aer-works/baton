using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.RoomSession;

/// <summary>
/// A room directory's projected state, reconstructed purely from durable data (UI spec §3, §11):
/// the bound <see cref="WorkflowDefinitionSnapshot"/> a room is permanently attached to, the
/// <see cref="FlowState"/> that snapshot and its Flow Event Store project to, and the fuller
/// <see cref="ExecutionHistory"/> that state alone doesn't carry (M14 Phase 2, issue #119). The
/// Snapshot/State pairing is deliberately the same one <c>Aer.Cli.CommandResult</c> uses on the
/// write side, for the same reason — a paused step's declared <c>PausePoint.SupersedeTargets</c> is
/// only resolvable against the snapshot, never <see cref="FlowState"/> alone — but owned by the
/// presentation layer (<c>Aer.Ui.Core</c> originally, this project since #1412 archived it) rather
/// than shared with <c>Aer.Cli</c>, since the UI is architecturally outside the trusted execution
/// stack (UI spec §2) and must not depend on it.
/// </summary>
/// <param name="Lineage">
/// Every execution's artifact-directory contents and resolved input provenance (M14 Phase 4, issue
/// #121) — a fourth read-model surface alongside <see cref="Snapshot"/>/<see cref="State"/>/
/// <see cref="History"/>, following the same "derived from the same events, owned by the
/// presentation layer" shape <see cref="History"/> established (Phase 2).
/// </param>
/// <param name="IsWorkflowOff">
/// The UI-side carrier of <see cref="Aer.Flow.Projection.RoomState.IsWorkflowOff"/> (#1216), which
/// defines it — projected from <c>room.jsonl</c> alongside <paramref name="PermissionAnswers"/>. A
/// plain bool rather than a transitions list (unlike <paramref name="DormancyTransitions"/>, whose
/// history is shown to the person): the switch is a "non-event" and nothing reads its past.
/// </param>
/// <param name="Participants">
/// 0054 §7/#1307: the room's participants as the projection's own live render source — the wire
/// twin of <c>SessionMetadata.Participants</c>, which stays the persistence truth. Populated
/// additively by <c>DaemonBroadcast.SendStateAsync</c>, the same site and pattern that already
/// stamps <c>SessionId</c> onto a push (it loads <c>SessionMetadata</c> in scope for exactly that).
/// Empty until a room is open, or on a pre-#1305 room.
/// </param>
/// <param name="Files">
/// The room's files as one versioned, attributed list (0021 §2; #1340) — a sixth read-model
/// surface, re-grouping the same facts <paramref name="Lineage"/> already carries by name instead of
/// by execution. Defaulted for the same source-compatibility reason as the other optional surfaces
/// above; an empty <see cref="RoomFiles"/> for any construction site that hasn't populated it.
/// </param>
public sealed record RoomProjection(
    WorkflowDefinitionSnapshot Snapshot, FlowState State, ExecutionHistory History, ArtifactLineage Lineage,
    IReadOnlyList<PermissionAnswer>? PermissionAnswers = null,
    IReadOnlyList<DormancyTransition>? DormancyTransitions = null,
    IReadOnlyList<StepPauseMoment>? StepPauseMoments = null,
    IReadOnlyList<RecordedDecisionMoment>? RecordedDecisionMoments = null,
    bool IsWorkflowOff = false,
    IReadOnlyList<Participant>? Participants = null,
    RoomFiles? Files = null)
{
    public IReadOnlyList<PermissionAnswer> PermissionAnswers { get; init; } = PermissionAnswers ?? [];
    public IReadOnlyList<DormancyTransition> DormancyTransitions { get; init; } = DormancyTransitions ?? [];
    public IReadOnlyList<StepPauseMoment> StepPauseMoments { get; init; } = StepPauseMoments ?? [];
    public IReadOnlyList<RecordedDecisionMoment> RecordedDecisionMoments { get; init; } = RecordedDecisionMoments ?? [];
    public IReadOnlyList<Participant> Participants { get; init; } = Participants ?? [];
    public RoomFiles Files { get; init; } = Files ?? new RoomFiles([]);
    public bool IsDormant => DormancyTransitions.Count > 0 && DormancyTransitions[^1].IsEntered;
}
