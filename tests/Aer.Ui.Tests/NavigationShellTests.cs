using Aer.Ui.Tests.TestSupport;
using Aer.Adapters;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Aer.Ui.Tests;

/// <summary>
/// M19 Phase 2 (issue #187): the navigation shell and Home's decision inbox — section switching,
/// the paused-step inbox item with its artifact preview, and §3's stale-recents card. Task
/// directories are built from hand-written <see cref="FlowEvent"/>s, matching
/// <see cref="MainWindowProjectionTests"/>' convention.
/// </summary>
public class NavigationShellTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => SnapshotBinder.Bind(new WorkflowDefinition(
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            // #1191: declared outputs are FILE NAMES, extension included — the engine satisfies a
            // contract with File.Exists on the declared name (OutcomeClassifier), and every built-in
            // template names them that way ("draft.md", "output.md"). This fixture used to declare
            // "plan"/"review" while writing plan.md/review.md, a pair no real run can produce: that
            // step would have been classified Failed for a missing declared output.
            new WorkflowStepDefinition(Architect, "architect", ["goal.md"], ["plan.md"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan.md"], ["review.md"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: new PausePoint(SupersedeTargets: [Architect])),
        ]));

    // #1191: Outputs is what the execution was contracted to produce — the list MutationInterface
    // turns into the ProducedOutputs ContractValidator satisfies with File.Exists. A request that
    // declares none of them is not a shape a real run of a producing step takes, and leaving it
    // empty here is what let the inbox preview pick the worker's own prompt file.
    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId, IReadOnlyList<string>? outputs = null)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: outputs ?? [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-shell-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static async Task<string> CreateRoomDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), cancellationToken);

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            foreach (var flowEvent in events)
            {
                await writer.AppendAsync(flowEvent, cancellationToken);
            }
        }

        return roomDirectory;
    }

    /// <summary>
    /// #461: a cancelled run reaches <see cref="WorkflowStatus.Terminal"/> like any other — there is
    /// no cancelled workflow status — so the status derivation fell through to "Finished" and told you
    /// a task you had just stopped had completed. Cancellation is only visible in the steps. The
    /// derivation is <see cref="RoomCardViewModel.DeriveStatus"/>, shared by the switcher and the fleet
    /// loader (#1071 retired the Home cards this used to read it through).
    /// </summary>
    [Fact]
    public async Task A_cancelled_task_derives_as_cancelled_and_not_as_finished()
    {
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionCancelled(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission, isFlowLockHeld: true);
            Assert.Equal(RoomCardStatus.Cancelled, status);
            Assert.Equal("Cancelled", statusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>A task paused at critic, with a durable output file for the inbox preview.</summary>
    private static async Task<string> CreatePausedRoomDirectoryAsync(string reviewContent, CancellationToken cancellationToken)
    {
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic, ["review.md"])),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        // #1191: TWO undeclared files, and note where they sort. `notes.md` is here so the fact
        // cannot pass under a "skip prompt.txt by name" implementation: excluding the prompt alone
        // leaves notes.md first, and only asking what the execution declared reaches review.md.
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "notes.md"), "Scratch the worker left behind", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "prompt.txt"), "Undeclared prompt instructions", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), reviewContent, cancellationToken);
        return roomDirectory;
    }

    /// <summary>
    /// The journal a crash leaves behind (#1215): an accepted execution request with no terminal event
    /// after it — which, by §6, is also exactly what a live run's journal looks like. No lock is held
    /// over this directory, and that is the whole difference; see
    /// <see cref="Aer.Ui.Core.RoomClient.DeriveRoomStoppedReason"/>.
    /// </summary>
    private static Task<string> CreateStalledRoomDirectoryAsync(CancellationToken cancellationToken) =>
        CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect))],
            cancellationToken);

    /// <summary>A task paused at a NeedsInput pause point (#334) — the shape an interactive session settles into: "your turn to reply", not an approval gate.</summary>
    private static async Task<string> CreateNeedsInputRoomDirectoryAsync(string replyContent, CancellationToken cancellationToken)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("session-like"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
                new WorkflowStepDefinition(
                    Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [Architect], Kind: PausePointKind.NeedsInput)),
            ]));

        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
            ],
            cancellationToken);

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", "execution_c-1");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "review.md"), replyContent, cancellationToken);
        return roomDirectory;
    }


    [AvaloniaFact]
    public async Task InitializeAsync_starts_on_the_home_section()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
        Assert.True(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsChatVisible);
        // #1071: a bare launch lands on the ▤ front door's first-run surface, with no room open.
        Assert.True(window.ViewModel.Home.HasNoRooms);
    }

    [AvaloniaFact]
    public async Task LandOnTopRoom_opens_the_top_room_instead_of_staying_on_home()
    {
        // Rooms-as-root (#1055, 02-screens.md "Both surfaces open on rooms"): with a room in the
        // switcher, startup lands in the work, not the Home dashboard. The fleet is seeded directly
        // because GetFleetAsync is daemon-only (RoomClient.Fleet.cs) and no daemon runs headless — the
        // real daemon population is covered by DaemonIntegrationTests. The directory is real so
        // OpenAsync can load it, exactly as OpenAsync_navigates_to_the_task_section does.
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            window.ViewModel.Rooms.AddTestItem(new RoomFleetItem(
                roomDirectory, FriendlyName: roomDirectory, TypeLabel: "solo-run-template",
                StatusText: "Idle", PausedStepCount: 0, IsArchived: false,
                Created: DateTimeOffset.UnixEpoch, Updated: DateTimeOffset.UnixEpoch));

            await window.LandOnTopRoomAsync(TestContext.Current.CancellationToken);

            // Since #1196 slice 3 the room it lands in is the transcript, not the shape — what this
            // fact is about is that startup lands in the work rather than on Home, and that claim is
            // unchanged.
            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsChatVisible);
            Assert.False(window.ViewModel.IsHomeVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task LandOnTopRoom_stays_on_home_when_the_fleet_is_empty()
    {
        // An empty fleet must leave the landing exactly as it was — the no-rooms first-run ("Point
        // Baton at a folder") is a later slice and is J8's outcome, not this one's.
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        await window.LandOnTopRoomAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
        Assert.True(window.ViewModel.IsHomeVisible);
    }

    [AvaloniaFact]
    /// <summary>
    /// One room, one surface (#1196 slice 3): a workflow room lands in the same rendering a chat
    /// session does, and is marked as a pipeline room — the flag the composer's three states read.
    /// </summary>
    public async Task OpenAsync_routes_a_workflow_room_to_the_transcript()
    {
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsChatVisible);
            Assert.False(window.ViewModel.IsHomeVisible);

            Assert.True(window.ViewModel.Chat.IsPipelineRoom);
            Assert.True(window.ViewModel.Chat.IsComposerVisible);
            // The whole of the composer's honesty: on screen, saying what it is, and refusing typing.
            Assert.False(window.ViewModel.Chat.IsComposerEnabled);
            Assert.False(window.ViewModel.Chat.IsNewChatVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The room's open decision is on the transcript the moment it opens, not one render later.
    /// <para>
    /// This exists because everything above it passed while the card was invisible in the running app:
    /// <c>OpenAsync</c> loads the projection (building the decisions) and only then clears the chat for
    /// the incoming room, which threw them away. Nothing that asserts on sections or composer state can
    /// see that, and only driving the built app did. What discriminates here is asserting the card
    /// after the WHOLE open path, rather than calling the view model's own entry point directly.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public async Task OpenAsync_puts_a_paused_workflow_rooms_decision_on_the_transcript()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            var decision = Assert.Single(window.ViewModel.Chat.PendingDecisions);
            Assert.Equal(Critic, decision.StepId);
            Assert.True(window.ViewModel.Chat.HasPendingDecision);

            // The same instance the room's own paused-step list holds, not a second one built for the
            // transcript — one decision, answered once, wherever it is rendered.
            Assert.Same(Assert.Single(window.ViewModel.PausedSteps), decision);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Reopening the same room leaves one card per open decision, not one per open. Second reader's
    /// finding on #1204: the clear-order fix was traced correct but nothing pinned it, so a later
    /// change to the reconcile could accumulate duplicates with every test still green.
    /// </summary>
    [AvaloniaFact]
    public async Task Reopening_a_workflow_room_leaves_one_card_per_open_decision()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            var decision = Assert.Single(window.ViewModel.Chat.PendingDecisions);
            Assert.Equal(Critic, decision.StepId);
            Assert.Same(Assert.Single(window.ViewModel.PausedSteps), decision);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1219, at the caller rather than at the derivation. A second reader found the Task view's
    /// headline still reading "Working — …" for a room whose process had died, because its one
    /// production call site took a defaulted lock reading while every other surface probed. The
    /// parity fact in <c>StatusDerivationTests</c> could not catch that: it checks the two
    /// derivations agree given the same answer, not that the caller asks the right question. This
    /// drives the real open path and reads what the headline actually says.
    /// </summary>
    [AvaloniaFact]
    public async Task The_room_headline_says_stopped_for_a_room_whose_process_died()
    {
        var stalledRoom = await CreateStalledRoomDirectoryAsync(TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(stalledRoom, TestContext.Current.CancellationToken);

            Assert.Equal("Stopped", window.ViewModel.RoomHeadlineText);

            // The control arm, and the reason this is not a test of a constant: the same room with a
            // live pump over it reads as working. Without this, a headline hard-coded to "Stopped"
            // would pass.
            using (ConcurrencyGuard.Acquire(stalledRoom, "headline control arm"))
            {
                await window.OpenAsync(stalledRoom, TestContext.Current.CancellationToken);
                Assert.StartsWith("Working", window.ViewModel.RoomHeadlineText);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(stalledRoom);
        }
    }

    /// <summary>
    /// #1215, through the whole open path rather than the derivation alone: a room that stopped
    /// mid-run offers Resume on its transcript, and a room waiting on a decision offers nothing —
    /// it already has the person's action there. Both arms in one fact because the pair is the
    /// claim; either alone passes under a predicate that always answers the same way.
    /// </summary>
    [AvaloniaFact]
    public async Task A_stopped_room_offers_resume_on_its_transcript_and_a_room_awaiting_a_decision_does_not()
    {
        var stalledRoom = await CreateStalledRoomDirectoryAsync(TestContext.Current.CancellationToken);
        var pausedRoom = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

            await window.OpenAsync(stalledRoom, TestContext.Current.CancellationToken);
            var card = window.ViewModel.RoomStoppedCard;
            Assert.NotNull(card);
            Assert.Equal(RoomStoppedReason.StoppedMidRun, card.Reason);
            Assert.Equal("Resume", card.ActionLabel);
            Assert.True(window.ViewModel.HasRoomStoppedCard);

            // Same window, next room: the offer has to clear, or it is an offer against the wrong
            // directory — which is how the retired Run button could resume a room you were not looking at.
            await window.OpenAsync(pausedRoom, TestContext.Current.CancellationToken);
            Assert.Null(window.ViewModel.RoomStoppedCard);
            Assert.False(window.ViewModel.HasRoomStoppedCard);
            Assert.Single(window.ViewModel.Chat.PendingDecisions);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(stalledRoom);
            DirectoryCleanup.DeleteRecursively(pausedRoom);
        }
    }

    /// <summary>
    /// #1215: slice 3 put Stop inside the Shape panel, which is closed by default, so the brake was
    /// unreachable on the surface a person actually sits on — the design rule it broke, and why it is
    /// present-and-disabled rather than hidden, is on the button in <c>ChatView.axaml</c>. This pins
    /// both halves: Stop is in the room header, and it is <em>not</em> also in the shape panel, since
    /// two of them would be two surfaces for one action.
    /// </summary>
    [AvaloniaFact]
    public async Task Stop_is_in_the_room_header_and_not_in_the_collapsible_shape_panel()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            // The default state a person lands in: transcript showing, shape panel closed.
            Assert.False(window.ViewModel.Chat.IsShapePanelOpen);
            Assert.True(window.IsMainRegionVisible);
            Assert.False(window.IsShapeRegionVisible);

            // #1224 moved the header out of ChatView and above the transcript/shape split; the rule
            // this pins is unchanged — Stop is in the header, and not in the collapsible panel.
            Assert.NotNull(window.ChatHeaderControl.FindControl<Button>("StopButton"));
            Assert.Null(window.RoomViewControl.FindControl<Button>("StopButton"));

            // Present, not merely reachable: disabled with nothing in flight, enabled the moment
            // there is — the same binding it carried in the header it came from.
            Assert.False(window.StopButton.IsEnabled);
            window.ViewModel.IsMutationInFlight = true;
            Assert.True(window.StopButton.IsEnabled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1216: the header's Workflow switch renders the room's durable fact, and a room whose workflow
    /// is off is not offered a Shape toggle — there is no shape to show. Both polarities from the same
    /// helper, one room with a <c>WorkflowSwitched</c> in its journal and one without, because the
    /// claim that absence means ON is only worth anything beside a room where it is genuinely off.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_workflow_switch_and_the_shape_toggle_render_the_rooms_own_journal(bool switchItOff)
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            if (switchItOff)
            {
                await using var writer = new RoomEventLogWriter(Path.Combine(roomDirectory, "room.jsonl"));
                await writer.AppendAsync(
                    new RoomEvent.WorkflowSwitched(IsOn: false, "operator", DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
            }

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(!switchItOff, window.ViewModel.Chat.IsWorkflowOn);
            Assert.Equal(switchItOff ? "Workflow OFF" : "Workflow ON", window.ViewModel.Chat.WorkflowSwitchLabel);

            // The switch itself is offered either way — it is how a room comes back.
            Assert.True(window.ViewModel.Chat.IsWorkflowSwitchVisible);
            var switchControl = window.ChatHeaderControl.FindControl<Avalonia.Controls.Primitives.ToggleButton>("WorkflowSwitch");
            Assert.NotNull(switchControl);

            // The Shape toggle is not: a room with no workflow has no shape to open.
            Assert.Equal(!switchItOff, window.ViewModel.Chat.IsShapeToggleVisible);

            // Nor is any workflow action — see ChatViewModel.IsWorkflowActive for why the offer has
            // to go with the shape.
            Assert.Equal(!switchItOff, window.ViewModel.Chat.IsWorkflowActive);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1216, found by driving the built app: another client switched a FINISHED room's workflow and
    /// the open window went on showing the opposite indefinitely. Why the poller no longer stops at
    /// <see cref="WorkflowStatus.Terminal"/> is on <c>MainWindow.UpdateLiveRefreshTimer</c>.
    /// </summary>
    /// <remarks>
    /// Three arms, and the middle one is the control: the tick must NOT reload when the journal has
    /// not moved (that cheapness is the whole reason the poller was stopped here in the first place,
    /// since a re-projection re-reads every execution's artifact directory). Without it this test
    /// would pass just as well against a poller that re-projects unconditionally forever.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_finished_room_still_observes_its_workflow_being_switched_by_someone_else()
    {
        var roomDirectory = await CreateRoomDirectoryAsync(
            TwoStepSnapshot(),
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("a-1"), Architect)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("a-1")),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(new ExecutionId("c-1"), Critic)),
                new FlowEvent.ExecutionSucceeded(new ExecutionId("c-1")),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.True(window.ViewModel.Chat.IsWorkflowOn);

            // The premise that changed: a settled room is still watched.
            Assert.True(window.IsLiveRefreshTimerEnabled);

            // Control — nothing has been appended, so a tick must not re-project.
            var renderedBefore = window.RenderedProjectionCountForTests;
            await window.OnLiveRefreshTickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(renderedBefore, window.RenderedProjectionCountForTests);
            Assert.True(window.ViewModel.Chat.IsWorkflowOn);

            // Someone else — a phone, a second window, the daemon — switches it off.
            await using (var writer = new RoomEventLogWriter(Path.Combine(roomDirectory, "room.jsonl")))
            {
                await writer.AppendAsync(
                    new RoomEvent.WorkflowSwitched(IsOn: false, "operator", DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
            }

            await window.OnLiveRefreshTickAsync(TestContext.Current.CancellationToken);

            Assert.False(window.ViewModel.Chat.IsWorkflowOn);
            Assert.False(window.ViewModel.Chat.IsShapeToggleVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The arm no view-model-level assertion reaches: a shape panel that was already OPEN when the
    /// switch was thrown. Retiring the toggle does not close the panel, so without this the person is
    /// left looking at the shape of a workflow the header says is off.
    /// </summary>
    [AvaloniaFact]
    public async Task Switching_the_workflow_off_closes_a_shape_panel_that_was_already_open()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            window.ViewModel.Chat.IsShapePanelOpen = true;
            Assert.True(window.IsShapeRegionVisible);

            window.ViewModel.Chat.IsWorkflowOn = false;

            Assert.False(window.ViewModel.Chat.IsShapePanelOpen);
            Assert.False(window.IsShapeRegionVisible);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Two claims about the click: which value it asks for (see
    /// <see cref="MainWindowViewModel.ToggleWorkflowSwitchCommand"/> for why it is the room's opposite
    /// and not the control's), and that a refusal is shown rather than swallowed — "stop the room
    /// first" being the only way the person learns what to do next.
    /// </summary>
    [AvaloniaFact]
    public async Task Throwing_the_switch_asks_for_the_opposite_of_the_room_and_surfaces_a_refusal()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            bool? asked = null;
            string? refusal = null;
            window.ViewModel.WorkflowSwitchRequested += isOn =>
            {
                asked = isOn;
                return Task.FromResult(refusal);
            };

            Assert.True(window.ViewModel.Chat.IsWorkflowOn);

            refusal = "Step 'critic' is waiting on a decision.";
            await window.ViewModel.RequestWorkflowSwitchAsync(!window.ViewModel.Chat.IsWorkflowOn);

            Assert.False(asked);
            Assert.Equal(refusal, window.ViewModel.WorkflowSwitchStatusText);
            Assert.True(window.ViewModel.HasWorkflowSwitchStatusText);

            // A refusal never moves the switch: it renders the journal, which did not change.
            Assert.True(window.ViewModel.Chat.IsWorkflowOn);

            // And the next attempt does not inherit the previous refusal's text.
            refusal = null;
            await window.ViewModel.RequestWorkflowSwitchAsync(false);
            Assert.Null(window.ViewModel.WorkflowSwitchStatusText);
            Assert.False(window.ViewModel.HasWorkflowSwitchStatusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1222: a workflow file is not a room, and opening one by path says so instead of drawing its
    /// graph as though it were. Why — the corpus passages that decide it, and where the capability
    /// went instead — is in <c>MainWindow.OpenAsync</c>, beside the branch this pins.
    /// <para>
    /// The discriminating assertion is the empty canvas, not the message: the retired branch also
    /// wrote to <c>StatusText</c>, and only the graph tells the two apart.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_workflow_file_by_path_says_it_is_not_a_room_and_draws_no_graph()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-shell-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("two-step"),
                1,
                [
                    new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1)),
                    new WorkflowStepDefinition(new StepId("critic"), "critic", ["plan"], ["review"], [new StepId("architect")], new RetryPolicy(1)),
                ]);
            var workflowFilePath = Path.Combine(testRoot, "workflow.json");
            await File.WriteAllTextAsync(
                workflowFilePath,
                System.Text.Json.JsonSerializer.Serialize(definition),
                TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            await window.OpenAsync(workflowFilePath, TestContext.Current.CancellationToken);

            Assert.Empty(window.DagCanvas.Children);
            Assert.Contains("not a room", window.FindViewControl<TextBlock>("StatusText")!.Text);
            Assert.Contains("Author", window.FindViewControl<TextBlock>("StatusText")!.Text);
            // It did not navigate anywhere either — a file cannot be the record the shell has open.
            Assert.Equal(ShellSection.Home, window.ViewModel.CurrentSection);
            // And it did not quietly start an editing session instead: M14 Phase 3's separation of
            // inspecting from authoring outlives the read-only view it was written about, so this
            // half of the retired MainWindowTemplateEditorTests fact lives on here.
            Assert.False(window.ViewModel.TemplateEditor.IsOpen);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1222: and it leaves the room you already had open exactly as it was. A mistyped path is a
    /// mistake, not a command to close anything, so the transcript, its section and its live refresh
    /// all stay — only the message changes.
    /// <para>
    /// This is the successor to <c>MainWindowDagTests.Opening_a_template_does_not_start_the_live_refresh_timer</c>,
    /// whose claim was that a file cannot change so nothing should poll for it. With no template
    /// rendering left, the honest version of that claim is about what the open room's poller does,
    /// which is: keep running, because that room really can change.
    /// </para>
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_workflow_file_by_path_leaves_the_room_already_open_alone()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-shell-file-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var workflowFilePath = Path.Combine(testRoot, "workflow.json");
            await File.WriteAllTextAsync(
                workflowFilePath,
                System.Text.Json.JsonSerializer.Serialize(new WorkflowDefinition(
                    new WorkflowTemplateId("one-step"),
                    1,
                    [new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1))])),
                TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);
            var headlineWhileOpen = window.ViewModel.Chat.HeadlineText;

            await window.OpenAsync(workflowFilePath, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.Equal(headlineWhileOpen, window.ViewModel.Chat.HeadlineText);
            Assert.True(window.ViewModel.Chat.IsPipelineRoom);
            Assert.Contains("not a room", window.FindViewControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The shell's layout states — three at #1196 slice 3, two since #1222 retired the full-width
    /// shape. Driving the built app is what verifies this looks right; what this pins is that each
    /// state still puts the width on the column whose content is actually visible, which is the part
    /// a later edit can silently invert, and that the retired state does not come back by accident.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_shell_layout_state_puts_the_width_on_the_column_that_is_showing()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("The plan holds up.", TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            // A workflow room in the transcript, shape closed: the transcript takes everything.
            Assert.True(window.IsMainRegionVisible);
            Assert.False(window.IsShapeRegionVisible);
            Assert.Equal(new GridLength(1, GridUnitType.Star), window.MainColumnWidth);
            Assert.Equal(GridLength.Auto, window.ShapeColumnWidth);

            // Shape toggled on: both, side by side.
            window.ViewModel.Chat.IsShapePanelOpen = true;
            Assert.True(window.IsMainRegionVisible);
            Assert.True(window.IsShapeRegionVisible);
            Assert.Equal(new GridLength(1, GridUnitType.Star), window.MainColumnWidth);
            Assert.Equal(GridLength.Auto, window.ShapeColumnWidth);

            // #1222 retired the third state — the shape alone, full width, the transcript's column
            // giving up its width entirely. There is no section left that produces it, so the
            // transcript is never the thing that yields: switching the shape back off returns to
            // exactly the state above it, and the transcript stays.
            window.ViewModel.Chat.IsShapePanelOpen = false;
            Assert.True(window.IsMainRegionVisible);
            Assert.False(window.IsShapeRegionVisible);
            Assert.Equal(new GridLength(1, GridUnitType.Star), window.MainColumnWidth);
            Assert.Equal(GridLength.Auto, window.ShapeColumnWidth);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// M24 Phase 1 desktop chat UI (issue #262): opening a directory that materialized an interactive
    /// session (.aer/session.json present) loads that session into the transcript — see
    /// <c>MainWindow.OpenAsync</c>'s remarks. What this used to pin was the *routing* — Chat rather
    /// than the generic Task view — and since #1222 there is nowhere else to route to, so what is
    /// left to pin is that the session's own identity arrives with it.
    /// </summary>
    [AvaloniaFact]
    public async Task OpenAsync_routes_an_interactive_session_directory_to_the_chat_section()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-chat-{Guid.NewGuid():N}");
        try
        {
            var metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-nav-test",
                roomDirectoryPath: roomDirectory,
                adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);
            Assert.True(window.ViewModel.IsChatVisible);
            Assert.Equal(metadata.SessionId, window.ViewModel.Chat.SessionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task Enter_in_the_composer_sends_but_shift_enter_does_not()
    {
        // The composer's send rule wired end-to-end, not just IsSendKeystroke in isolation: the KeyDown
        // handler is actually attached to the composer. A bare Enter runs SendChatMessageAsync, whose
        // synchronous BeginSend clears the input; Shift+Enter must not send, so the text survives.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-composer-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-composer-test", roomDirectoryPath: roomDirectory, adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);

            // Shift+Enter is a newline, not a send — the composer text survives.
            window.ViewModel.Chat.InputText = "keep me";
            window.ChatInputBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.Shift,
            });
            Assert.Equal("keep me", window.ViewModel.Chat.InputText);

            // A bare Enter sends — SendChatMessageAsync's synchronous BeginSend clears the input.
            window.ViewModel.Chat.InputText = "send me";
            window.ChatInputBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.None,
            });
            Assert.Equal(string.Empty, window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task A_send_while_a_turn_is_in_flight_queues_instead_of_posting_a_concurrent_turn()
    {
        // #1074 seam: the composer never blocks. With a turn in flight (IsSending), a bare Enter must
        // QUEUE the message — visible and removable — not post a second concurrent turn. Discriminator:
        // the queue was empty before, the composer clears after (the message went somewhere, not nowhere).
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-queue-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-queue-test", roomDirectoryPath: roomDirectory, adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(ShellSection.Chat, window.ViewModel.CurrentSection);

            // A turn is already running.
            window.ViewModel.Chat.IsSending = true;
            Assert.False(window.ViewModel.Chat.HasQueuedMessages);

            window.ViewModel.Chat.InputText = "one more thing";
            window.ChatInputBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.None,
            });

            Assert.True(window.ViewModel.Chat.HasQueuedMessages);
            Assert.Equal("one more thing", Assert.Single(window.ViewModel.Chat.QueuedMessages).Text);
            // IsSending is untouched (no new turn was dispatched by this send) and the composer cleared.
            Assert.True(window.ViewModel.Chat.IsSending);
            Assert.Equal(string.Empty, window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task A_send_while_the_queue_is_non_empty_enqueues_behind_it_even_with_nothing_in_flight()
    {
        // #1074 finding #3 (FIFO): SendChatMessageAsync gates on IsSending || HasQueuedMessages, so a
        // new typed message can't jump ahead of already-queued ones when nothing is in flight (the
        // paused-after-a-failed-drain state). Without the HasQueuedMessages half it would post ahead.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-shell-fifo-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId: "sess-fifo-test", roomDirectoryPath: roomDirectory, adapter: "claude",
                cancellationToken: TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            // A message is already queued, and nothing is in flight (IsSending false by default).
            window.ViewModel.Chat.EnqueueMessage("first");
            Assert.False(window.ViewModel.Chat.IsSending);

            window.ViewModel.Chat.InputText = "second";
            window.ChatInputBox.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                KeyModifiers = KeyModifiers.None,
            });

            // "second" joined the queue behind "first" rather than posting ahead of it.
            Assert.Collection(window.ViewModel.Chat.QueuedMessages,
                m => Assert.Equal("first", m.Text),
                m => Assert.Equal("second", m.Text));
            Assert.Equal(string.Empty, window.ViewModel.Chat.InputText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #334/#1072: the paused-step derivation the switcher's filter renders comes from
    // HomeViewModel.BuildInboxItem (status wording + preview) and RoomCardViewModel.DeriveStatus (row
    // status). These exercise that derivation directly, from the paused-room fixtures — the narrowing
    // and empty state itself live in RoomsViewModelTests.
    [Fact]
    public async Task A_paused_review_derives_a_needs_you_status_and_an_inbox_item_with_its_preview()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(
            "The plan looks solid overall.", TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission, isFlowLockHeld: true);
            Assert.Equal(RoomCardStatus.NeedsYou, status);
            Assert.Equal("Waiting for your review", statusText);

            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);
            var item = HomeViewModel.BuildInboxItem(roomDirectory, projection, pausedStep, _ => Task.CompletedTask);
            Assert.Equal("critic", item.StepName);
            Assert.Equal("Waiting for your review — review.md ready", item.StatusText);
            Assert.True(item.HasPreview);
            Assert.Equal("The plan looks solid overall.", item.PreviewText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1191: an execution that declared an output and did not write it previews NOTHING. Why the
    /// obvious fallback is wrong is written on <c>HomeViewModel.BuildInboxItem</c>'s selection;
    /// this pins that the silence is deliberate, so a later reader does not "fix" it back.
    /// </summary>
    [Fact]
    public async Task A_pause_whose_declared_output_is_missing_previews_nothing_rather_than_whatever_else_is_there()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync("unused", TestContext.Current.CancellationToken);
        try
        {
            // EnsureDeleted, not Delete: this is the arrangement, not cleanup — a swallowed failure
            // here would leave the file in place and the test would pass on the wrong state.
            FileCleanup.EnsureDeleted(Path.Combine(roomDirectory, "artifacts", "execution_c-1", "review.md"));

            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);
            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);
            var item = HomeViewModel.BuildInboxItem(roomDirectory, projection, pausedStep, _ => Task.CompletedTask);

            Assert.False(item.HasPreview);
            Assert.Equal(string.Empty, item.PreviewText);
            // Still an honest item, and it names no file it cannot show.
            Assert.Equal("Waiting for your review", item.StatusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_needs_input_pause_derives_a_reply_not_a_review()
    {
        // #334: the exact bug — a settled chat turn showed "Waiting for your review" and a [Review]
        // button. A NeedsInput pause must read as "your turn to reply" wherever the derivation renders.
        var roomDirectory = await CreateNeedsInputRoomDirectoryAsync("ok", TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission, isFlowLockHeld: true);
            Assert.Equal(RoomCardStatus.NeedsYou, status);
            Assert.Equal("Waiting for your reply", statusText);

            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);
            var item = HomeViewModel.BuildInboxItem(roomDirectory, projection, pausedStep, _ => Task.CompletedTask);
            Assert.Equal(PausePointKind.NeedsInput, item.Kind);
            Assert.Equal("Waiting for your reply", item.StatusText);
            Assert.Equal("Reply", item.ActionLabel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_paused_step_item_opens_the_room_it_points_at()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(
            "Needs another pass at the error handling.", TestContext.Current.CancellationToken);
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);
            var pausedStep = projection.State.Steps.Single(s => s.Status == StepStatus.Paused);

            string? opened = null;
            var item = HomeViewModel.BuildInboxItem(
                roomDirectory, projection, pausedStep, path => { opened = path; return Task.CompletedTask; });
            await item.ReviewCommand.ExecuteAsync(null);

            // Review opens the room the item points at — on the switcher that selects its row, whose
            // existing open path renders the gate inline (#1072/#336).
            Assert.Equal(roomDirectory, opened);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>M24 Phase 5 (#278): the sixth nav destination — a fleet management view distinct from Home's capped recents cards.</summary>
    [AvaloniaFact]
    public async Task NavigatingToRooms_showsTheRoomsSectionAndHidesEverythingElse()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        window.ViewModel.CurrentSection = ShellSection.Rooms;

        Assert.True(window.ViewModel.IsRoomsVisible);
        Assert.False(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsChatVisible);
        Assert.False(window.ViewModel.IsSettingsVisible);
    }

    /// <summary>
    /// #1068: Settings is the former Remote destination. Navigating to it shows the Settings section
    /// (hiding everything else), and the pairing UI that used to be its own destination is folded in —
    /// reachable through the RemoteView embedded in SettingsView, so the fold didn't drop it.
    /// </summary>
    [AvaloniaFact]
    public async Task NavigatingToSettings_showsSettingsAndFoldsInTheRemotePairingSurface()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        window.ViewModel.CurrentSection = ShellSection.Settings;

        Assert.True(window.ViewModel.IsSettingsVisible);
        Assert.False(window.ViewModel.IsHomeVisible);
        Assert.False(window.ViewModel.IsChatVisible);
        Assert.False(window.ViewModel.IsRoomsVisible);

        // The pairing controls survive the fold: they now resolve through SettingsView, not a
        // standalone Remote view. A null here means the fold dropped the surface.
        Assert.NotNull(window.RemoteToggleButton);
        Assert.NotNull(window.ThemeSystemButton);
    }

    /// <summary>
    /// #1068: choosing a theme in Settings → Appearance applies it to the running app, marks that
    /// choice selected on the toggle, and persists it so the next launch opens in it. Starts from the
    /// System default so the assertions discriminate a real change from a no-op.
    /// </summary>
    [AvaloniaFact]
    public async Task Choosing_a_theme_applies_it_marks_it_selected_and_persists_it()
    {
        var configFilePath = NewConfigFilePath();
        var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
        await window.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(window.ViewModel.IsThemeSystem);

        var original = Avalonia.Application.Current!.RequestedThemeVariant;
        try
        {
            await window.ChooseThemeAsync(ThemeNames.Dark);

            Assert.Equal(ThemeNames.Dark, window.ViewModel.ThemePreference);
            Assert.True(window.ViewModel.IsThemeDark);
            Assert.False(window.ViewModel.IsThemeSystem);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);
            Assert.Equal(
                ThemeNames.Dark,
                await new LocalUiConfigurationStore(configFilePath).LoadThemeAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            // Theme is app-global; don't leak the change into other tests.
            Avalonia.Application.Current!.RequestedThemeVariant = original;
        }
    }

    // #1071 retired the Home recents cards, and with them the greyed "unavailable" rendering for a
    // stale local recent — that was a Home-cards feature over LocalUiConfiguration recents, and the
    // RoomCardStatus.Unavailable derivation had no other producer. The switcher lists the daemon fleet,
    // where a directory with no snapshot reads "Not yet run" (RoomProjectionLoader.LoadFleetStatusAsync,
    // unchanged); an explicit per-room "unavailable" state on the switcher is separate design scope
    // (0018's unavailable band is host-reachability, not per-room deletion). So there is no Home-side
    // behaviour left to assert, and this test is removed rather than left as a misleading skip.

    /// <summary>
    /// docs/design/02-screens.md:58 — the rooms list header reads "Rooms + New", "+ New" starting a
    /// room. Guards that the switcher header actually carries a "+ New" affordance (the invisible-but-
    /// green failure class: a wired handler with no button to fire it), and that restructuring the
    /// header to add it did not drop the existing refresh affordance beside the "Rooms" label.
    /// </summary>
    [AvaloniaFact]
    public void The_switcher_header_carries_a_plus_new_affordance_beside_the_rooms_label()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        var newButton = window.FindControl<Button>("SwitcherNewButton");
        Assert.NotNull(newButton);
        var label = Assert.IsType<TextBlock>(newButton.Content);
        Assert.Equal("+ New", label.Text);

        // The refresh affordance the header already had must survive the "+ New" restructure.
        Assert.NotNull(window.FindControl<Button>("SwitcherRefreshButton"));
    }

    /// <summary>
    /// #1062: Home used to carry a "Start from template" button beside its "Rooms" heading that
    /// duplicated the empty-state card's identical button (both fired OnStartTemplateClick), so the
    /// empty state showed two stacked. With the switcher's "+ New" now the always-available new-room
    /// affordance (#1061), that header button is gone — the empty-state card's one remains.
    /// </summary>
    [AvaloniaFact]
    public void Home_no_longer_carries_a_duplicate_start_from_template_button_beside_its_heading()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        Assert.Null(window.FindViewControl<Button>("HeaderStartTemplateButton"));
        Assert.NotNull(window.FindViewControl<Button>("StartTemplateButton"));
    }
}
