using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.Ui.Core;

/// <summary>
/// A room directory's projected state, reconstructed purely from durable data (UI spec §3, §11):
/// the bound <see cref="WorkflowDefinitionSnapshot"/> a room is permanently attached to, the
/// <see cref="FlowState"/> that snapshot and its Flow Event Store project to, and the fuller
/// <see cref="ExecutionHistory"/> that state alone doesn't carry (M14 Phase 2, issue #119). The
/// Snapshot/State pairing is deliberately the same one <c>Aer.Cli.CommandResult</c> uses on the
/// write side, for the same reason — a paused step's declared <c>PausePoint.SupersedeTargets</c> is
/// only resolvable against the snapshot, never <see cref="FlowState"/> alone — but owned by
/// <c>Aer.Ui</c> rather than shared with <c>Aer.Cli</c>, since the UI is architecturally outside the
/// trusted execution stack (UI spec §2) and must not depend on it.
/// </summary>
/// <param name="Lineage">
/// Every execution's artifact-directory contents and resolved input provenance (M14 Phase 4, issue
/// #121) — a fourth read-model surface alongside <see cref="Snapshot"/>/<see cref="State"/>/
/// <see cref="History"/>, following the same "derived from the same events, owned by <c>Aer.Ui</c>"
/// shape <see cref="History"/> established (Phase 2).
/// </param>
/// <param name="PendingPermission">
/// The runtime conversational gate a worker is currently blocked on (#445/#390), projected from the
/// room's <c>room.jsonl</c> journal (a DIFFERENT event store from the <see cref="State"/>'s
/// <c>flow.jsonl</c>) via <see cref="RoomProjector"/>. <see langword="null"/> when no gate is open —
/// the common case. A fifth read-model surface, defaulted so the many existing construction sites
/// (e.g. the fleet-status loader) that carry no gate stay source-compatible. This is the one field
/// that lets the daemon's projection push carry a pending permission to a screen: without it the
/// mid-turn ask is journaled but never rendered (<see cref="RoomProjectionLoader.LoadAsync"/> reads
/// only <c>flow.jsonl</c> otherwise).
/// </param>
public sealed record RoomProjection(
    WorkflowDefinitionSnapshot Snapshot, FlowState State, ExecutionHistory History, ArtifactLineage Lineage,
    PendingPermission? PendingPermission = null);
