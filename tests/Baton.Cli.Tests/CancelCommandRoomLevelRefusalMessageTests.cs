using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1607 review findings F4/F5: <see cref="RunningExecutionResolverTests"/> pins the resolver's own
/// predicate, but nothing previously asserted on the CLI-side refusal text
/// <see cref="CancelCommand"/>'s <c>ResolveRunningExecutionAsync</c> actually throws for the
/// zero-candidate and ambiguous-candidate room-level cases — collapsing either message back to its
/// pre-#1607 wording would have passed every other test in this project unnoticed.
/// </summary>
public class CancelCommandRoomLevelRefusalMessageTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Bare_cancel_against_a_fully_terminal_room_names_zero_candidates_and_points_at_status()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneQuickStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneQuickStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: null, bindingsFilePath);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("no currently-Running or", ex.Message, StringComparison.Ordinal);
            Assert.Contains("quota-parked step to target", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("--execution", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("baton status", ex.TryInvocation, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// F5: a Running step plus a quota-parked sibling — the case spec/baton.md §2 calls out as newly
    /// ambiguous since #1607's widening. The dead-holder gate is skipped by holding <c>flow.lock</c>
    /// directly (as <see cref="CancelCommandParkedRoomLevelTargetingTests"/> already does), so liveness
    /// reads Alive and the refusal under test is the resolver's, not the dead-holder one.
    /// </summary>
    [Fact]
    public async Task Bare_cancel_against_a_running_step_and_a_parked_sibling_labels_which_is_which()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (runningExecutionId, parkedExecutionId) = await WriteRunningAndParkedFixtureAsync(roomDirectory);
            var bindingsFilePath = await WriteMixedBindingsFileAsync(testRoot);

            using (ConcurrencyGuard.Acquire(roomDirectory, "test holder simulating a live pump"))
            {
                var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: null, bindingsFilePath);
                var ex = await Assert.ThrowsAsync<CliArgumentException>(
                    () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));

                Assert.Contains($"{runningExecutionId.Value} (Running)", ex.Message, StringComparison.Ordinal);
                Assert.Contains($"{parkedExecutionId.Value} (quota-parked)", ex.Message, StringComparison.Ordinal);
                Assert.NotNull(ex.TryInvocation);
                Assert.Contains("--execution", ex.TryInvocation, StringComparison.Ordinal);
                Assert.Contains("baton status", ex.TryInvocation, StringComparison.Ordinal);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<(ExecutionId RunningExecutionId, ExecutionId ParkedExecutionId)> WriteRunningAndParkedFixtureAsync(
        string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("running-and-parked-probe"),
            1,
            [
                new WorkflowStepDefinition(new StepId("running-step"), "running-step", [], ["out-a"], [], new RetryPolicy(3)),
                new WorkflowStepDefinition(new StepId("parked-step"), "parked-step", [], ["out-b"], [], new RetryPolicy(3)),
            ]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var runningExecutionId = new ExecutionId("exec-running-1");
        var parkedExecutionId = new ExecutionId("exec-parked-1");
        var retryNotBefore = DateTimeOffset.UtcNow.AddMinutes(45);

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    runningExecutionId,
                    new WorkflowId("wf-mixed"),
                    new StepId("running-step"),
                    "running-step",
                    Inputs: [],
                    Outputs: [],
                    Timeout: TimeSpan.FromSeconds(30),
                    Environment: [],
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);

            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    parkedExecutionId,
                    new WorkflowId("wf-mixed"),
                    new StepId("parked-step"),
                    "parked-step",
                    Inputs: [],
                    Outputs: [],
                    Timeout: TimeSpan.FromSeconds(30),
                    Environment: [],
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(parkedExecutionId, FailureClassification.ExhaustedUntil, "attempt failed", retryNotBefore),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.StepRetryScheduled(new StepId("parked-step"), parkedExecutionId, retryNotBefore, 2_700_000),
                TestContext.Current.CancellationToken);
        }

        return (runningExecutionId, parkedExecutionId);
    }

    private static async Task<string> WriteMixedBindingsFileAsync(string testRoot)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["running-step"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("running-step", [], [new ProducedOutput("out-a")], []), "echo unused", TimeSpan.FromSeconds(30)),
            ["parked-step"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("parked-step", [], [new ProducedOutput("out-b")], []), "echo unused", TimeSpan.FromSeconds(30)),
        };
        var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
        await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config));

        return bindingsFilePath;
    }

    private static async Task<string> WriteOneQuickStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-quick-step-zero-candidates"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneQuickStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var writeCommand = OperatingSystem.IsWindows() ? "echo done>%BATON_OUTPUT_DIR%\\out" : "echo done > \"$BATON_OUTPUT_DIR/out\"";
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out")], []), writeCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
