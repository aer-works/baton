using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>--wait</c> (#1356): the pump already blocks until Terminal-or-Paused on its own, so the only
/// observable difference this flag makes is what happens on Paused — see <see cref="RunOptions.Wait"/>'s
/// own doc for why. These tests pin both polarities: without the flag, a paused run returns
/// immediately; with it, the SAME call only returns once a separate <c>baton decide</c> process carries
/// the room to Terminal.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class RunWaitEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Without_wait_a_paused_run_returns_immediately()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-wait-control-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory, Wait: false);

            var result = await RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, result.State.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task With_wait_the_same_call_only_returns_once_a_separate_decide_call_reaches_Terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-wait-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteApprovalGateWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteApprovalGateBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory, Wait: true);

            var waitingRunTask = RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            // Give the waiting call every chance to (wrongly) return early on its own before the
            // decision lands -- if --wait were a no-op this would already be Paused by now.
            // wait-ok: in-process settle window proving --wait didn't return early, not an external wait.
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.False(waitingRunTask.IsCompleted, "--wait must not return at Paused.");

            var pausedExecutionId = await WaitForPausedExecutionIdAsync(roomDirectory, workflowFilePath, TestContext.Current.CancellationToken);
            var decideOptions = new DecideOptions(
                roomDirectory, pausedExecutionId, DecisionType.Resume, TargetStepId: null,
                SupplementaryExecutionId: null, bindingsFilePath);
            var decideResult = await DecideCommand.ExecuteAsync(decideOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, decideResult.State.Status);

            // wait-ok: safety ceiling on a task that should already be resolving post-decision, not a real external wait.
            var waitedResult = await waitingRunTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, waitedResult.State.Status);
            Assert.All(waitedResult.State.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WaitForPausedExecutionIdAsync(
        string roomDirectory, string workflowFilePath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(Path.Combine(roomDirectory, "snapshot.json")) && File.Exists(Path.Combine(roomDirectory, "flow.jsonl")))
            {
                using var stdout = new StringWriter();
                try
                {
                    await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Json: true), stdout, cancellationToken);
                    var view = JsonSerializer.Deserialize<WorkflowStatusView>(stdout.ToString());
                    var step = view?.Steps.SingleOrDefault(s => s.Id == "a");
                    if (step is { State: "Paused", Execution: not null })
                    {
                        return step.Execution;
                    }
                }
                catch (SnapshotLoadException)
                {
                    // Not persisted yet -- keep polling.
                }
            }

            // wait-ok: local status-poll cadence while waiting for step 'a' to reach Paused; capped by the 10s deadline above.
            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"Step 'a' in '{roomDirectory}' never reached Paused.");
    }

    private static async Task<string> WriteApprovalGateWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("wait-approval-gate"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([])),
                new WorkflowStepDefinition(new StepId("b"), "b", ["out_a"], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
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
                WriteFileCommand("out_a", "a-done"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                WriteFileCommand("out_b", "b-done"), TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$BATON_OUTPUT_DIR/{outputName}\"";
}
