using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

public class CancelCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> AdaptersWithUnsatisfiable =
        new Dictionary<string, IWorkerAdapter>
        {
            ["shell"] = new ShellCommandWorkerAdapter(),
            ["unsatisfiable"] = new UnsatisfiableContractWorkerAdapter(),
        };

    [Fact]
    public async Task Cancelling_a_task_whose_journal_is_held_open_by_another_process_throws_FlowJournalHeldException_not_a_raw_IOException()
    {
        // #816's population: the same shared FlowEventLogWriter construction CancelCommand uses
        // must surface the typed refusal too, not just DecideCommand.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare contention is OS-enforced only on Windows; see DecideCommandEndToEndTests' Unix arm");
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            using var liveEngineHolder = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);

            var cancelOptions = new CancelOptions(roomDirectory, architectExecutionId.Value.Value, bindingsFilePath);

            await Assert.ThrowsAsync<FlowJournalHeldException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_room_directory_whose_bindings_file_also_names_an_unresolvable_worker_still_succeeds()
    {
        // #662, pinning the rationale CancelCommand's own lazy-resolve comment carries: "reviewer"
        // is never used by the three-step workflow below — it stands in for a worker whose contract
        // and grant became unsatisfiable after this run started, and the cancel must proceed
        // regardless.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var unresolvableBindingsFilePath = await WriteThreeStepBindingsWithAnUnresolvableEntryAsync(testRoot);
            var cancelOptions = new CancelOptions(roomDirectory, architectExecutionId.Value.Value, unresolvableBindingsFilePath);
            var canceledState = (await CancelCommand.ExecuteAsync(cancelOptions, AdaptersWithUnsatisfiable, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, canceledState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_room_directory_whose_bindings_file_also_names_an_unprovisionable_worktree_worker_still_succeeds()
    {
        // #1012: lazy worktree provisioning in baton cancel ensures an unprovisionable worktree spec
        // on an unrelated worker does not block cancelling an execution.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var unprovisionableBindingsFilePath = await WriteThreeStepBindingsWithAnUnprovisionableWorktreeEntryAsync(testRoot);
            var cancelOptions = new CancelOptions(roomDirectory, architectExecutionId.Value.Value, unprovisionableBindingsFilePath);
            var canceledState = (await CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, canceledState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_an_already_succeeded_execution_is_a_too_late_no_op_reported_as_success()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var cancelOptions = new CancelOptions(roomDirectory, architectExecutionId.Value.Value, bindingsFilePath);
            var canceledState = (await CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, canceledState.Status);
            Assert.All(canceledState.Steps, FlowAssert.Succeeded);

            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var cancellationEvents = events.OfType<FlowEvent.CancellationRequested>().ToList();
            Assert.Single(cancellationEvents);
            Assert.Equal(architectExecutionId.Value, cancellationEvents[0].ExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_against_a_room_directory_with_no_snapshot_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var cancelOptions = new CancelOptions(roomDirectory, "exec-1", bindingsFilePath);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_an_unknown_execution_id_throws_a_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var cancelOptions = new CancelOptions(roomDirectory, "not-a-real-execution-id", bindingsFilePath);
            await Assert.ThrowsAsync<UnknownExecutionIdException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_malformed_bindings_file_throws_a_typed_config_exception()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var malformedBindingsPath = Path.Combine(testRoot, "malformed.json");
            await File.WriteAllTextAsync(malformedBindingsPath, "{ not valid json", TestContext.Current.CancellationToken);
            var cancelOptions = new CancelOptions(roomDirectory, "whatever", malformedBindingsPath);

            await Assert.ThrowsAsync<WorkerBindingConfigException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// F3 (#1607 review): a bare `baton run --bindings &lt;elsewhere&gt;` never copies bindings.json
    /// into the room directory (spec/baton.md §2) — <see cref="WriteThreeStepBindingsAsync"/> writes it
    /// into <c>testRoot</c>, not <paramref name="roomDirectory"/>, which is exactly that shape. Proves
    /// the augmented message names the defaulted path and still says --bindings is available, without
    /// changing the underlying exception type every other command's missing-bindings case also throws.
    /// </summary>
    [Fact]
    public async Task Cancelling_with_the_defaulted_bindings_path_missing_names_it_and_points_at_the_flag()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var defaultBindingsPath = BatonPaths.RoomBindingsFile(roomDirectory);
            Assert.False(File.Exists(defaultBindingsPath), "bare 'baton run' must not have copied bindings.json into the room");

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId;
            Assert.NotNull(architectExecutionId);

            var cancelOptions = new CancelOptions(roomDirectory, architectExecutionId.Value.Value, defaultBindingsPath);
            var ex = await Assert.ThrowsAsync<WorkerBindingConfigException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains(defaultBindingsPath, ex.Message, StringComparison.Ordinal);
            Assert.Contains("--bindings", ex.Message, StringComparison.Ordinal);
            Assert.Contains("default", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    private static async Task<string> WriteThreeStepBindingsWithAnUnresolvableEntryAsync(string directory)
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
            ["reviewer"] = new WorkerBindingConfigEntry(
                "unsatisfiable",
                new WorkerContract("reviewer", [], [new ProducedOutput("review.md")], []),
                "irrelevant — never dispatched",
                TimeSpan.FromSeconds(30),
                PermissionGrant: new PermissionGrant(ReadFiles: true, WriteFiles: false)),
        };

        var path = Path.Combine(directory, "bindings-with-unresolvable-entry.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteThreeStepBindingsWithAnUnprovisionableWorktreeEntryAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromSeconds(30)), // wait-ok: test config timeout
            ["unprovisionable"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("unprovisionable", [], [new ProducedOutput("other")], []),
                "irrelevant — never dispatched",
                TimeSpan.FromSeconds(30), // wait-ok: test config timeout
                Worktree: new WorktreeWorkspace(
                    OperatingSystem.IsWindows() ? "C:\\nonexistent\\repo" : "/nonexistent/repo",
                    "nonexistent-ref")),
        };

        var path = Path.Combine(directory, "bindings-with-unprovisionable-entry.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$BATON_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %BATON_INPUT_0% >%BATON_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$BATON_INPUT_0\" > \"$BATON_OUTPUT_DIR/{outputName}\"";
}
