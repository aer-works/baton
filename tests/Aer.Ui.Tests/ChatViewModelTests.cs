using Aer.Adapters;

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
    public void Clear_ResetsEveryFieldToItsEmptyState()
    {
        var viewModel = new ChatViewModel();
        viewModel.LoadFromMetadata(MetadataWithTurns(
            new SessionTurn(1, "claude", "Hello", "Hi", DateTimeOffset.UtcNow, false, false)), "/tmp/sess-1");
        viewModel.CurrentMode = "auto";

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
}
