using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

/// <summary>
/// <c>aer status --json</c>'s shape (#1356 point 1): one <see cref="WorkflowStatusView"/> object,
/// derived from the same <c>StateProjector.Project</c> result the human rendering uses, across the
/// three states an agent needs to tell apart without parsing prose — succeeded, failed, running.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class StatusJsonEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task A_succeeded_room_reports_state_Succeeded_with_step_states_and_output_paths()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-ok-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, WriteFileCommand("plan", "the-plan"));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var view = ParseSingleObject(stdout.ToString());
            Assert.Equal("Succeeded", view.State);
            var step = Assert.Single(view.Steps);
            Assert.Equal("solo", step.Id);
            Assert.Equal("Succeeded", step.State);
            Assert.NotNull(step.Execution);
            var outputPath = Assert.Single(view.Outputs);
            Assert.True(File.Exists(outputPath));
            Assert.Null(view.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_failed_room_reports_state_Failed_with_the_step_failure_reason_as_the_top_level_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-fail-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot, "solo");
            var bindingsFilePath = await WriteOneStepBindingsAsync(testRoot, "exit 1");
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            await StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);

            var view = ParseSingleObject(stdout.ToString());
            Assert.Equal("Failed", view.State);
            var step = Assert.Single(view.Steps);
            Assert.Equal("Failed", step.State);
            Assert.Empty(view.Outputs);
            Assert.NotNull(view.Error);
            Assert.Contains("non-zero code", view.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_running_room_reports_state_Running_with_the_finished_step_already_Succeeded()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-status-json-running-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoStepBindingsAsync(testRoot, SleepThenWriteCommand("out_b", seconds: 5));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                WorkflowStatusView? view = null;
                while (DateTime.UtcNow < deadline)
                {
                    if (Directory.Exists(roomDirectory))
                    {
                        using var stdout = new StringWriter();
                        try
                        {
                            await StatusCommand.ExecuteAsync(
                                new StatusOptions(roomDirectory, Json: true), stdout, TestContext.Current.CancellationToken);
                            var candidate = ParseSingleObject(stdout.ToString());
                            if (candidate.Steps.Any(s => s.Id == "b" && s.State == "Running"))
                            {
                                view = candidate;
                                break;
                            }
                        }
                        catch (SnapshotLoadException)
                        {
                            // Not persisted yet -- keep polling.
                        }
                    }

                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                Assert.NotNull(view);
                Assert.Equal("Running", view!.State);
                var stepA = view.Steps.Single(s => s.Id == "a");
                Assert.Equal("Succeeded", stepA.State);
                var stepB = view.Steps.Single(s => s.Id == "b");
                Assert.Equal("Running", stepB.State);
            }
            finally
            {
                await runTask;
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static WorkflowStatusView ParseSingleObject(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var singleLine = Assert.Single(lines);

        // Also proves #1356 point 1's "nothing else on stdout in json mode": one line, one object.
        var view = JsonSerializer.Deserialize<WorkflowStatusView>(singleLine);
        Assert.NotNull(view);
        return view!;
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory, string stepId)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step"), 1,
            [new WorkflowStepDefinition(new StepId(stepId), stepId, [], ["plan"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneStepBindingsAsync(string directory, string command)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("solo", [], [new ProducedOutput("plan")], []), command, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("two-step-running"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoStepBindingsAsync(string directory, string stepBCommand)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-done"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                stepBCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string SleepThenWriteCommand(string outputName, int seconds) => OperatingSystem.IsWindows()
        ? $"ping -n {seconds + 1} 127.0.0.1>nul & echo done>%AER_OUTPUT_DIR%\\{outputName}"
        : $"sleep {seconds}; echo done > \"$AER_OUTPUT_DIR/{outputName}\"";
}
