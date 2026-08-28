using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
// Aliased, not `using Aer.RoomSession;`: this file also uses the global-using-supplied
// Aer.Ui.Core.RoomClient, and Aer.RoomSession now declares its own RoomClient -- a blanket using
// would make every bare RoomClient reference ambiguous.
using LocalUiConfigurationStore = Aer.RoomSession.LocalUiConfigurationStore;
using RoomCardStatus = Aer.RoomSession.RoomCardStatus;
using RoomFleetItem = Aer.RoomSession.RoomFleetItem;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 2 (issue #138): §7's Approve/Reject decisions proven end to end through the real
/// <see cref="MainWindow"/> — in-process reuse of <c>Aer.Cli.DecideCommand.ExecuteAsync</c>, driven
/// through a deterministic shell-stub <see cref="IWorkerAdapter"/> exactly like
/// <see cref="MainWindowRunTests"/>, so this is CI-safe on every OS. A task is first driven to
/// <see cref="WorkflowStatus.Paused"/> through the real <see cref="MainWindow.RunAsync"/> (Phase 1's
/// seam), then resolved through <see cref="PausedStepViewModel.ApproveCommand"/>/
/// <see cref="PausedStepViewModel.RejectCommand"/> — the same commands the bound "Approve"/"Reject"
/// buttons in <c>MainWindow.axaml</c> invoke.
/// </summary>
public class MainWindowDecisionTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-decide-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    [AvaloniaFact]
    public async Task Approve_resolves_the_pause_to_its_underlying_outcome_and_the_workflow_runs_to_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-approve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Paused", statusText.Text);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Equal(new StepId("a"), pausedStep.StepId);

            await pausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Empty(window.ViewModel.PausedSteps);
            Assert.Equal(string.Empty, window.ViewModel.DecisionStatusText);
            // The flow question, not the poller's — see MainWindow.IsRoomFlowStillChanging (#1216).
            Assert.False(window.IsRoomFlowStillChanging);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["a: Succeeded", "b: Succeeded"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    // #350: a live-refresh tick against an unchanged Paused workflow used to Clear()+re-add
    // PausedSteps unconditionally, tearing down every item's Avalonia container (killing hover/focus)
    // and silently wiping any operator-typed RevisionFilePath/SupplementaryWorker/SupplementaryOutputName
    // mid-entry. RoomClient.RebuildPausedSteps now reconciles by (StepId, ExecutionId) instead — an
    // unchanged pause point keeps its exact instance across the tick.
    [AvaloniaFact]
    public async Task A_live_refresh_tick_against_an_unchanged_pause_keeps_the_same_instance_and_its_typed_in_state()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-tick-reconcile-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);
            pausedStep.RevisionFilePath = "operator-typed-mid-entry.txt";

            await window.OnLiveRefreshTickAsync(TestContext.Current.CancellationToken);

            var pausedStepAfterTick = Assert.Single(window.ViewModel.PausedSteps);
            Assert.Same(pausedStep, pausedStepAfterTick);
            Assert.Equal("operator-typed-mid-entry.txt", pausedStepAfterTick.RevisionFilePath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Reject_projects_the_paused_step_terminally_failed_and_the_downstream_step_never_dispatches()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-reject-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);

            await pausedStep.RejectCommand.ExecuteAsync(null);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Empty(window.ViewModel.PausedSteps);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["a: Rejected", "b: Pending"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task An_invalid_decision_renders_as_an_in_window_message_not_a_crash_and_leaves_the_pause_intact()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-decide-invalid-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            // A bindings file naming an adapter this window's registry doesn't have makes
            // DecideCommand throw UnknownWorkerAdapterException before it ever reaches the mutation
            // interface — standing in for any AerFlowException a real decide call can surface (a
            // competing external pump's WorkflowLockedException included), which this phase's
            // decision surface must render as a message, never crash the window.
            var unresolvableBindingsFilePath = await WriteUnresolvableBindingsAsync(testRoot);
            window.ViewModel.BindingsFilePath = unresolvableBindingsFilePath;
            var pausedStep = Assert.Single(window.ViewModel.PausedSteps);

            await pausedStep.ApproveCommand.ExecuteAsync(null);

            Assert.NotEqual(string.Empty, window.ViewModel.DecisionStatusText);
            Assert.False(window.ViewModel.IsMutationInFlight);
            Assert.True(pausedStep.IsEnabled);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            Assert.Equal("Workflow status: Paused", statusText.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    // #1072: RoomClient calls Rooms.RetireInboxItem on decision resolution (see RoomsViewModel for what
    // that does). That call site is verified by review + build — there is no RoomClient/daemon-fleet
    // double to drive it end-to-end here, the same limit #1069 recorded; this pair verifies the retire
    // method itself, in both polarities.
    private static RoomFleetItem NeedsYouFleetItem(string roomPath, string name, int pausedSteps) =>
        new(roomPath, name, "Workflow", "Waiting for your review", pausedSteps, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Status: RoomCardStatus.NeedsYou);

    private static Aer.Ui.Core.InboxItemViewModel PausedStep(string roomPath, string name, string step, string exec) =>
        new(roomPath, name, step, "Waiting for your review", "Preview",
            Aer.Flow.Domain.PausePointKind.ReadyForReview, _ => Task.CompletedTask, exec);

    [Fact]
    public void Answering_a_gate_retires_its_paused_step_from_the_switcher_row()
    {
        var rooms = new Aer.Ui.Core.RoomsViewModel();
        var roomPath = Path.Combine(Path.GetTempPath(), "task-a");
        var row = rooms.AddTestItem(NeedsYouFleetItem(roomPath, "task-a", 1));
        row.PausedSteps.Add(PausedStep(roomPath, "task-a", "step-a", "exec-1"));

        rooms.RetireInboxItem(roomPath, new StepId("step-a"), new ExecutionId("exec-1"));

        Assert.Empty(row.PausedSteps);
        // Its only gate was answered, so the row is no longer needs-you *now* — the authoritative
        // count decremented, not left stale until the next projection push (review finding #2).
        Assert.False(row.HasPausedSteps);
    }

    [Fact]
    public void Retiring_a_gate_leaves_an_unmatched_paused_step_in_place()
    {
        // The polarity control: only the answered gate's item is retired, matched by room + step +
        // execution — a different step in the same room, and any step in another room, both survive.
        var rooms = new Aer.Ui.Core.RoomsViewModel();
        var roomPath1 = Path.Combine(Path.GetTempPath(), "task1");
        var roomPath2 = Path.Combine(Path.GetTempPath(), "task2");
        var row1 = rooms.AddTestItem(NeedsYouFleetItem(roomPath1, "Task 1", 2));
        var row2 = rooms.AddTestItem(NeedsYouFleetItem(roomPath2, "Task 2", 1));

        var item1 = PausedStep(roomPath1, "Task 1", "step-a", "exec-1");
        var item2 = PausedStep(roomPath1, "Task 1", "step-b", "exec-1");
        var item3 = PausedStep(roomPath2, "Task 2", "step-a", "exec-1");
        row1.PausedSteps.Add(item1);
        row1.PausedSteps.Add(item2);
        row2.PausedSteps.Add(item3);

        // Retire step-a of task 1 — its row loses that one item; step-b and task 2's step-a stay.
        rooms.RetireInboxItem(roomPath1, new StepId("step-a"), new ExecutionId("exec-1"));

        Assert.Equal(new[] { item2 }, row1.PausedSteps);
        Assert.Equal(new[] { item3 }, row2.PausedSteps);
        // Row 1 still has a second gate (started at 2), so it stays needs-you; row 2 is untouched.
        Assert.True(row1.HasPausedSteps);
        Assert.True(row2.HasPausedSteps);
    }

    [AvaloniaFact]
    public async Task Loading_a_locked_task_renders_waiting_on_lock_banner_with_holder_text()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-lock-banner-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var configStore = new LocalUiConfigurationStore(NewConfigFilePath());

            // Acquire lock in fixture before opening/loading
            using var lockGuard = Aer.Flow.Concurrency.ConcurrencyGuard.Acquire(roomDirectory, "Other Room (pid 777)");

            var window = new MainWindow(configStore, Adapters);
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.True(window.ViewModel.HasWaitingOnLockBanner);
            Assert.NotNull(window.ViewModel.WaitingOnLockBanner);
            Assert.Equal("Waiting on another process's lock", window.ViewModel.WaitingOnLockBanner.Title);
            // #1299: HolderText now carries a duration ("Held by X for Ns") the fixture cannot pin
            // to an exact second — StartsWith is the honest assertion for a live-computed elapsed time.
            Assert.StartsWith("Held by Other Room (pid 777) for", window.ViewModel.WaitingOnLockBanner.HolderText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Loading_an_unlocked_task_does_not_render_waiting_on_lock_banner()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-lock-banner-polarity-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var configStore = new LocalUiConfigurationStore(NewConfigFilePath());

            var window = new MainWindow(configStore, Adapters);
            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            Assert.False(window.ViewModel.HasWaitingOnLockBanner);
            Assert.Null(window.ViewModel.WaitingOnLockBanner);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteApprovalGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("approval-gate"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(
                    new StepId("b"), "b", ["out_a"], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteApprovalGateBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                CopyFirstInputCommand("out_b"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteUnresolvableBindingsAsync(string directory)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-out"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                CopyFirstInputCommand("out_b"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "unresolvable-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";
}
