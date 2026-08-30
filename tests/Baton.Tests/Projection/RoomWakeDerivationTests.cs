using Baton.Domain;
using Baton.Projection;

namespace Baton.Tests.Projection;

public class RoomWakeDerivationTests
{
    private static readonly HeldWorkRef LaneRefA = new("lanes/lane-a");
    private static readonly HeldWorkRef LaneRefB = new("lanes/lane-b");
    private const string CitedSubject = "exec-lane-a";

    [Fact]
    public void Dispatched_ref_with_terminal_workflow_wakes_as_DispatchedWorkflowTerminated()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: true, IsTerminal: true) });

        var wake = Assert.Single(wakes);
        Assert.Equal(LaneRefA, wake.Ref);
        Assert.Equal(RoomWakeKind.DispatchedWorkflowTerminated, wake.Kind);
    }

    [Fact]
    public void Escalated_ref_with_terminal_workflow_wakes_as_EscalatedWorkflowTerminated()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefA, "supervisor-bob"),
        ]);

        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: true, IsTerminal: true) });

        var wake = Assert.Single(wakes);
        Assert.Equal(RoomWakeKind.EscalatedWorkflowTerminated, wake.Kind);
    }

    [Fact]
    public void Dispatched_ref_with_no_lane_journal_wakes_as_DispatchOrphaned_the_774_pattern()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: false, IsTerminal: false) });

        var wake = Assert.Single(wakes);
        Assert.Equal(RoomWakeKind.DispatchOrphaned, wake.Kind);
    }

    [Fact]
    public void Dispatched_memory_proposal_never_wakes_even_with_a_journalless_probe_handed_in()
    {
        var proposalRef = new HeldWorkRef("captures/proposal-abc.json");
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                proposalRef, Baton.Mutation.MemoryProposalEscalation.MemoryProposalShape, TimeSpan.Zero, "decider-1"),
        ]);

        // The probe is handed in deliberately: the derivation's own guard must hold even when a
        // caller probes a capture-file ref anyway -- "no journal" is this shape's normal pending
        // state, and pre-#832 this exact input derived a spurious DispatchOrphaned.
        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [proposalRef] = new(JournalExists: false, IsTerminal: false) });

        Assert.Empty(wakes);
    }

    [Fact]
    public void Dispatched_ref_with_running_lane_produces_no_wake()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: true, IsTerminal: false) });

        Assert.Empty(wakes);
    }

    [Fact]
    public void Polarity_arm_1_unresolved_terminal_ref_wakes()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: true, IsTerminal: true) });

        Assert.Single(wakes);
    }

    [Fact]
    public void Polarity_arm_2_resolving_the_ref_clears_the_wake_even_though_the_lane_is_still_terminal()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkResolved(LaneRefA, new HeldWorkCitation(CitedSubject, "executionSucceeded", 2)),
        ]);

        // Same lane probe result as the terminal-wakes test above -- only the room's own mutation
        // (resolving the ref) changed. This is consuming the wake IS the room acting on it: no
        // daemon-side ack, no separate "clear" operation.
        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: true, IsTerminal: true) });

        Assert.Empty(wakes);
    }

    [Fact]
    public void Multiple_refs_each_derive_independently()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkDispatched(LaneRefB, "shape-2", TimeSpan.FromMinutes(5), "decider-2"),
            new RoomEvent.HeldWorkEscalated(LaneRefB, "supervisor-bob"),
        ]);

        var wakes = RoomWakeDerivation.DeriveWakes(
            state,
            new Dictionary<HeldWorkRef, WorkflowProbeResult>
            {
                [LaneRefA] = new(JournalExists: false, IsTerminal: false),
                [LaneRefB] = new(JournalExists: true, IsTerminal: true),
            });

        Assert.Equal(2, wakes.Count);
        Assert.Contains(wakes, w => w.Ref == LaneRefA && w.Kind == RoomWakeKind.DispatchOrphaned);
        Assert.Contains(wakes, w => w.Ref == LaneRefB && w.Kind == RoomWakeKind.EscalatedWorkflowTerminated);
    }

    [Fact]
    public void Recompute_is_deterministic_identical_inputs_produce_an_identical_set()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);
        var probes = new Dictionary<HeldWorkRef, WorkflowProbeResult> { [LaneRefA] = new(JournalExists: true, IsTerminal: true) };

        var wakes1 = RoomWakeDerivation.DeriveWakes(state, probes);
        var wakes2 = RoomWakeDerivation.DeriveWakes(state, probes);

        // Sorted before comparing: dictionary enumeration order is not part of its contract, and
        // this claim is about set identity, not iteration order (the integration test's
        // recompute-after-restart assertion sorts for the same reason).
        Assert.Equal(wakes1.OrderBy(w => w.Ref.Value), wakes2.OrderBy(w => w.Ref.Value));
    }
}
