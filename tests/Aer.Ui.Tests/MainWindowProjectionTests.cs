using Aer.Ui.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M14 Phase 2 (issue #119): the full read-model surface plus change observation, driven through
/// the real <see cref="MainWindow"/> exactly like <see cref="MainWindowTests"/> already does for
/// Phase 1's rendering, but building room directories directly from hand-written
/// <see cref="FlowEvent"/>s (matching <c>Aer.Flow.Tests.Projection.StateProjectorTests</c>'
/// convention) rather than driving a full <c>MutationInterface</c> pump — the point here is what the
/// UI renders from a given event history, not re-proving dispatch behavior Aer.Flow's own tests
/// already cover. Every assertion drives <see cref="MainWindow.OpenAsync"/>/<c>RefreshAsync</c>
/// directly rather than simulating a button click, the same reason <see cref="MainWindow.LoadAsync"/>
/// is public and awaitable (issue #118): deterministic, no dispatcher-timer pumping.
/// </summary>
public class MainWindowProjectionTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static WorkflowDefinitionSnapshot TwoStepSnapshot() => SnapshotBinder.Bind(new WorkflowDefinition(
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: new PausePoint(SupersedeTargets: [Architect])),
        ]));

    /// <summary>An ordinary process dispatch — always a real (non-null) <c>Timeout</c>.</summary>
    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId? stepId, string worker = "worker")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    /// <summary>A non-process dispatch (spec §17.3) — always a <c>null</c> <c>Timeout</c>; a distinct helper, not an optional parameter on <see cref="MakeRequest"/>, so a call can never get the wrong one via a defaulted argument.</summary>
    private static ExecutionRequest MakeNonProcessRequest(ExecutionId executionId, StepId? stepId, string worker = "human")
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            worker,
            Inputs: [],
            Outputs: [],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-window-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static async Task<string> CreateRoomDirectoryAsync(
        WorkflowDefinitionSnapshot snapshot, IEnumerable<FlowEvent> events, CancellationToken cancellationToken)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-history-{Guid.NewGuid():N}");
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

    private static List<string> TextsOf(StackPanel panel) => panel.Children.OfType<TextBlock>().Select(block => block.Text!).ToList();

    [AvaloniaFact]
    public async Task LoadAsync_renders_full_attempt_history_and_retry_state_in_the_history_panel()
    {
        var snapshot = TwoStepSnapshot();
        var firstArchitectAttempt = new ExecutionId("a-1");
        var secondArchitectAttempt = new ExecutionId("a-2");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(firstArchitectAttempt, Architect)),
                new FlowEvent.ExecutionFailed(firstArchitectAttempt, FailureClassification.Retryable),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(secondArchitectAttempt, Architect)),
                new FlowEvent.ExecutionSucceeded(secondArchitectAttempt),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var historyPanel = window.FindViewControl<StackPanel>("HistoryPanel")!;
            Assert.Equal(
                [
                    "architect attempt 1/2: a-1 -> Failed (Retryable)",
                    "architect attempt 2/2: a-2 -> Succeeded",
                    "architect: consecutive failures=0",
                    "critic attempt 1/1: c-1 -> Succeeded",
                    "critic: consecutive failures=0",
                ],
                TextsOf(historyPanel));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task LoadAsync_renders_pause_state_supersede_targets_and_unresolved_decisions()
    {
        var snapshot = TwoStepSnapshot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var decisionId = new DecisionId("decision-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
                new FlowEvent.ExecutionSucceeded(criticExecutionId),
                new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
                new FlowEvent.ExternalDecisionRecorded(decisionId, criticExecutionId, DecisionType.RetryWithRevision, null, null),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var historyPanel = window.FindViewControl<StackPanel>("HistoryPanel")!;
            var historyTexts = TextsOf(historyPanel);
            Assert.Contains(
                "critic: consecutive failures=0, paused (underlying outcome=Succeeded), supersede targets=[architect]",
                historyTexts);

            var decisionsPanel = window.FindViewControl<StackPanel>("DecisionsPanel")!;
            Assert.Equal(
                ["decision-1: RetryWithRevision on c-1 (unresolved)"],
                TextsOf(decisionsPanel));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task LoadAsync_renders_settled_supplementary_and_human_executions()
    {
        var snapshot = TwoStepSnapshot();
        var humanExecutionId = new ExecutionId("supplement-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeNonProcessRequest(humanExecutionId, null)),
                new FlowEvent.ExecutionSucceeded(humanExecutionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var supplementaryPanel = window.FindViewControl<StackPanel>("SupplementaryPanel")!;
            Assert.Equal(["supplement-1 (human): Succeeded [non-process]"], TextsOf(supplementaryPanel));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task OpenAsync_records_the_opened_directory_as_a_recent()
    {
        var snapshot = TwoStepSnapshot();
        var executionId = new ExecutionId("a-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId, Architect)),
                new FlowEvent.ExecutionSucceeded(executionId),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            // #1071: the recents cards retired to the permanent switcher; opening a room still records
            // it, so the ▤ front door's first-run empty state clears and the room box shows the opened
            // directory. (The switcher's own row content is daemon-driven, covered by the fleet tests.)
            Assert.False(window.ViewModel.Home.HasNoRooms);
            Assert.Equal(roomDirectory, window.FindViewControl<TextBox>("RoomDirectoryPathBox")!.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task OpenAsync_does_not_record_a_directory_that_failed_to_load()
    {
        var notARoomDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notARoomDirectory);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(notARoomDirectory, TestContext.Current.CancellationToken);

            // #1071: a directory that failed to load is not recorded, so the first-run empty state stays.
            Assert.True(window.ViewModel.Home.HasNoRooms);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(notARoomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task InitializeAsync_populates_the_recents_panel_from_local_configuration_at_startup()
    {
        var configFilePath = NewConfigFilePath();
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-window-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var configurationStore = new LocalUiConfigurationStore(configFilePath);
            await configurationStore.RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(configFilePath));
            await window.InitializeAsync(TestContext.Current.CancellationToken);

            // #1071: a recorded room means the first-run empty state is cleared at startup (the recents
            // cards this used to assert moved to the switcher).
            Assert.False(window.ViewModel.Home.HasNoRooms);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task RefreshAsync_picks_up_events_appended_after_the_initial_open_while_still_running()
    {
        var snapshot = TwoStepSnapshot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(["architect: Running", "critic: Running"], TextsOf(stepsPanel));
            Assert.True(window.IsLiveRefreshTimerEnabled);

            await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(architectExecutionId), TestContext.Current.CancellationToken);
            }

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["architect: Succeeded", "critic: Running"], TextsOf(stepsPanel));
            // Critic is still running — nothing further to observe yet, so polling continues.
            Assert.True(window.IsLiveRefreshTimerEnabled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Reaching terminal used to STOP the poller outright. #1216 ended that — a room's workflow
    /// switch is a <c>room.jsonl</c> fact that changes precisely when the flow cannot, and a
    /// terminal room is the one most likely to be switched, since a room with work in flight is
    /// refused. What survives is the saving that stop was actually for: a settled room must stop
    /// paying for re-projection, which re-reads every execution's artifact directory. See
    /// <c>MainWindow.UpdateLiveRefreshTimer</c>'s remarks, and
    /// <c>NavigationShellTests.A_finished_room_still_observes_its_workflow_being_switched_by_someone_else</c>
    /// for the behaviour that replaced it.
    /// </summary>
    [AvaloniaFact]
    public async Task Live_refresh_stops_re_projecting_once_the_workflow_reaches_a_terminal_state()
    {
        var snapshot = TwoStepSnapshot();
        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(architectExecutionId, Architect)),
                new FlowEvent.ExecutionSucceeded(architectExecutionId),
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(criticExecutionId, Critic)),
            ],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.True(window.IsLiveRefreshTimerEnabled);

            await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(criticExecutionId), TestContext.Current.CancellationToken);
            }

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal("Workflow status: Terminal", window.FindViewControl<TextBlock>("StatusText")!.Text);

            // Still watching — but a tick over an unchanged journal costs a stat, not a projection.
            Assert.True(window.IsLiveRefreshTimerEnabled);
            var renderedBefore = window.RenderedProjectionCountForTests;
            await window.OnLiveRefreshTickAsync(TestContext.Current.CancellationToken);
            await window.OnLiveRefreshTickAsync(TestContext.Current.CancellationToken);
            Assert.Equal(renderedBefore, window.RenderedProjectionCountForTests);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task RefreshAsync_before_anything_has_been_opened_is_a_no_op()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));

        await window.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("No room directory loaded.", window.FindViewControl<TextBlock>("StatusText")!.Text);
    }

    [AvaloniaFact]
    public async Task OpenAsync_when_room_directory_lacks_workflow_path_does_not_populate_state_with_bare_template_id()
    {
        var snapshot = TwoStepSnapshot();
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [],
            TestContext.Current.CancellationToken);
        try
        {
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.NotEqual(snapshot.WorkflowTemplateId.Value, window.ViewModel.WorkflowTemplateFilePath);
            Assert.Equal(string.Empty, window.ViewModel.WorkflowTemplateFilePath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [AvaloniaFact]
    public async Task OpenAsync_when_workflow_path_is_missing_falls_back_to_the_room_directorys_own_workflow_json()
    {
        var snapshot = TwoStepSnapshot();
        var roomDirectory = await CreateRoomDirectoryAsync(
            snapshot,
            [],
            TestContext.Current.CancellationToken);
        var workflowJsonPath = Path.Combine(roomDirectory, "workflow.json");
        try
        {
            await File.WriteAllTextAsync(workflowJsonPath, "{}", TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(workflowJsonPath, window.ViewModel.WorkflowTemplateFilePath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
