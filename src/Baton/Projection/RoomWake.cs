using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// The three named ways an unresolved held-work reference asks its holding room for an
/// orchestrator turn (#799). <see cref="DispatchedWorkflowTerminated"/> and
/// <see cref="EscalatedWorkflowTerminated"/> are the design comment's single "unresolved ref whose
/// workflow shows a terminal event" clause, split by <see cref="HeldWorkStatus"/> because the two are
/// different orchestrator situations (a first wake vs. a wake on work already escalated) —
/// <see cref="HeldWorkReconciler.RenderStatus"/> already renders them differently for the same
/// reason. <see cref="DispatchOrphaned"/> is the #774 pattern: dispatch recorded, workflow journal
/// never appeared.
/// </summary>
public enum RoomWakeKind
{
    DispatchedWorkflowTerminated,
    EscalatedWorkflowTerminated,
    DispatchOrphaned,
}

/// <summary>A single derived wake: one unresolved held-work ref, one reason. Never persisted.</summary>
public sealed record RoomWake(HeldWorkRef Ref, RoomWakeKind Kind);

/// <summary>
/// The read-only probe of a single workflow directory the derivation needs: whether its
/// <c>flow.jsonl</c> exists at all (the #774 orphan check), and — only meaningful when it does —
/// whether its projected <see cref="WorkflowStatus"/> is <see cref="WorkflowStatus.Terminal"/>.
/// </summary>
public readonly record struct WorkflowProbeResult(bool JournalExists, bool IsTerminal);

/// <summary>
/// Wake set = f(<see cref="RoomState"/>, workflow probes) — pure, synchronous, no wall-clock, no I/O.
/// The design's "the bridge derives wakes, it never stores them": call this fresh every tick
/// against freshly-read state and it reproduces the identical set, restart or not.
/// </summary>
public static class RoomWakeDerivation
{
    /// <summary>
    /// Derives every wake in <paramref name="roomState"/> given <paramref name="workflowProbes"/> — a
    /// probe result per <see cref="HeldWorkRef"/> the caller chose to watch. A ref with no entry
    /// in <paramref name="workflowProbes"/> is treated as not (yet) probed and produces no wake, never
    /// a default orphan — the caller decides what "watched" means (today: every unresolved ref).
    /// </summary>
    public static IReadOnlyList<RoomWake> DeriveWakes(
        RoomState roomState,
        IReadOnlyDictionary<HeldWorkRef, WorkflowProbeResult> workflowProbes)
    {
        ArgumentNullException.ThrowIfNull(roomState);
        ArgumentNullException.ThrowIfNull(workflowProbes);

        var wakes = new List<RoomWake>();

        foreach (var (@ref, state) in roomState.HeldWork)
        {
            // Resolving the ref IS what clears the wake — a Resolved ref is never a wake source,
            // regardless of what its workflow journal now shows.
            if (state.Status == HeldWorkStatus.Resolved)
            {
                continue;
            }

            // A memory proposal refs its capture file (#801; the defect record is
            // RoomProjectorTests' #832 note) — every workflow-probe-derived wake kind is meaningless
            // for it: "no journal" is its normal pending state, not an orphaned dispatch.
            // Waiting on an operator decision is
            // what this shape DOES; it never wakes anyone from here. Guarded in the derivation,
            // not only the probe loop, so a caller handing in a probe for one of these refs
            // cannot resurrect the spurious orphan.
            if (state.Shape == Mutation.MemoryProposalEscalation.MemoryProposalShape)
            {
                continue;
            }

            if (!workflowProbes.TryGetValue(@ref, out var probe))
            {
                continue;
            }

            if (!probe.JournalExists)
            {
                wakes.Add(new RoomWake(@ref, RoomWakeKind.DispatchOrphaned));
                continue;
            }

            if (probe.IsTerminal)
            {
                wakes.Add(new RoomWake(
                    @ref,
                    state.Status == HeldWorkStatus.Escalated
                        ? RoomWakeKind.EscalatedWorkflowTerminated
                        : RoomWakeKind.DispatchedWorkflowTerminated));
            }
        }

        return wakes;
    }
}
