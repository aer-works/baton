using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using static Aer.Ui.Tests.TestSupport.FlowLogWaiter;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 4 (issue #140): §7's Cancel, on both grains M10/M12 built, proven end to end through the
/// real <see cref="MainWindow"/> — targeted cancel of one running execution (both the locally-hosted,
/// in-process-registry delivery and the not-locally-hosted, fresh-mutation-call delivery), the Stop
/// button's host stop, and window-close semantics with a pump still in flight. Real dispatch through
/// <see cref="ShellCommandWorkerAdapter"/> and the real Core binding — no stub dispatcher — so a
/// genuinely-running execution is awaited by polling the log for <c>CoreEvent.ExecutionStarted</c>
/// (<see cref="FlowLogWaiter"/>), never a fixed delay.
/// </summary>
public class MainWindowCancelAndStopTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    // 10 seconds, matching Aer.Flow.Tests.TestSupport.ShellWorkerCommands.SleepThenWriteFile's own
    // convention for the same reason its remarks give: enough real wall-clock headroom that a real
    // OS-level process kill reliably lands before the sleep would have finished naturally anyway,
    // even under a loaded CI sandbox — a shorter sleep here was observed to flake under load.
    private static readonly TimeSpan SleepDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-cancel-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    // #350: same reconciliation as PausedSteps (see MainWindowDecisionTests' live-refresh-tick test) —
    // RoomClient.RebuildRunningExecutions used to Clear()+re-add every tick, so a running execution's
    // container (and the operator's hover/focus on its Cancel button) was torn down and rebuilt twice
    // a second even though nothing about it had changed.
    [AvaloniaFact]
    public async Task A_second_refresh_against_an_unchanged_running_execution_keeps_the_same_instance()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-cancel-tick-reconcile-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var workflowFilePath = await WriteSleepingWorkflowAsync(testRoot, "worker", maxAttempts: 5);
            var bindingsFilePath = await WriteSleepingBindingsAsync(testRoot, "worker");
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            var runTask = window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            await WaitForCoreExecutionStartedAsync(logPath);
            await window.RefreshAsync(TestContext.Current.CancellationToken);

            var running = Assert.Single(window.ViewModel.RunningExecutions);

            await window.RefreshAsync(TestContext.Current.CancellationToken);

            var runningAfterSecondRefresh = Assert.Single(window.ViewModel.RunningExecutions);
            Assert.Same(running, runningAfterSecondRefresh);

            await running.CancelCommand.ExecuteAsync(null);
            await AwaitWithTimeoutAsync(runTask, TestTimeout);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Targeted_cancel_of_a_locally_hosted_execution_is_delivered_via_the_retained_registry()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-cancel-local-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var workflowFilePath = await WriteSleepingWorkflowAsync(testRoot, "worker", maxAttempts: 5);
            var bindingsFilePath = await WriteSleepingBindingsAsync(testRoot, "worker");
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            var runTask = window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            await WaitForCoreExecutionStartedAsync(logPath);
            await window.RefreshAsync(TestContext.Current.CancellationToken);

            var running = Assert.Single(window.ViewModel.RunningExecutions);
            Assert.Equal(new StepId("worker"), running.StepId);
            Assert.True(running.IsLocallyHosted);
            Assert.False(running.CancellationRequested);
            Assert.True(running.CancelCommand.CanExecute(null));

            await running.CancelCommand.ExecuteAsync(null);

            await AwaitWithTimeoutAsync(runTask, TestTimeout);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Equal(["worker: Cancelled"], stepsPanel.Children.OfType<TextBlock>().Select(b => b.Text).ToList());

            // Never went through a fresh CancelCommand mutation call — delivered in-process, so no
            // in-window cancel message and no retry despite the step's remaining budget (§10).
            Assert.Equal(string.Empty, window.ViewModel.CancelStatusText);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>(), e => e.Request.StepId == new StepId("worker"));

            var requestIndex = events.ToList().FindIndex(e => e is FlowEvent.CancellationRequested);
            var outcomeIndex = events.ToList().FindIndex(e => e is FlowEvent.ExecutionCancelled);
            Assert.True(requestIndex >= 0 && outcomeIndex > requestIndex);

            Assert.Empty(window.ViewModel.RunningExecutions);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Targeted_cancel_of_an_execution_not_hosted_by_this_window_goes_through_a_fresh_mutation_call()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-cancel-external-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var workflowFilePath = await WriteSleepingWorkflowAsync(testRoot, "orphan", maxAttempts: 1);
            var bindingsFilePath = await WriteSleepingBindingsAsync(testRoot, "orphan");

            // Bound directly (standing in for a task `aer run` started on some other, now-gone
            // process) — never pumped through this window at all, so this window has no
            // InFlightExecutionRegistry entry for anything in it.
            Directory.CreateDirectory(roomDirectory);
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(workflowFilePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(
                snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            // A fabricated in-flight dispatch with no CoreEvent history at all — standing in for "some
            // other process admitted this execution and never got to record anything about it," the
            // "not hosted by this window" case issue #140's own open question names: no
            // InFlightExecutionRegistry anywhere in this process has ever heard of it.
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var request = new ExecutionRequest(
                executionId, new WorkflowId("sleeping-single-step"), new StepId("orphan"), "orphan",
                Inputs: [], Outputs: ["out"], Timeout: TimeSpan.FromSeconds(30),
                Environment: [], UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
            await using (var seedWriter = new FlowEventLogWriter(logPath))
            {
                await seedWriter.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            }

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);
            await window.OpenAsync(roomDirectory, TestContext.Current.CancellationToken);

            // Never asked for by RunAsync in this test (only OpenAsync was called, standing in for
            // re-opening a task this window never itself ran) — the same "ask, don't infer" bindings
            // state CancelExecutionAsync reads from (Phase 1's decision of record) needs a value
            // before it can wrap a fresh CancelCommand call.
            window.ViewModel.BindingsFilePath = bindingsFilePath;

            var running = Assert.Single(window.ViewModel.RunningExecutions);
            Assert.Equal(new StepId("orphan"), running.StepId);
            Assert.Equal(executionId, running.ExecutionId);
            Assert.False(running.IsLocallyHosted);

            await running.CancelCommand.ExecuteAsync(null);

            Assert.Equal(string.Empty, window.ViewModel.CancelStatusText);
            Assert.Empty(window.ViewModel.RunningExecutions);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == executionId);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId == executionId);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Equal(["orphan: Cancelled"], stepsPanel.Children.OfType<TextBlock>().Select(b => b.Text).ToList());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task StopAsync_cancels_every_execution_this_window_currently_has_in_flight()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-hoststop-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var workflowFilePath = await WriteTwoIndependentSleepingStepsWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoIndependentSleepingStepsBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            var runTask = window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            await WaitForConditionAsync(logPath, s => s.CoreEvents.OfType<CoreEvent.ExecutionStarted>().Count() >= 2);

            await window.StopAsync();

            await AwaitWithTimeoutAsync(runTask, TestTimeout);

            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;
            Assert.Equal(
                ["one: Cancelled", "two: Cancelled"],
                stepsPanel.Children.OfType<TextBlock>().Select(b => b.Text).OrderBy(t => t).ToList());

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.Count(e => e is FlowEvent.CancellationRequested));
            Assert.Equal(2, events.Count(e => e is FlowEvent.ExecutionCancelled));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task Closing_the_window_with_a_pump_in_flight_triggers_a_host_stop_and_the_window_closes_once_the_pump_settles()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-close-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var workflowFilePath = await WriteSleepingWorkflowAsync(testRoot, "worker", maxAttempts: 1);
            var bindingsFilePath = await WriteSleepingBindingsAsync(testRoot, "worker");
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            var runTask = window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);
            await WaitForCoreExecutionStartedAsync(logPath);

            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();

            // Deferred, not immediate: the first Close() is turned into a host stop, and the window
            // only actually closes once that pump has reached its fixed point (issue #140).
            Assert.False(closed);

            await AwaitWithTimeoutAsync(runTask, TestTimeout);
            await WaitUntilAsync(() => closed, TestTimeout);

            Assert.True(closed);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteSleepingWorkflowAsync(string directory, string stepName, int maxAttempts)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("sleeping-single-step"),
            1,
            [new WorkflowStepDefinition(new StepId(stepName), stepName, [], ["out"], [], new RetryPolicy(maxAttempts))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSleepingBindingsAsync(string directory, string stepName)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            [stepName] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract(stepName, [], [new ProducedOutput("out")], []),
                SleepThenWriteFileCommand("out", "should-not-be-reached"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoIndependentSleepingStepsWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("two-independent-sleeping-steps"),
            1,
            [
                new WorkflowStepDefinition(new StepId("one"), "one", [], ["out"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("two"), "two", [], ["out"], [], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoIndependentSleepingStepsBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["one"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("one", [], [new ProducedOutput("out")], []),
                SleepThenWriteFileCommand("out", "should-not-be-reached"), TimeSpan.FromSeconds(30)),
            ["two"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("two", [], [new ProducedOutput("out")], []),
                SleepThenWriteFileCommand("out", "should-not-be-reached"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteThreeStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("three-step-linear"),
            1,
            [
                new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("critic"), "critic", ["plan"], ["review"], [new StepId("architect")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("publisher"), "publisher", ["review"], ["summary"], [new StepId("critic")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteThreeStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"), TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"), TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";

    /// <summary>
    /// Sleeps for at least <see cref="SleepDuration"/> before writing <paramref name="outputName"/> and
    /// exiting 0 — enough real wall-clock time for a test to observe <c>CoreEvent.ExecutionStarted</c>
    /// before acting on it, mirroring <c>Aer.Flow.Tests.TestSupport.ShellWorkerCommands.SleepThenWriteFile</c>'s
    /// own cross-platform sleep primitive (`ping -n` on Windows, never `timeout` — see that method's
    /// own remarks for why).
    /// </summary>
    private static string SleepThenWriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"ping -n {(int)SleepDuration.TotalSeconds + 1} 127.0.0.1 >nul & echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"sleep {SleepDuration.TotalSeconds} && echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";
}
