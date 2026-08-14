using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M15 Phase 1 (issue #137): the mutation seam proven end to end through the real
/// <see cref="MainWindow.RunAsync"/> — in-process reuse of <c>Aer.Cli.RunCommand.ExecuteAsync</c>,
/// the same call <c>aer run</c> makes, driven through a deterministic shell-stub
/// <see cref="IWorkerAdapter"/> (never a live vendor CLI) so this is CI-safe on every OS. Covers
/// both halves of the phase's "Run" action: starting a fresh task from a template + bindings file,
/// and resuming an already-bound room directory that needs no template at all.
/// </summary>
public class MainWindowRunTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-run-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    [AvaloniaFact]
    public async Task RunAsync_starts_a_fresh_task_from_a_template_and_bindings_file_and_renders_it_to_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-run-fresh-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            var runStatusText = window.FindViewControl<TextBlock>("RunStatusText")!;
            var stepsPanel = window.FindViewControl<StackPanel>("StepsPanel")!;

            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Equal(string.Empty, runStatusText.Text);
            Assert.Equal(
                ["architect: Succeeded", "critic: Succeeded", "publisher: Succeeded"],
                stepsPanel.Children.OfType<TextBlock>().Select(block => block.Text).ToList());

            // The UI never blocked its own thread on the pump (issue #137): RunAsync only returns
            // once the pump has already reached its fixed point (M14 Phase 2's terminal-status stop
            // condition). Asks the flow question directly rather than reading it off the poller,
            // which since #1216 keeps watching a settled room for room-level facts.
            Assert.False(window.IsRoomFlowStillChanging);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task RunAsync_records_the_room_directory_and_remembers_the_bindings_and_template_paths()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-run-remember-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var configurationStore = new LocalUiConfigurationStore(NewConfigFilePath());
            var window = new MainWindow(configurationStore, Adapters);

            await window.RunAsync(roomDirectory, workflowFilePath, bindingsFilePath, TestContext.Current.CancellationToken);

            var recents = await configurationStore.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFullPath(roomDirectory), Assert.Single(recents));
            Assert.Equal(
                Path.GetFullPath(bindingsFilePath),
                await configurationStore.LoadLastBindingsFilePathAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                Path.GetFullPath(workflowFilePath),
                await configurationStore.LoadLastWorkflowTemplateFilePathAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task RunAsync_resumes_an_already_bound_room_directory_with_no_workflow_template()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-run-resume-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);

            // Bound and pumped once directly through MutationInterface — standing in for "a task
            // aer run already started," so this test's own RunAsync call below is proven to resume
            // it rather than to have started it fresh.
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(
                await WriteThreeStepWorkflowAsync(testRoot), TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(
                snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            // No workflow template path given: a resume never needs one (RunOptions.WorkflowFilePath's
            // own remarks, issue #137) since the snapshot is already bound.
            await window.RunAsync(roomDirectory, workflowTemplateFilePath: null, bindingsFilePath, TestContext.Current.CancellationToken);

            var statusText = window.FindViewControl<TextBlock>("StatusText")!;
            var runStatusText = window.FindViewControl<TextBlock>("RunStatusText")!;
            Assert.Equal("Workflow status: Terminal", statusText.Text);
            Assert.Equal(string.Empty, runStatusText.Text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [AvaloniaFact]
    public async Task RunAsync_renders_a_missing_template_on_a_fresh_start_as_an_in_window_message_not_a_crash()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ui-run-missing-template-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()), Adapters);

            // A fresh room directory (no snapshot.json yet) with no template given at all.
            await window.RunAsync(roomDirectory, workflowTemplateFilePath: null, bindingsFilePath, TestContext.Current.CancellationToken);

            var runStatusText = window.FindViewControl<TextBlock>("RunStatusText")!;
            Assert.Contains(roomDirectory, runStatusText.Text);
            // The surface is usable again after the failure. Asserted on the flag the retired Run
            // button's IsEnabled was bound to (#1215), which is what the claim was always about.
            Assert.False(window.ViewModel.IsMutationInFlight);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
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
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(30)),
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
}
