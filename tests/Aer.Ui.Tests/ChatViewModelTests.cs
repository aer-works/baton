using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// M24 Phase 1 desktop chat UI (issue #262): <see cref="ChatViewModel"/> is pure Aer.Ui.Core logic
/// (no Avalonia, no daemon) — these tests exercise it directly rather than through a headless
/// window, the same split <see cref="PausedStepViewModelTests"/> already draws for its own ViewModel.
/// </summary>
public class ChatViewModelTests
{
    private static SessionMetadata MetadataWithTurns(params SessionTurn[] turns) => new(
        SessionId: "sess-1",
        RoomDirectoryPath: "/tmp/sess-1",
        CurrentAdapter: "claude",
        CurrentVendorSessionId: "vendor-1",
        Model: null,
        WorkingDirectory: null,
        TurnCount: turns.Length,
        SafetyCeiling: 100,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Turns: [.. turns]);

    [Fact]
    public void LoadFromMetadata_RendersOneRowPerHumanMessageAndOnePerCompletedResponse()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "Still thinking?", null, DateTimeOffset.UtcNow, true, false));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal("sess-1", viewModel.SessionId);
        Assert.Equal(3, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsFromUser);
        Assert.Equal("Hello", viewModel.Messages[0].Text);
        Assert.False(viewModel.Messages[1].IsFromUser);
        Assert.Equal("Hi there", viewModel.Messages[1].Text);
        Assert.True(viewModel.Messages[2].IsFromUser);
        Assert.Equal("Still thinking?", viewModel.Messages[2].Text);
    }

    [Fact]
    public void LoadFromMetadata_HeaderIsTheCanonicalRoomName_AndWorkerChipIsTheVendor()
    {
        // Guards the header wiring in ChatViewModel.LoadFromMetadata: the room resolves through the
        // shared RoomProjectionLoader.FriendlyNameFor (so it matches the switcher row), and the chip is
        // the vendor. Asserts both the concrete value and the shared-helper equality.
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));

        viewModel.LoadFromMetadata(metadata, "/tmp/aer-flow");

        Assert.Equal(RoomProjectionLoader.FriendlyNameFor("/tmp/aer-flow"), viewModel.HeadlineText);
        Assert.Equal("aer-flow", viewModel.HeadlineText);
        Assert.Equal("claude", viewModel.WorkerChipText);
        Assert.True(viewModel.HasWorker);
    }

    /// <summary>
    /// #592: guards <see cref="ChatViewModel.WorkerIsOrchestrator"/> and
    /// <see cref="ChatViewModel.IsOrchestratorReassignVisible"/> — see those properties' doc
    /// comments for the rule (ruling 3) each renders.
    /// </summary>
    [Fact]
    public void LoadFromMetadata_SurfacesOrchestratorStatus_AndHidesReassignControlForOneParticipant()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants = [new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true)],
        };

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.True(viewModel.WorkerIsOrchestrator);
        Assert.Single(viewModel.Participants);
        Assert.False(viewModel.IsOrchestratorReassignVisible);
    }

    /// <summary>The other half of ruling 3: a second participant makes the reassign control visible, and the status still reads off the chip's own participant (the first), not whichever one holds the role.</summary>
    [Fact]
    public void LoadFromMetadata_TwoParticipants_ShowsReassignControl_ChipStatusReadsFirstParticipant()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants =
            [
                new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: false),
                new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: true),
            ],
        };

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.False(viewModel.WorkerIsOrchestrator);
        Assert.Equal(2, viewModel.Participants.Count);
        Assert.True(viewModel.IsOrchestratorReassignVisible);
    }

    /// <summary><see cref="ChatViewModel.Clear"/> resets the orchestrator surfacing the same way it resets every other chip field -- a stale "reassign visible" flag surviving a room close would let the next opened room borrow the previous room's participant count for a render tick.</summary>
    [Fact]
    public void Clear_ResetsOrchestratorStatusAndParticipants()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants =
            [
                new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true),
                new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false),
            ],
        };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        viewModel.Clear();

        Assert.False(viewModel.WorkerIsOrchestrator);
        Assert.Empty(viewModel.Participants);
        Assert.False(viewModel.IsOrchestratorReassignVisible);
    }

    /// <summary>0054 §4/#1307 ruling 2: tapping a chip sets the sticky tag, and it shows for a room with more than one participant -- the same visibility precedent <see cref="IsOrchestratorReassignVisible"/> already establishes.</summary>
    [Fact]
    public void SelectTagParticipant_SetsTheStickyTag_AndShowsTheChipRowForTwoParticipants()
    {
        var viewModel = new ChatViewModel();
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants =
            [
                new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true),
                second,
            ],
        };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.True(viewModel.ShowTagChipRow);
        Assert.False(viewModel.HasSelectedTag);

        viewModel.SelectTagParticipantCommand.Execute(second.Id);

        Assert.True(viewModel.HasSelectedTag);
        Assert.Equal(second, viewModel.SelectedTagParticipant);
        Assert.Equal("To: claude-2", viewModel.SelectedTagLabel);
    }

    /// <summary>The row collapses for a single-participant room -- ruling 4's "match the reassign control's hidden-at-one-participant precedent."</summary>
    [Fact]
    public void ShowTagChipRow_IsFalse_ForASingleParticipantRoom()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        { Participants = [new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true)] };

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.False(viewModel.ShowTagChipRow);
    }

    /// <summary>The untag/clear affordance returns the composer to the room default (an untagged send).</summary>
    [Fact]
    public void ClearTagParticipant_ReturnsToTheRoomDefault()
    {
        var viewModel = new ChatViewModel();
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants =
            [
                new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true),
                second,
            ],
        };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");
        viewModel.SelectTagParticipantCommand.Execute(second.Id);

        viewModel.ClearTagParticipantCommand.Execute(null);

        Assert.False(viewModel.HasSelectedTag);
        Assert.Null(viewModel.SelectedTagParticipant);
        Assert.Equal("To: room", viewModel.SelectedTagLabel);
    }

    /// <summary>
    /// 0054 §4/#1307's queued-capture rule: each queued message keeps the tag that was selected at
    /// enqueue time, even if the operator re-tags (or clears) the sticky chip afterward -- a later
    /// choice must not silently retarget a message already waiting.
    /// </summary>
    [Fact]
    public void EnqueueMessage_CapturesTheStickyTagAtEnqueueTime_NotAtDrainTime()
    {
        var viewModel = new ChatViewModel();
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants =
            [
                new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true),
                second,
            ],
        };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        viewModel.SelectTagParticipantCommand.Execute(second.Id);
        viewModel.EnqueueMessage("tagged message");

        viewModel.ClearTagParticipantCommand.Execute(null);
        viewModel.EnqueueMessage("untagged message");

        Assert.Equal(2, viewModel.QueuedMessages.Count);
        Assert.Equal(second.Id, viewModel.QueuedMessages[0].TargetParticipantId);
        Assert.Null(viewModel.QueuedMessages[1].TargetParticipantId);
    }

    /// <summary>
    /// #1307 second-reader finding: the tagged participant leaving the room (#1308 will make this
    /// routine) must not leave <see cref="ChatViewModel.SelectedTagParticipantId"/> naming someone who
    /// is no longer in <see cref="ChatViewModel.Participants"/> -- otherwise <see cref="ChatViewModel.SelectedTagLabel"/>
    /// reads "To: room" while <see cref="ChatViewModel.HasSelectedTag"/> stays true, and the next send
    /// still carries the vanished id. A subsequent enqueue must capture the cleared (null) tag, not the
    /// stale one.
    /// </summary>
    [Fact]
    public void LoadFromMetadata_TaggedParticipantNoLongerInTheRoster_ClearsTheTag_AndTheNextEnqueueCapturesNull()
    {
        var viewModel = new ChatViewModel();
        var orchestrator = new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true);
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        { Participants = [orchestrator, second] };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");
        viewModel.SelectTagParticipantCommand.Execute(second.Id);
        Assert.True(viewModel.HasSelectedTag);

        var afterSecondLeft = metadata with { Participants = [orchestrator] };
        viewModel.LoadFromMetadata(afterSecondLeft, "/tmp/sess-1");

        Assert.False(viewModel.HasSelectedTag);
        Assert.Null(viewModel.SelectedTagParticipantId);
        Assert.Equal("To: room", viewModel.SelectedTagLabel);

        viewModel.EnqueueMessage("posted after the tagged participant left");
        Assert.Null(Assert.Single(viewModel.QueuedMessages).TargetParticipantId);
    }

    /// <summary>Polarity pair for the guard above: a room reload that STILL carries the tagged participant must not clear it -- guards against a regression that always clears on every LoadFromMetadata call.</summary>
    [Fact]
    public void LoadFromMetadata_TaggedParticipantStillPresent_KeepsTheTag()
    {
        var viewModel = new ChatViewModel();
        var orchestrator = new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true);
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        { Participants = [orchestrator, second] };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");
        viewModel.SelectTagParticipantCommand.Execute(second.Id);

        // A second LoadFromMetadata call (the ordinary live-refresh poll) describing the SAME roster.
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.True(viewModel.HasSelectedTag);
        Assert.Equal(second.Id, viewModel.SelectedTagParticipantId);
        Assert.Equal("To: claude-2", viewModel.SelectedTagLabel);
    }

    /// <summary>
    /// Pins <see cref="QueuedChatMessageViewModel.ClearTargetParticipantId"/>'s call site -- see its
    /// own remarks and <see cref="ChatViewModel.LoadFromMetadata"/>'s for the decision and why it
    /// differs from the re-tag freeze <see cref="EnqueueMessage_CapturesTheStickyTagAtEnqueueTime_NotAtDrainTime"/>
    /// pins.
    /// </summary>
    [Fact]
    public void LoadFromMetadata_QueuedItemsTaggedToADepartedParticipant_HaveTheirCapturedTagCleared()
    {
        var viewModel = new ChatViewModel();
        var orchestrator = new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true);
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        { Participants = [orchestrator, second] };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        viewModel.SelectTagParticipantCommand.Execute(second.Id);
        viewModel.EnqueueMessage("queued for claude-2");
        viewModel.ClearTagParticipantCommand.Execute(null);
        viewModel.EnqueueMessage("queued for the room");
        Assert.Equal(second.Id, viewModel.QueuedMessages[0].TargetParticipantId);
        Assert.Null(viewModel.QueuedMessages[1].TargetParticipantId);

        var afterSecondLeft = metadata with { Participants = [orchestrator] };
        viewModel.LoadFromMetadata(afterSecondLeft, "/tmp/sess-1");

        Assert.Equal(2, viewModel.QueuedMessages.Count); // the guard clears the captured tag, never the queued item itself
        Assert.Null(viewModel.QueuedMessages[0].TargetParticipantId);
        Assert.Null(viewModel.QueuedMessages[1].TargetParticipantId);
    }

    /// <summary>Ruling 2 (see ChatViewModel.Clear's own comment): Clear() resets the sticky tag, so a room close never leaks the previous room's tag into the next one opened.</summary>
    [Fact]
    public void Clear_ResetsTheStickyTag()
    {
        var viewModel = new ChatViewModel();
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        {
            Participants =
            [
                new Participant(new WorkerId("claude"), "claude", "claude", "sonnet", null, IsOrchestrator: true),
                second,
            ],
        };
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");
        viewModel.SelectTagParticipantCommand.Execute(second.Id);

        viewModel.Clear();

        Assert.False(viewModel.HasSelectedTag);
        Assert.Null(viewModel.SelectedTagParticipantId);
    }

    [Fact]
    public void LoadFromMetadata_ParticipantWithModel_SetsWorkerModelText()
    {
        // Test debt from #1310 (0054 §1, #1305): WorkerModelText/HasWorkerModel were wired in
        // LoadFromMetadata but never pinned against a metadata that actually carries a Participant.
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        { Participants = [new Participant(new WorkerId("claude"), "claude", "claude", "claude-sonnet-4.5", null, true)] };

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal("claude-sonnet-4.5", viewModel.WorkerModelText);
        Assert.True(viewModel.HasWorkerModel);
    }

    [Fact]
    public void LoadFromMetadata_ParticipantWithNoModel_HasWorkerModelIsFalse()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false))
            with
        { Participants = [new Participant(new WorkerId("claude"), "claude", "claude", null, null, true)] };

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Null(viewModel.WorkerModelText);
        Assert.False(viewModel.HasWorkerModel);
    }

    [Fact]
    public void BeginSend_ShowsThePendingMessageUntilLoadFromMetadataObservesTheCompletedTurn()
    {
        var viewModel = new ChatViewModel();
        var initialMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));
        viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");

        viewModel.BeginSend("What's next?", currentTurnsCount: initialMetadata.Turns.Count);
        Assert.True(viewModel.IsSending);
        Assert.Equal(string.Empty, viewModel.InputText);

        // Poll #1: the daemon hasn't finished the turn yet -- Turns is unchanged, but the pending
        // message should still render so Send doesn't look like it silently did nothing.
        viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");
        Assert.True(viewModel.IsSending);
        Assert.Equal(3, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[^1].IsFromUser);
        Assert.Equal("What's next?", viewModel.Messages[^1].Text);

        // Poll #2: the turn landed.
        var completedMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "What's next?", "Let's continue", DateTimeOffset.UtcNow, true, false));
        viewModel.LoadFromMetadata(completedMetadata, "/tmp/sess-1");

        Assert.False(viewModel.IsSending);
        Assert.Equal(4, viewModel.Messages.Count);
        Assert.Equal("Let's continue", viewModel.Messages[^1].Text);
    }

    [Fact]
    public void FailSend_ClearsIsSendingAndSurfacesTheError()
    {
        var viewModel = new ChatViewModel();
        viewModel.BeginSend("Hello", currentTurnsCount: 0);

        viewModel.FailSend("The daemon rejected the request.");

        Assert.False(viewModel.IsSending);
        Assert.Equal("The daemon rejected the request.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
    }

    // ---- #1074: the composer never blocks — messages queue and drain on completion ----

    [Fact]
    public void EnqueueMessage_queues_the_message_clears_the_composer_and_flags_the_queue()
    {
        var viewModel = new ChatViewModel { InputText = "one more thing" };

        viewModel.EnqueueMessage("one more thing");

        Assert.True(viewModel.HasQueuedMessages);
        Assert.Equal("one more thing", Assert.Single(viewModel.QueuedMessages).Text);
        Assert.Equal(string.Empty, viewModel.InputText);
    }

    [Fact]
    public void TryPeekQueuedMessage_reads_the_head_item_fifo_without_removing_it_and_is_false_on_empty()
    {
        var viewModel = new ChatViewModel();
        viewModel.EnqueueMessage("first");
        viewModel.EnqueueMessage("second");

        // Peek reads the head item without consuming it — the drain relies on this so a failed dispatch
        // leaves the message queued, and returns the item (not the text) so removal is by identity.
        Assert.True(viewModel.TryPeekQueuedMessage(out var head));
        Assert.Equal("first", head!.Text);
        Assert.Equal(2, viewModel.QueuedMessages.Count);

        // Removing that exact item consumes it, FIFO.
        viewModel.RemoveQueuedMessage(head);
        Assert.True(viewModel.TryPeekQueuedMessage(out var next));
        Assert.Equal("second", next!.Text);
        viewModel.RemoveQueuedMessage(next);

        Assert.False(viewModel.TryPeekQueuedMessage(out _));
        Assert.False(viewModel.HasQueuedMessages);
    }

    [Fact]
    public void A_failed_dispatch_leaves_the_peeked_head_queued_so_it_is_never_dropped()
    {
        // Finding #1: the drain peeks then only removes the item on success. A dispatch failure
        // (FailSend) must leave the head in the queue — the bound is "never silently dropped on failure".
        var viewModel = new ChatViewModel();
        viewModel.EnqueueMessage("do not lose me");
        viewModel.EnqueueMessage("nor me");

        Assert.True(viewModel.TryPeekQueuedMessage(out var head));
        viewModel.BeginDrainedSend(head!.Text, currentTurnsCount: 0);
        viewModel.FailSend("the daemon was unreachable"); // dispatch failed — item not removed

        Assert.Equal(2, viewModel.QueuedMessages.Count);
        Assert.Equal("do not lose me", viewModel.QueuedMessages[0].Text);
        Assert.True(viewModel.LastSendFailed);
    }

    [Fact]
    public void A_head_removed_during_its_own_dispatch_does_not_drop_the_message_behind_it()
    {
        // Second-reader round 2: the head stays in the queue during the daemon round trip. If the
        // operator removes it mid-dispatch, the success path must remove *that* item by identity, not
        // index 0 — otherwise it drops the message that shuffled into the head slot.
        var viewModel = new ChatViewModel();
        viewModel.EnqueueMessage("being sent");
        viewModel.EnqueueMessage("must survive");

        Assert.True(viewModel.TryPeekQueuedMessage(out var dispatching)); // "being sent"
        viewModel.BeginDrainedSend(dispatching!.Text, currentTurnsCount: 0);

        // Operator clicks Remove on the in-flight head during the (simulated) await.
        dispatching.RemoveCommand.Execute(null);

        // The dispatch then succeeds — the drain removes the exact item it sent (already gone → no-op),
        // NOT whatever is now at index 0.
        viewModel.RemoveQueuedMessage(dispatching);

        Assert.Equal("must survive", Assert.Single(viewModel.QueuedMessages).Text);
    }

    [Fact]
    public void A_queued_message_removed_before_it_sends_never_sends()
    {
        var viewModel = new ChatViewModel();
        viewModel.EnqueueMessage("regret this");
        var item = Assert.Single(viewModel.QueuedMessages);

        item.RemoveCommand.Execute(null);

        Assert.False(viewModel.HasQueuedMessages);
        Assert.False(viewModel.TryPeekQueuedMessage(out _));
    }

    [Fact]
    public void BeginDrainedSend_preserves_the_in_progress_InputText_while_BeginSend_clears_it()
    {
        // The seam #1074 exists to protect: a queued message draining mid-turn must not wipe what the
        // operator is currently typing. BeginSend (the just-typed path) still clears; BeginDrainedSend
        // (the poll's drain path) must leave InputText alone. Both mark the send in flight.
        var draining = new ChatViewModel { InputText = "still typing this" };
        draining.BeginDrainedSend("an earlier queued line", currentTurnsCount: 0);
        Assert.True(draining.IsSending);
        Assert.Equal("still typing this", draining.InputText);

        // Control arm — the ordinary typed-send path DOES clear the composer, so the assertion above
        // is about BeginDrainedSend specifically, not about MarkInFlight never clearing.
        var typed = new ChatViewModel { InputText = "send me" };
        typed.BeginSend("send me", currentTurnsCount: 0);
        Assert.Equal(string.Empty, typed.InputText);
    }

    /// <summary>
    /// #1167: the open-gate clause of <see cref="ChatViewModel.CanDrainQueue"/> is load-bearing on
    /// its own — the cross-client case (a phone-started turn's gate, this client's IsSending false)
    /// is exactly the arm where only that clause holds the drain. Red-proven by dropping the
    /// clause: the gate-open arm fails, the rest stay green.
    /// </summary>
    [Fact]
    public void CanDrainQueue_holds_for_an_open_permission_gate_even_when_this_client_is_not_sending()
    {
        var chat = new ChatViewModel();
        chat.LoadFromMetadata(MetadataWithTurns(), "/tmp/sess-1");
        chat.EnqueueMessage("queued while blocked");

        Assert.True(chat.CanDrainQueue); // control: idle, no gate — drains

        var gate = new Aer.Flow.Projection.PendingPermission(
            "req-1", "chat-worker", "claude", "Bash", "{}", "shell", DateTimeOffset.UtcNow);
        chat.SurfacePendingPermission(gate, (_, _, _) => Task.CompletedTask);

        Assert.False(chat.IsSending);    // the cross-client shape: gate open, not our turn
        Assert.False(chat.CanDrainQueue);

        chat.SurfacePendingPermission(null, (_, _, _) => Task.CompletedTask);
        Assert.True(chat.CanDrainQueue); // gate answered elsewhere — drain resumes
    }

    /// <summary>
    /// #1167's second call site (its second reader's HIGH) — the why lives on
    /// <see cref="ChatViewModel.SendJoinsQueue"/>'s doc. Both polarities pinned: gate-open joins,
    /// the post-failure typed retry stays direct.
    /// </summary>
    [Fact]
    public void SendJoinsQueue_for_an_open_gate_but_a_typed_retry_after_a_failure_posts_directly()
    {
        var chat = new ChatViewModel();
        chat.LoadFromMetadata(MetadataWithTurns(), "/tmp/sess-1");

        Assert.False(chat.SendJoinsQueue); // idle, no gate, no queue — a typed send posts

        var gate = new Aer.Flow.Projection.PendingPermission(
            "req-1", "chat-worker", "claude", "Bash", "{}", "shell", DateTimeOffset.UtcNow);
        chat.SurfacePendingPermission(gate, (_, _, _) => Task.CompletedTask);
        Assert.True(chat.SendJoinsQueue);  // open gate — the send joins the queue
        chat.SurfacePendingPermission(null, (_, _, _) => Task.CompletedTask);

        chat.BeginSend("hello", currentTurnsCount: 0);
        chat.FailSend("daemon unreachable");
        Assert.False(chat.SendJoinsQueue); // failed dispatch: the typed retry stays direct (#1074)

        chat.EnqueueMessage("queued");
        Assert.True(chat.SendJoinsQueue);  // non-empty queue — FIFO preserved
    }

    [Fact]
    public void CanDrainQueue_holds_while_sending_and_while_paused_by_a_failed_dispatch()
    {
        var chat = new ChatViewModel();
        chat.LoadFromMetadata(MetadataWithTurns(), "/tmp/sess-1");

        chat.BeginSend("hello", currentTurnsCount: 0);
        Assert.False(chat.CanDrainQueue); // turn in flight

        chat.FailSend("daemon unreachable");
        Assert.False(chat.CanDrainQueue); // paused by the failure, not by IsSending

        chat.EnqueueMessage("try again"); // operator action clears the pause (#1074's contract)
        Assert.True(chat.CanDrainQueue);
    }

    [Fact]
    public void The_drain_pause_flag_is_separate_from_StatusText_so_a_success_notice_never_stalls_the_queue()
    {
        // Finding #2: the drain gates on LastSendFailed, NOT HasStatusText — StatusText also carries
        // *success* notices ("Mode set to plan.", "Room context cleared.") that must not pause the
        // queue. A non-error status must leave LastSendFailed clear.
        var viewModel = new ChatViewModel { StatusText = "Mode set to plan." };
        Assert.True(viewModel.HasStatusText);
        Assert.False(viewModel.LastSendFailed);

        // And a real dispatch failure sets it, then any fresh send OR enqueue clears it — the latter
        // matters because after a failed drain the queue is non-empty, so the operator's next send
        // enqueues (never MarkInFlight), and only clearing here resumes the drain (no deadlock).
        viewModel.FailSend("the daemon was unreachable");
        Assert.True(viewModel.LastSendFailed);
        viewModel.EnqueueMessage("try again");
        Assert.False(viewModel.LastSendFailed);
    }

    [Fact]
    public void LoadFromMetadata_tracks_the_durable_turn_count_as_the_send_baseline()
    {
        // The completion check keys on metadata.Turns.Count; the send baseline must be that same
        // durable number, not one derived from Messages (which carries the optimistic echo).
        var viewModel = new ChatViewModel();
        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "More", "Sure", DateTimeOffset.UtcNow, false, false)),
            "/tmp/sess-1");

        Assert.Equal(2, viewModel.LastKnownTurnsCount);
    }

    [Fact]
    public void AppendProgress_AccumulatesEachFragmentIntoLiveProgressText()
    {
        var viewModel = new ChatViewModel();

        viewModel.AppendProgress(new WorkerProgressEvent("text", "Thinking", IsPartial: true));
        viewModel.AppendProgress(new WorkerProgressEvent("text", " some more...", IsPartial: true));

        Assert.Equal("Thinking some more...", viewModel.LiveProgressText);
    }

    [Fact]
    public void AppendProgress_SeparatesDiscreteEventsButNotPartialTextDeltas()
    {
        // #323/#1290: "status"/"tool" events, and a fresh non-partial "text" event, are each their
        // own label and must not run together into one unreadable word; only a continuing
        // IsPartial:true "text" stream is one sentence arriving token by token.
        var viewModel = new ChatViewModel();

        viewModel.AppendProgress(new WorkerProgressEvent("status", "Session started"));
        viewModel.AppendProgress(new WorkerProgressEvent("status", "requesting"));
        viewModel.AppendProgress(new WorkerProgressEvent("tool", "PowerShell"));
        viewModel.AppendProgress(new WorkerProgressEvent("text", "Thinking", IsPartial: true));
        viewModel.AppendProgress(new WorkerProgressEvent("text", " some more...", IsPartial: true));
        viewModel.AppendProgress(new WorkerProgressEvent("status", "requesting"));

        Assert.Equal(
            "Session started · requesting · PowerShell · Thinking some more... · requesting",
            viewModel.LiveProgressText);
    }

    [Theory]
    [InlineData(9, "")]
    [InlineData(10, "Thought for 10s")]
    [InlineData(34, "Thought for 34s")]
    [InlineData(59, "Thought for 59s")]
    [InlineData(60, "Thought for 1m 0s")]
    [InlineData(125, "Thought for 2m 5s")]
    public void FormatThinkingTime_GatesOnTheThresholdAndUsesWholeSecondsBelowAMinute(int seconds, string expected)
    {
        // #483: see ThinkingTimeReportThreshold's doc comment for why below-threshold is empty.
        Assert.Equal(expected, ChatViewModel.FormatThinkingTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void BeginSend_ThenAFastCompletion_ReportsNoThinkingTime()
    {
        // The measured duration of this whole test is well under #483's 10-second threshold, so the
        // real client-side stopwatch this exercises end-to-end must gate the caption off, not just
        // FormatThinkingTime in isolation.
        var viewModel = new ChatViewModel();
        var initialMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));
        viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");

        viewModel.BeginSend("What's next?", currentTurnsCount: initialMetadata.Turns.Count);
        Assert.Equal(string.Empty, viewModel.ThinkingTimeText);

        var completedMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "What's next?", "Let's continue", DateTimeOffset.UtcNow, true, false));
        viewModel.LoadFromMetadata(completedMetadata, "/tmp/sess-1");

        Assert.Equal(string.Empty, viewModel.ThinkingTimeText);
        Assert.False(viewModel.HasThinkingTimeText);
    }

    [Fact]
    public void BeginSend_ThenA34SecondCompletion_ShowsTheFormattedCaption_NeverALiveCounter()
    {
        // xunit has no fake wall clock, so DebugThinkingTimeClock is the seam that drives elapsed
        // time without a test literally sleeping 10+ seconds -- this is the discriminating
        // end-to-end proof that LoadFromMetadata's completion branch actually surfaces the caption,
        // not just that FormatThinkingTime is correct in isolation.
        var start = DateTimeOffset.UtcNow;
        ChatViewModel.DebugThinkingTimeClock = () => start;
        try
        {
            var viewModel = new ChatViewModel();
            var initialMetadata = MetadataWithTurns(
                new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));
            viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");

            viewModel.BeginSend("What's next?", currentTurnsCount: initialMetadata.Turns.Count);
            // Nothing renders while the turn is still in flight -- this is never a live counter.
            Assert.Equal(string.Empty, viewModel.ThinkingTimeText);

            ChatViewModel.DebugThinkingTimeClock = () => start.AddSeconds(34);
            var completedMetadata = MetadataWithTurns(
                new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false),
                new SessionTurn(2, "claude", "What's next?", "Let's continue", DateTimeOffset.UtcNow, true, false));
            viewModel.LoadFromMetadata(completedMetadata, "/tmp/sess-1");

            Assert.Equal("Thought for 34s", viewModel.ThinkingTimeText);
            Assert.True(viewModel.HasThinkingTimeText);
        }
        finally
        {
            ChatViewModel.DebugThinkingTimeClock = () => DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public void FailSend_ReportsNoThinkingTime()
    {
        // A failed dispatch never reached a completed turn, so there is nothing to report.
        var viewModel = new ChatViewModel();
        var initialMetadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));
        viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");
        viewModel.BeginSend("What's next?", currentTurnsCount: initialMetadata.Turns.Count);

        viewModel.FailSend("network error");

        Assert.Equal(string.Empty, viewModel.ThinkingTimeText);
        Assert.False(viewModel.HasThinkingTimeText);
    }

    [Fact]
    public void Clear_ResetsEveryFieldToItsEmptyState()
    {
        var start = DateTimeOffset.UtcNow;
        ChatViewModel.DebugThinkingTimeClock = () => start;
        try
        {
            var viewModel = new ChatViewModel();
            var initialMetadata = MetadataWithTurns(
                new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false));
            viewModel.LoadFromMetadata(initialMetadata, "/tmp/sess-1");
            viewModel.CurrentMode = "auto";

            // Drive ThinkingTimeText non-empty first, so the assertion below actually discriminates
            // Clear() clearing it from a real value rather than trivially finding it already empty.
            viewModel.BeginSend("What's next?", currentTurnsCount: initialMetadata.Turns.Count);
            ChatViewModel.DebugThinkingTimeClock = () => start.AddSeconds(34);
            viewModel.LoadFromMetadata(MetadataWithTurns(
                new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false),
                new SessionTurn(2, "claude", "What's next?", "Let's continue", DateTimeOffset.UtcNow, true, false)),
                "/tmp/sess-1");
            Assert.Equal("Thought for 34s", viewModel.ThinkingTimeText);

            viewModel.Clear();

            Assert.Null(viewModel.SessionId);
            Assert.Null(viewModel.RoomDirectoryPath);
            Assert.Empty(viewModel.Messages);
            Assert.Equal("No room open.", viewModel.HeadlineText);
            Assert.Null(viewModel.WorkerChipText);
            Assert.False(viewModel.HasWorker);
            Assert.False(viewModel.IsSending);
            Assert.Null(viewModel.CurrentMode);
            Assert.False(viewModel.HasCurrentMode);
            Assert.Equal(string.Empty, viewModel.ThinkingTimeText);
            Assert.False(viewModel.HasThinkingTimeText);
        }
        finally
        {
            ChatViewModel.DebugThinkingTimeClock = () => DateTimeOffset.UtcNow;
        }
    }

    /// <summary>#286: the mode indicator only renders once a mode has actually been resolved from the daemon — a null default must not read as "mode: (blank)".</summary>
    [Fact]
    public void HasCurrentMode_ReflectsWhetherAModeHasBeenResolved()
    {
        var viewModel = new ChatViewModel();
        Assert.False(viewModel.HasCurrentMode);

        viewModel.CurrentMode = "plan";
        Assert.True(viewModel.HasCurrentMode);

        viewModel.CurrentMode = null;
        Assert.False(viewModel.HasCurrentMode);
    }

    /// <summary>#290: a fresh ChatViewModel (no session ever loaded) must read as "no session open" so the Chat page's new-chat entry point renders instead of the inert message box.</summary>
    [Fact]
    public void IsSessionOpen_IsFalseUntilASessionLoadsAndFalseAgainAfterClear()
    {
        var viewModel = new ChatViewModel();
        Assert.False(viewModel.IsSessionOpen);

        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false)), "/tmp/sess-1");
        Assert.True(viewModel.IsSessionOpen);

        viewModel.Clear();
        Assert.False(viewModel.IsSessionOpen);
    }

    /// <summary>#290: IsSessionOpen derives from RoomDirectoryPath, which is a plain-setter property, not an [ObservableProperty] -- this guards against a regression where LoadFromMetadata/Clear forget to raise the change notification a XAML IsVisible binding depends on.</summary>
    [Fact]
    public void IsSessionOpen_RaisesPropertyChangedOnLoadAndClear()
    {
        var viewModel = new ChatViewModel();
        var raisedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false)), "/tmp/sess-1");
        Assert.Contains(nameof(ChatViewModel.IsSessionOpen), raisedProperties);

        raisedProperties.Clear();
        viewModel.Clear();
        Assert.Contains(nameof(ChatViewModel.IsSessionOpen), raisedProperties);
    }

    [Fact]
    public void PopulateAvailableAdapters_UsesTheProbeResultWhenAtLeastOneVendorIsAvailable()
    {
        var viewModel = new ChatViewModel();

        viewModel.PopulateAvailableAdapters([
            new VendorCliStatus("claude", "claude", IsAvailable: false),
            new VendorCliStatus("agy", "agy", IsAvailable: true),
        ]);

        Assert.Equal(["agy"], viewModel.AvailableAdapters);
        Assert.Equal("agy", viewModel.NewChatAdapter);
    }

    /// <summary>Mirrors the desktop template picker's own fallback (TemplatePickerWindow.PopulateVendors) so the two "start a session" entry points never disagree about what's offered when neither vendor CLI is detected on PATH.</summary>
    [Fact]
    public void PopulateAvailableAdapters_FallsBackToClaudeAndAgyWhenNoneAreAvailable()
    {
        var viewModel = new ChatViewModel();

        viewModel.PopulateAvailableAdapters([
            new VendorCliStatus("claude", "claude", IsAvailable: false),
            new VendorCliStatus("agy", "agy", IsAvailable: false),
        ]);

        Assert.Equal(["claude", "agy"], viewModel.AvailableAdapters);
    }

    [Fact]
    public void LoadCommands_routes_rows_by_the_invokability_the_adapter_layer_stated()
    {
        // #615: the picker no longer holds the which-kinds-are-actionable opinion — it routes on
        // WorkerCapabilityItem.IsInvokable (see WorkerCapabilityItemTests for the kind map). Both
        // polarities in one result: the command must land in the selectable section, the mode in
        // the informational one, and neither anywhere else.
        var viewModel = new ChatViewModel();

        viewModel.LoadCommands(new RoomClient.SessionCommandsResult(
            Vendor: "claude",
            Items:
            [
                new WorkerCapabilityItem("do-thing", "command", "runs the thing"),
                new WorkerCapabilityItem("plan-mode", "mode", "a permission scope"),
            ],
            Models: [],
            RecentlyUsed: []));

        Assert.Equal(["do-thing"], viewModel.InvokableCommands.Select(item => item.Name));
        Assert.Equal(["plan-mode"], viewModel.InfoCommands.Select(item => item.Name));
    }

    [Fact]
    public void LoadFromMetadata_TurnWithErrorMessage_RendersYouAndFailureEntry()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Do work", null, DateTimeOffset.UtcNow, false, false, ErrorMessage: "Process exited with code 1"));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsFromUser);
        Assert.Equal("Do work", viewModel.Messages[0].Text);

        var failureMsg = viewModel.Messages[1];
        Assert.False(failureMsg.IsFromUser);
        Assert.True(failureMsg.IsFailure);
        Assert.Equal("claude", failureMsg.SenderLabel);
        Assert.Equal("Process exited with code 1", failureMsg.Text);
        Assert.NotNull(failureMsg.PrepareFixPromptCommand);
    }

    [Fact]
    public void LoadFromMetadata_TurnWithBothPartialResponseAndErrorMessage_RendersResponseThenFailure()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Do work", "Partial output...", DateTimeOffset.UtcNow, false, false, ErrorMessage: "Process crashed"));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal(3, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsFromUser);
        Assert.Equal("Do work", viewModel.Messages[0].Text);

        Assert.False(viewModel.Messages[1].IsFromUser);
        Assert.False(viewModel.Messages[1].IsFailure);
        Assert.Equal("Partial output...", viewModel.Messages[1].Text);

        var failureMsg = viewModel.Messages[2];
        Assert.False(failureMsg.IsFromUser);
        Assert.True(failureMsg.IsFailure);
        Assert.Equal("Process crashed", failureMsg.Text);
    }

    [Fact]
    public void PrepareFixPrompt_SetsInputTextAndDoesNotSend()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Do work", null, DateTimeOffset.UtcNow, false, false, ErrorMessage: "Compilation failed"));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        var failureMsg = viewModel.Messages[1];
        Assert.True(failureMsg.IsFailure);

        failureMsg.PrepareFixPromptCommand!.Execute(null);

        Assert.Equal("The last turn failed with:\n> Compilation failed\nPlease diagnose and fix it.", viewModel.InputText);
        Assert.False(viewModel.IsSending);
    }

    [Fact]
    public void LoadFromMetadata_HealthyTurn_RendersNoFailureCard()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false, ErrorMessage: null));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.False(viewModel.Messages[0].IsFailure);
        Assert.False(viewModel.Messages[1].IsFailure);
    }

    /// <summary>
    /// 0026 §4/#1180 control arm: a plain ErrorMessage-only turn (IsExhausted false, the #1177
    /// shape) still renders the failure card with its fix button unchanged -- proves this feature
    /// did not perturb the pre-existing failure path.
    /// </summary>
    [Fact]
    public void LoadFromMetadata_OrdinaryFailureTurn_StillRendersTheFailureCardWithFixButton()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Do work", null, DateTimeOffset.UtcNow, false, false,
                ErrorMessage: "Process exited with code 1", IsExhausted: false));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        var failureMsg = viewModel.Messages[1];
        Assert.True(failureMsg.IsFailure);
        Assert.False(failureMsg.IsOutOfPlan);
        Assert.NotNull(failureMsg.PrepareFixPromptCommand);
    }

    [Fact]
    public void LoadFromMetadata_ExhaustedTurn_WithKnownResetInstant_RendersOutOfPlanCard_NoFixPrompt()
    {
        var resetInstant = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "agy", "Keep going", null, DateTimeOffset.UtcNow, false, false,
                ErrorMessage: "Individual quota reached. Resets in 1h39m10s.",
                IsExhausted: true, ExhaustedUntil: resetInstant));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsFromUser);
        Assert.Equal("Keep going", viewModel.Messages[0].Text);

        var card = viewModel.Messages[1];
        Assert.False(card.IsFromUser);
        Assert.True(card.IsOutOfPlan);
        Assert.False(card.IsFailure);
        Assert.Equal("agy", card.SenderLabel);
        Assert.Equal(PlainLanguage.ForExhaustion(resetInstant), card.Text);
        Assert.Equal($"Out of plan — resumes {resetInstant.ToLocalTime():yyyy-MM-dd HH:mm}", card.Text);
        // NEVER a PrepareFixPrompt on this card -- rationale on ChatMessageViewModel.IsOutOfPlan.
        Assert.Null(card.PrepareFixPromptCommand);
        // Copy carries the raw vendor text, not the plain-language sentence rendered above.
        Assert.Equal("Individual quota reached. Resets in 1h39m10s.", card.CopyText);
    }

    [Fact]
    public void LoadFromMetadata_ExhaustedTurn_WithUnknownReset_RendersHonestUnknownWording()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Keep going", null, DateTimeOffset.UtcNow, false, false,
                ErrorMessage: "credits_required", IsExhausted: true, ExhaustedUntil: null));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        var card = viewModel.Messages[1];
        Assert.True(card.IsOutOfPlan);
        Assert.Equal("Out of plan — reset unknown", card.Text);
        Assert.Null(card.PrepareFixPromptCommand);
    }

    /// <summary>
    /// A partial response can precede exhaustion (the vendor said something before refusing) --
    /// mirrors <see cref="LoadFromMetadata_TurnWithBothPartialResponseAndErrorMessage_RendersResponseThenFailure"/>
    /// for the out-of-plan arm.
    /// </summary>
    [Fact]
    public void LoadFromMetadata_ExhaustedTurn_WithPartialResponse_RendersResponseThenOutOfPlanCard()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Keep going", "Partial output before refusal...", DateTimeOffset.UtcNow, false, false,
                ErrorMessage: "credits_required", IsExhausted: true, ExhaustedUntil: null));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal(3, viewModel.Messages.Count);
        Assert.False(viewModel.Messages[1].IsOutOfPlan);
        Assert.Equal("Partial output before refusal...", viewModel.Messages[1].Text);

        var card = viewModel.Messages[2];
        Assert.True(card.IsOutOfPlan);
        Assert.Equal("Out of plan — reset unknown", card.Text);
    }

    [Fact]
    public void LoadFromMetadata_DormancyAnswerTurn_RendersYouAndDormancyCard_GatedOnIsDormant()
    {
        // #1179: a send into a dormant room is answered by the PRODUCT (IsDormancyAnswer turn), not
        // dispatched -- rendered as You + a dormancy card, same wording/shape as #1178's transition
        // card, never AssistantResponse/ErrorMessage handling.
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "System", "how's it going?", null, DateTimeOffset.UtcNow, false, false, IsDormancyAnswer: true));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsFromUser);
        Assert.Equal("how's it going?", viewModel.Messages[0].Text);

        var card = viewModel.Messages[1];
        Assert.False(card.IsFromUser);
        Assert.True(card.IsDormancy);
        Assert.False(card.IsFailure);
        Assert.Equal("System", card.SenderLabel);
        Assert.Equal("Still dormant — waking is yours to choose.", card.Text);

        // Wake absent while _isDormant is false (the default before any SurfacePendingPermission call).
        Assert.Null(card.WakeCommand);

        bool wakeCalled = false;
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, [], isDormant: true, wake: () => wakeCalled = true);

        var cardWhileDormant = viewModel.Messages[1];
        Assert.NotNull(cardWhileDormant.WakeCommand);
        cardWhileDormant.WakeCommand!.Execute(null);
        Assert.True(wakeCalled);

        // Polarity: once the room is no longer reported dormant, Wake disappears from the same card.
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, [], isDormant: false, wake: null);
        Assert.Null(viewModel.Messages[1].WakeCommand);
    }

    [Fact]
    public void SurfacePendingPermission_RendersDormancyTransitions_InTimestampOrder_AndWakeVisibilityPolarity()
    {
        var viewModel = new ChatViewModel();
        var t0 = DateTimeOffset.UtcNow;
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);
        var t3 = t0.AddMinutes(3);
        var t4 = t0.AddMinutes(4);

        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "First turn", "Done turn 1", t1, false, false));
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        var transition1 = new Aer.Flow.Domain.DormancyTransition(true, 3, "build failed", null, t2);
        var transition2 = new Aer.Flow.Domain.DormancyTransition(false, 0, null, "operator", t3);
        var transition3 = new Aer.Flow.Domain.DormancyTransition(true, 3, "build failed again", null, t4);

        bool wakeCalled = false;
        Action wakeAction = () => wakeCalled = true;

        viewModel.SurfacePendingPermission(
            null,
            null,
            (_, _, _) => Task.CompletedTask,
            [transition1, transition2, transition3],
            isDormant: true,
            wake: wakeAction);

        Assert.Equal(5, viewModel.Messages.Count);

        var msgEntered1 = viewModel.Messages[2];
        Assert.True(msgEntered1.IsDormancy);
        Assert.Contains("Dormant — stopped after 3 machine turns without progress.", msgEntered1.Text);
        Assert.Contains("build failed", msgEntered1.Text);
        Assert.Null(msgEntered1.WakeCommand);

        var msgCleared1 = viewModel.Messages[3];
        Assert.True(msgCleared1.IsSystem);
        Assert.False(msgCleared1.IsDormancy);
        Assert.Equal("Woken by operator.", msgCleared1.Text);

        var msgEntered2 = viewModel.Messages[4];
        Assert.True(msgEntered2.IsDormancy);
        Assert.NotNull(msgEntered2.WakeCommand);

        msgEntered2.WakeCommand!.Execute(null);
        Assert.True(wakeCalled);

        viewModel.SurfacePendingPermission(
            null,
            null,
            (_, _, _) => Task.CompletedTask,
            [transition1, transition2, transition3],
            isDormant: false,
            wake: wakeAction);

        Assert.Null(viewModel.Messages[4].WakeCommand);
    }

    [Fact]
    public void SurfacePendingPermission_TranscriptClear_WatermarkHidesOldDormancyTransitions()
    {
        var viewModel = new ChatViewModel();
        var t0 = DateTimeOffset.UtcNow;
        var t1 = t0.AddMinutes(1);

        var transition = new Aer.Flow.Domain.DormancyTransition(true, 3, "build failed", null, t1);
        viewModel.SurfacePendingPermission(
            null,
            null,
            (_, _, _) => Task.CompletedTask,
            [transition],
            isDormant: true,
            wake: () => { });

        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "First turn", "Done", t0, false, false));
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.Contains(viewModel.Messages, m => m.IsDormancy);

        viewModel.MarkTranscriptCleared();
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        Assert.DoesNotContain(viewModel.Messages, m => m.IsDormancy);
    }

    [Fact]
    public void SurfacePendingPermission_NoTransitions_RendersExactlyAsBefore()
    {
        var viewModel = new ChatViewModel();
        var metadata = MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi there", DateTimeOffset.UtcNow, false, false));

        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");
        viewModel.SurfacePendingPermission(null, null, (_, _, _) => Task.CompletedTask, [], isDormant: false, wake: null);

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.False(viewModel.Messages[0].IsDormancy);
        Assert.False(viewModel.Messages[1].IsDormancy);
    }

    /// <summary>
    /// Pins the three-way merge's tie rule the #1178 review found unexercised: on an exact
    /// timestamp tie, an answer renders BEFORE a dormancy transition (turns already win any tie
    /// with either). A swapped tie priority in <c>RebuildMessages</c> flips the order here.
    /// </summary>
    [Fact]
    public void SurfacePendingPermission_AnswerAndTransitionAtTheSameInstant_AnswerRendersFirst()
    {
        var viewModel = new ChatViewModel();
        viewModel.LoadFromMetadata(MetadataWithTurns(), "/tmp/sess-1");
        var sharedInstant = DateTimeOffset.UtcNow;

        var answer = new PermissionAnswer(
            "req-1", "Bash", "shell", "AllowOnce", null, "operator", sharedInstant, WasRevoked: false);
        var transition = new Aer.Flow.Domain.DormancyTransition(true, 3, "no progress", null, sharedInstant);

        viewModel.SurfacePendingPermission(
            null,
            [answer],
            (_, _, _) => Task.CompletedTask,
            [transition],
            isDormant: true,
            wake: () => { });

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.True(viewModel.Messages[0].IsSystem);
        Assert.False(viewModel.Messages[0].IsDormancy);
        Assert.True(viewModel.Messages[1].IsDormancy);
    }

    [Fact]
    public void SurfacePendingPermission_LiveDecisionCard_AppearsAndClearsAndPreservesInstance()
    {
        var viewModel = new ChatViewModel();
        var stepId = new StepId("review_step");
        var execId = new ExecutionId("exec-100");
        DecideDelegate decide = (_, _, _, _, _, _, _) => Task.CompletedTask;

        var otherStepId = new StepId("build_step");
        var otherExecId = new ExecutionId("exec-200");

        var pausedStep1 = new PausedStepViewModel(stepId, execId, [], decide);
        var otherPausedStep = new PausedStepViewModel(otherStepId, otherExecId, [], decide);

        // Two steps paused at once are two cards — the room models a paused-step COUNT, not a
        // paused step, so a transcript that showed one of them would be hiding the other.
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, null, [pausedStep1, otherPausedStep]);

        Assert.True(viewModel.HasPendingDecision);
        Assert.Equal(2, viewModel.PendingDecisions.Count);
        Assert.Same(pausedStep1, viewModel.PendingDecisions[0]);
        Assert.Same(otherPausedStep, viewModel.PendingDecisions[1]);
        Assert.Equal("review_step (exec-100)", viewModel.PendingDecisions[0].Label);

        // Same key (StepId and ExecutionId) keeps the live instance rather than swapping in the
        // freshly-projected one, so an in-flight IsEnabled toggle survives a poll that changed nothing.
        var pausedStep1Rebuilt = new PausedStepViewModel(stepId, execId, [], decide);
        var otherRebuilt = new PausedStepViewModel(otherStepId, otherExecId, [], decide);
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, null, [pausedStep1Rebuilt, otherRebuilt]);

        Assert.Equal(2, viewModel.PendingDecisions.Count);
        Assert.Same(pausedStep1, viewModel.PendingDecisions[0]);
        Assert.Same(otherPausedStep, viewModel.PendingDecisions[1]);

        // One decision answered while the other stays open leaves exactly the other — the assertion
        // a single-card shape could not make.
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, null, [otherRebuilt]);

        Assert.True(viewModel.HasPendingDecision);
        Assert.Same(otherPausedStep, Assert.Single(viewModel.PendingDecisions));

        // And all of them clearing empties the collection.
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, null, null);

        Assert.False(viewModel.HasPendingDecision);
        Assert.Empty(viewModel.PendingDecisions);
    }

    /// <summary>
    /// A step paused, answered, and paused again on a retry keeps the same StepId with a new
    /// ExecutionId. The reconcile keys on both, so the second pause is a different card — and this
    /// fact is what stops that second half of the key being dropped as redundant: every other
    /// reconcile fact here uses two distinct steps and would pass on StepId alone.
    /// </summary>
    [Fact]
    public void SurfacePendingPermission_SameStepPausedOnANewExecution_IsADifferentCard()
    {
        var viewModel = new ChatViewModel();
        var stepId = new StepId("review_step");
        DecideDelegate decide = (_, _, _, _, _, _, _) => Task.CompletedTask;

        var firstAttempt = new PausedStepViewModel(stepId, new ExecutionId("exec-1"), [], decide);
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, null, [firstAttempt]);

        Assert.Same(firstAttempt, Assert.Single(viewModel.PendingDecisions));

        var retryAttempt = new PausedStepViewModel(stepId, new ExecutionId("exec-2"), [], decide);
        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, null, [retryAttempt]);

        Assert.Same(retryAttempt, Assert.Single(viewModel.PendingDecisions));
    }

    /// <summary>
    /// The transcript already had a tie-break — turn, then answer, then transition — and adding a
    /// fourth stream is a chance to reverse it without noticing. A decision recorded at the very
    /// instant of a turn renders after it.
    /// </summary>
    [Fact]
    public void SurfacePendingPermission_DecisionAndTurnAtTheSameInstant_TurnRendersFirst()
    {
        var viewModel = new ChatViewModel();
        var instant = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

        viewModel.LoadFromMetadata(
            MetadataWithTurns(new SessionTurn(1, "claude", "Human msg 1", null, instant, false, false)),
            "/tmp/sess-1");

        var decisionMoment = new RecordedDecisionMoment(
            new DecisionId("dec-1"),
            new ExecutionId("exec-1"),
            DecisionType.Resume,
            null,
            null,
            DeciderInfo.DefaultHuman,
            instant);

        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, [decisionMoment], null);

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("Human msg 1", viewModel.Messages[0].Text);
        Assert.Equal("Approved", viewModel.Messages[1].Text);
    }

    /// <summary>
    /// The merge peeks only the head of each stream, so it needs its decision list ascending. The
    /// loader appends in journal order, which is not the same promise — this hands it a list whose
    /// unknown-time moment arrives last and still expects it rendered first.
    /// </summary>
    [Fact]
    public void SurfacePendingPermission_DecisionMomentsOutOfOrder_StillRenderChronologically()
    {
        var viewModel = new ChatViewModel();
        var t1 = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 8, 13, 10, 5, 0, TimeSpan.Zero);

        // A session with no turns, but a session: the transcript renders nothing at all without
        // metadata, which is how every stream here behaves and is not this slice's to change.
        viewModel.LoadFromMetadata(MetadataWithTurns(), "/tmp/sess-1");

        var later = new RecordedDecisionMoment(
            new DecisionId("dec-late"), new ExecutionId("exec-1"), DecisionType.Supersede,
            new StepId("target_step"), null, DeciderInfo.DefaultHuman, t2);
        var earlier = new RecordedDecisionMoment(
            new DecisionId("dec-early"), new ExecutionId("exec-0"), DecisionType.Resume,
            null, null, DeciderInfo.DefaultHuman, t1);
        var unknownTime = new RecordedDecisionMoment(
            new DecisionId("dec-unknown"), new ExecutionId("exec-x"), DecisionType.Reject,
            null, null, DeciderInfo.DefaultHuman, null);

        viewModel.SurfacePendingPermission(
            null, null, (_, _, _) => Task.CompletedTask, null, false, null, [later, earlier, unknownTime], null);

        Assert.Equal(3, viewModel.Messages.Count);
        Assert.Equal("Rejected", viewModel.Messages[0].Text);
        Assert.Equal("Approved", viewModel.Messages[1].Text);
        Assert.Equal("Sent back to target_step", viewModel.Messages[2].Text);
    }

    [Fact]
    public void SurfacePendingPermission_AnsweredDecisionRow_LandsInRightInterleavedPosition()
    {
        var viewModel = new ChatViewModel();
        var t1 = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 8, 13, 10, 5, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 8, 13, 10, 10, 0, TimeSpan.Zero);
        var t4 = new DateTimeOffset(2026, 8, 13, 10, 15, 0, TimeSpan.Zero);

        var turn1 = new SessionTurn(1, "claude", "Human msg 1", null, t1, false, false);
        var turn2 = new SessionTurn(2, "claude", "Human msg 2", null, t4, false, false);
        var metadata = MetadataWithTurns(turn1, turn2);
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        var decider = DeciderInfo.DefaultHuman;
        var decisionMoment = new RecordedDecisionMoment(
            new DecisionId("dec-1"),
            new ExecutionId("exec-1"),
            DecisionType.Supersede,
            new StepId("target_step"),
            null,
            decider,
            t2);

        var permissionAnswer = new PermissionAnswer(
            "req-1", "Bash", "shell", "AllowOnce", null, "operator", t3, WasRevoked: false);

        viewModel.SurfacePendingPermission(
            null,
            [permissionAnswer],
            (_, _, _) => Task.CompletedTask,
            null,
            false,
            null,
            [decisionMoment],
            null);

        // Assert full SEQUENCE of messages:
        // 0: Human msg 1 (Turn 1) at t1
        // 1: Sent back to target_step (Decision moment) at t2
        // 2: Allowed once — Bash (Permission answer) at t3
        // 3: Human msg 2 (Turn 2) at t4
        Assert.Equal(4, viewModel.Messages.Count);
        Assert.Equal("Human msg 1", viewModel.Messages[0].Text);
        Assert.Equal("Sent back to target_step", viewModel.Messages[1].Text);
        Assert.True(viewModel.Messages[1].IsSystem);
        Assert.Equal(t2, viewModel.Messages[1].Timestamp);
        Assert.Equal("Allowed once — Bash", viewModel.Messages[2].Text);
        Assert.True(viewModel.Messages[2].IsSystem);
        Assert.Equal(t3, viewModel.Messages[2].Timestamp);
        Assert.Equal("Human msg 2", viewModel.Messages[3].Text);
    }

    [Fact]
    public void SurfacePendingPermission_NullTimestampDecisionMoment_RendersBeforeFirstStampedEntry()
    {
        var viewModel = new ChatViewModel();
        var t1 = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

        var turn1 = new SessionTurn(1, "claude", "Human msg 1", null, t1, false, false);
        var metadata = MetadataWithTurns(turn1);
        viewModel.LoadFromMetadata(metadata, "/tmp/sess-1");

        var decider = DeciderInfo.DefaultHuman;
        var nullTimestampDecision = new RecordedDecisionMoment(
            new DecisionId("dec-0"),
            new ExecutionId("exec-0"),
            DecisionType.Resume,
            null,
            null,
            decider,
            null);

        viewModel.SurfacePendingPermission(
            null,
            null,
            (_, _, _) => Task.CompletedTask,
            null,
            false,
            null,
            [nullTimestampDecision],
            null);

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("Approved", viewModel.Messages[0].Text);
        Assert.True(viewModel.Messages[0].IsSystem);
        Assert.Equal(DateTimeOffset.MinValue, viewModel.Messages[0].Timestamp);
        Assert.Equal("Human msg 1", viewModel.Messages[1].Text);
    }
}
