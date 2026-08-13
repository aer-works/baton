using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;

namespace Aer.Flow.Tests.Projection;

public class RoomProjectorTests
{
    private static readonly HeldWorkRef LaneRefA = new("lanes/lane-a");
    private static readonly HeldWorkRef LaneRefB = new("lanes/lane-b");
    private const string CitedSubject = "exec-lane-a";

    [Fact]
    public void Projects_held_work_lifecycle_purely_and_deterministically()
    {
        var events = new List<RoomEvent>
        {
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefA, "supervisor-bob"),
            new RoomEvent.HeldWorkResolved(LaneRefA, new HeldWorkCitation(CitedSubject, "executionSucceeded", 2)),
            new RoomEvent.HeldWorkDispatched(LaneRefB, "shape-2", TimeSpan.FromMinutes(5), "decider-2"),
        };

        var state = RoomProjector.Project(events);

        Assert.Equal(2, state.HeldWork.Count);

        var itemA = state.HeldWork[LaneRefA];
        Assert.Equal(HeldWorkStatus.Resolved, itemA.Status);
        Assert.Equal("supervisor-bob", itemA.EscalatedTo);
        Assert.NotNull(itemA.Citation);
        Assert.Equal(CitedSubject, itemA.Citation.Subject);

        var itemB = state.HeldWork[LaneRefB];
        Assert.Equal(HeldWorkStatus.Dispatched, itemB.Status);
        Assert.Null(itemB.EscalatedTo);
        Assert.Null(itemB.Citation);
    }

    [Fact]
    public void Replay_determinism_room_projection_output_is_byte_identical_regardless_of_probe()
    {
        var events = new List<RoomEvent>
        {
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        };

        var state1 = RoomProjector.Project(events);
        var state2 = RoomProjector.Project(events);

        Assert.Equal(state1, state2);
        Assert.Equal(state1.HeldWork[LaneRefA], state2.HeldWork[LaneRefA]);
    }

    [Fact]
    public void Polarity_arm_1_ref_with_no_lane_journal_renders_loud_orphan_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1")
        ]);

        var item = state.HeldWork[LaneRefA];
        var rendered = HeldWorkReconciler.RenderStatus(item, workflowJournalExistsProbe: _ => false);

        Assert.Contains("dispatch recorded; workflow never started", rendered);
    }

    [Fact]
    public void An_escalation_and_resolution_for_an_unknown_ref_surface_as_unmatched_entries()
    {
        var events = new List<RoomEvent>
        {
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefB, "supervisor-bob"),
            new RoomEvent.HeldWorkResolved(LaneRefB, new HeldWorkCitation(CitedSubject, "executionSucceeded", 1)),
        };

        var state = RoomProjector.Project(events);

        // The tracked ref is untouched, and the orphans are named in append order -- the why
        // lives on RoomState.UnmatchedEntries' doc.
        Assert.Equal(HeldWorkStatus.Dispatched, state.HeldWork[LaneRefA].Status);
        Assert.Equal(2, state.UnmatchedEntries.Count);
        Assert.Contains("heldWorkEscalated", state.UnmatchedEntries[0]);
        Assert.Contains("lanes/lane-b", state.UnmatchedEntries[0]);
        Assert.Contains("heldWorkResolved", state.UnmatchedEntries[1]);
    }

    [Fact]
    public void A_journal_whose_every_entry_matches_has_no_unmatched_entries()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
            new RoomEvent.HeldWorkEscalated(LaneRefA, "supervisor-bob"),
        ]);

        Assert.Empty(state.UnmatchedEntries);
    }

    [Fact]
    public void Polarity_arm_2_lane_directory_with_no_ref_in_room_journal_is_invisible_to_room()
    {
        // Projection has only LaneRefA; non-referenced lane directory 'lanes/lane-b' is invisible
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1")
        ]);

        Assert.True(state.HeldWork.ContainsKey(LaneRefA));
        Assert.False(state.HeldWork.ContainsKey(LaneRefB));
    }

    // #832: a memory-proposal held-work item's Ref is a capture FILE, not a lane directory. Before
    // #832, RenderStatus had no shape concept and joined every ref against "<ref>/flow.jsonl" --
    // for a file ref that join can never exist, so every memory-proposal item rendered the loud
    // "lane never started" line regardless of whether its capture file was actually still there.

    private static readonly HeldWorkRef MemoryProposalRef = new("captures/proposal-1.json");

    [Fact]
    public void Reconciler_status_arm_lane_shaped_item_with_journal_renders_todays_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(state.HeldWork[LaneRefA], workflowJournalExistsProbe: _ => true);

        Assert.Equal("dispatched", rendered);
    }

    [Fact]
    public void Reconciler_status_arm_lane_shaped_item_without_journal_renders_todays_orphan_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "shape-1", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(state.HeldWork[LaneRefA], workflowJournalExistsProbe: _ => false);

        Assert.Equal(
            $"dispatch recorded; workflow never started (no ledger found at {LaneRefA.AsWorkflowDirectoryPath()})",
            rendered);
    }

    [Fact]
    public void Reconciler_status_arm_memory_proposal_with_capture_file_renders_operator_wait_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                MemoryProposalRef, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget, "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(
            state.HeldWork[MemoryProposalRef], memoryProposalFileExistsProbe: _ => true);

        Assert.Equal("awaiting operator decision (memory proposal)", rendered);
    }

    [Fact]
    public void Reconciler_status_arm_escalated_memory_proposal_renders_the_generic_escalation_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                MemoryProposalRef, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget, "decider-1"),
            new RoomEvent.HeldWorkEscalated(MemoryProposalRef, "operator"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(
            state.HeldWork[MemoryProposalRef], memoryProposalFileExistsProbe: _ => true);

        Assert.Equal("escalated to operator", rendered);
    }

    [Fact]
    public void Reconciler_status_arm_memory_proposal_without_capture_file_renders_missing_file_line()
    {
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(
                MemoryProposalRef, MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget, "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(
            state.HeldWork[MemoryProposalRef], memoryProposalFileExistsProbe: _ => false);

        Assert.Equal(
            $"proposal file missing (memory proposal; no capture file found at {MemoryProposalRef.Value})",
            rendered);
    }

    [Fact]
    public void Reconciler_status_arm_unknown_future_shape_deliberately_keeps_the_lane_probe()
    {
        // No shape other than memory-proposal is distinguished today (#832) -- an unrecognised
        // shape falls through to the lane probe deliberately, not silently; this test pins that
        // choice so a future shape added without its own case is caught here, not in production.
        var state = RoomProjector.Project([
            new RoomEvent.HeldWorkDispatched(LaneRefA, "some-future-shape", TimeSpan.FromMinutes(10), "decider-1"),
        ]);

        var rendered = HeldWorkReconciler.RenderStatus(state.HeldWork[LaneRefA], workflowJournalExistsProbe: _ => false);

        Assert.Equal(
            $"dispatch recorded; workflow never started (no ledger found at {LaneRefA.AsWorkflowDirectoryPath()})",
            rendered);
    }

    [Fact]
    public void Projects_RuntimePermissionAsked_sets_PendingPermission()
    {
        var emptyState = RoomProjector.Project([]);
        Assert.Null(emptyState.PendingPermission);

        var askedAt = DateTimeOffset.UtcNow;
        var askedEvent = new RoomEvent.RuntimePermissionAsked(
            "req-101",
            new ExecutionId("exec-01"),
            new StepId("step-01"),
            "worker-alpha",
            "claude",
            "tool_use_123",
            "WriteFiles",
            """{"path":"test.txt"}""",
            "WriteFiles",
            askedAt);

        var state = RoomProjector.Project([askedEvent]);

        Assert.NotNull(state.PendingPermission);
        Assert.Equal("req-101", state.PendingPermission.PermissionRequestId);
        Assert.Equal("worker-alpha", state.PendingPermission.WorkerId);
        Assert.Equal("claude", state.PendingPermission.VendorTag);
        Assert.Equal("WriteFiles", state.PendingPermission.ToolName);
        Assert.Equal("""{"path":"test.txt"}""", state.PendingPermission.ToolInputJson);
        Assert.Equal("WriteFiles", state.PendingPermission.Category);
        Assert.Equal(askedAt, state.PendingPermission.AskedAt);
    }

    [Fact]
    public void Projects_RuntimePermissionAsked_followed_by_matching_Answered_clears_PendingPermission()
    {
        var askedAt = DateTimeOffset.UtcNow;
        var askedEvent = new RoomEvent.RuntimePermissionAsked(
            "req-101",
            new ExecutionId("exec-01"),
            new StepId("step-01"),
            "worker-alpha",
            "claude",
            "tool_use_123",
            "WriteFiles",
            """{"path":"test.txt"}""",
            "WriteFiles",
            askedAt);

        var answeredEvent = new RoomEvent.RuntimePermissionAnswered(
            "req-101",
            "AllowOnce",
            null,
            "Approved",
            "operator-bob",
            askedAt.AddSeconds(5));

        var state = RoomProjector.Project([askedEvent, answeredEvent]);

        Assert.Null(state.PendingPermission);
    }

    [Fact]
    public void Projects_RuntimePermissionAsked_followed_by_matching_Revoked_clears_PendingPermission()
    {
        var askedAt = DateTimeOffset.UtcNow;
        var askedEvent = new RoomEvent.RuntimePermissionAsked(
            "req-101",
            new ExecutionId("exec-01"),
            new StepId("step-01"),
            "worker-alpha",
            "claude",
            "tool_use_123",
            "WriteFiles",
            """{"path":"test.txt"}""",
            "WriteFiles",
            askedAt);

        var revokedEvent = new RoomEvent.RuntimePermissionRevoked(
            "req-101",
            "Cancelled by turn host",
            askedAt.AddSeconds(5));

        var state = RoomProjector.Project([askedEvent, revokedEvent]);

        Assert.Null(state.PendingPermission);
    }

    [Fact]
    public void Projects_RuntimePermissionAsked_followed_by_non_matching_Answered_leaves_PendingPermission_unchanged()
    {
        var askedAt = DateTimeOffset.UtcNow;
        var askedEvent = new RoomEvent.RuntimePermissionAsked(
            "req-101",
            new ExecutionId("exec-01"),
            new StepId("step-01"),
            "worker-alpha",
            "claude",
            "tool_use_123",
            "WriteFiles",
            """{"path":"test.txt"}""",
            "WriteFiles",
            askedAt);

        var answeredEvent = new RoomEvent.RuntimePermissionAnswered(
            "req-999",
            "AllowOnce",
            null,
            "Approved",
            "operator-bob",
            askedAt.AddSeconds(5));

        var state = RoomProjector.Project([askedEvent, answeredEvent]);

        Assert.NotNull(state.PendingPermission);
        Assert.Equal("req-101", state.PendingPermission.PermissionRequestId);
    }

    /// <summary>
    /// Order-robustness: an `Answered` (or `Revoked`) can be journaled BEFORE its `Asked` — the daemon
    /// appends `Asked` asynchronously after observing the ask file, while the answer path appends
    /// `Answered` directly, so a fast/automated answer or crash reconciliation can invert the order. A
    /// late `Asked` for an already-resolved gate must NOT re-open it. The control is the non-matching
    /// test above: proving the resolved-id suppression keys on the id, not a blanket ignore-all-asks.
    /// </summary>
    [Fact]
    public void Projects_Answered_before_its_Asked_never_reopens_the_gate()
    {
        var at = DateTimeOffset.UtcNow;
        var answeredFirst = new RoomEvent.RuntimePermissionAnswered(
            "req-101", "Deny", null, "Denied", "operator-bob", at);
        var askedLate = new RoomEvent.RuntimePermissionAsked(
            "req-101", new ExecutionId("exec-01"), new StepId("step-01"), "worker-alpha", "claude",
            "tool_use_123", "WriteFiles", """{"path":"test.txt"}""", "WriteFiles", at.AddSeconds(1));

        var state = RoomProjector.Project([answeredFirst, askedLate]);

        Assert.Null(state.PendingPermission);
    }

    [Fact]
    public void Projects_Answered_and_Revoked_events_accumulate_joined_permission_answers()
    {
        var at = DateTimeOffset.UtcNow;
        var askedEvent = new RoomEvent.RuntimePermissionAsked(
            "req-1", new ExecutionId("exec-01"), new StepId("step-01"), "worker-alpha", "claude",
            "tool_use_123", "Bash", """{"command":"ls"}""", "Shell", at);

        var answeredEvent = new RoomEvent.RuntimePermissionAnswered(
            "req-1", "AllowOnce", null, "Allowed by operator", "operator-bob", at.AddSeconds(2));

        var revokedEvent = new RoomEvent.RuntimePermissionRevoked(
            "req-2", "turn_ended", at.AddSeconds(5));

        var state = RoomProjector.Project([askedEvent, answeredEvent, revokedEvent]);

        Assert.Equal(2, state.PermissionAnswers.Count);

        var ans1 = state.PermissionAnswers[0];
        Assert.Equal("req-1", ans1.PermissionRequestId);
        Assert.Equal("Bash", ans1.ToolName);
        Assert.Equal("Shell", ans1.Category);
        Assert.Equal("AllowOnce", ans1.DecisionKind);
        Assert.Equal("Allowed by operator", ans1.Reason);
        Assert.Equal("operator-bob", ans1.DeciderIdentity);
        Assert.False(ans1.WasRevoked);

        var ans2 = state.PermissionAnswers[1];
        Assert.Equal("req-2", ans2.PermissionRequestId);
        Assert.Equal("(unknown)", ans2.ToolName);
        Assert.Equal("", ans2.Category);
        Assert.True(ans2.WasRevoked);
        Assert.Equal("turn_ended", ans2.Reason);
    }

    [Fact]
    public void Projects_PermissionAnswers_bounds_list_to_newest_50_dropping_oldest()
    {
        var at = DateTimeOffset.UtcNow;
        var events = new List<RoomEvent>();

        for (int i = 1; i <= 55; i++)
        {
            events.Add(new RoomEvent.RuntimePermissionRevoked($"req-{i}", "timeout", at.AddSeconds(i)));
        }

        var state = RoomProjector.Project(events);

        Assert.Equal(50, state.PermissionAnswers.Count);
        Assert.Equal("req-6", state.PermissionAnswers[0].PermissionRequestId);
        Assert.Equal("req-55", state.PermissionAnswers[49].PermissionRequestId);
    }

    [Fact]
    public void Projects_DormancyTransitions_in_order_with_detail_paired()
    {
        var ts1 = DateTimeOffset.UtcNow;
        var ts2 = ts1.AddMinutes(1);
        var ts3 = ts2.AddMinutes(5);

        var escalation = new RoomEvent.EscalationRaised(
            new WorkerId("worker-1"),
            EscalationTrigger.Direction,
            new EscalationSubject.HostCondition("turn-host-dormancy", "3 consecutive turns without progress"),
            ts1);

        var entered = new RoomEvent.TurnHostDormancyEntered(3, ts2);
        var cleared = new RoomEvent.TurnHostDormancyCleared("operator", ts3);

        var state = RoomProjector.Project([escalation, entered, cleared]);

        Assert.False(state.IsDormant);
        Assert.Equal(2, state.DormancyTransitions.Count);

        var t1 = state.DormancyTransitions[0];
        Assert.True(t1.IsEntered);
        Assert.Equal(3, t1.ConsecutiveFailures);
        Assert.Equal("3 consecutive turns without progress", t1.Detail);
        Assert.Null(t1.ClearedBy);
        Assert.Equal(ts2, t1.Timestamp);

        var t2 = state.DormancyTransitions[1];
        Assert.False(t2.IsEntered);
        Assert.Equal(0, t2.ConsecutiveFailures);
        Assert.Null(t2.Detail);
        Assert.Equal("operator", t2.ClearedBy);
        Assert.Equal(ts3, t2.Timestamp);
    }

    [Fact]
    public void RoomState_Equals_discriminates_on_DormancyTransitions()
    {
        var ts = DateTimeOffset.UtcNow;
        var t1 = new DormancyTransition(true, 3, "detail", null, ts);
        var t2 = new DormancyTransition(false, 0, null, "operator", ts.AddMinutes(1));

        var emptyDict = new Dictionary<HeldWorkRef, HeldWorkState>();
        var s1 = new RoomState(emptyDict, [], DormancyTransitions: [t1]);
        var s2 = new RoomState(emptyDict, [], DormancyTransitions: [t1]);
        var s3 = new RoomState(emptyDict, [], DormancyTransitions: [t1, t2]);

        Assert.Equal(s1, s2);
        Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
        Assert.NotEqual(s1, s3);
    }
}
