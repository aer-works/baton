using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

public class KeepCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Marking_a_terminal_room_writes_the_keep_marker()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await RunToTerminalAsync(testRoot, roomDirectory);
            Assert.False(KeepMarker.IsKept(roomDirectory));

            var output = new StringWriter();
            await KeepCommand.MarkAsync(new KeepOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            Assert.True(KeepMarker.IsKept(roomDirectory));
            Assert.Contains("Marked keep", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Unmarking_removes_a_previously_written_keep_marker()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await RunToTerminalAsync(testRoot, roomDirectory);
            await KeepMarker.MarkKeepAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.True(KeepMarker.IsKept(roomDirectory));

            var output = new StringWriter();
            await KeepCommand.UnmarkAsync(new KeepOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            Assert.False(KeepMarker.IsKept(roomDirectory));
            Assert.Contains("Unmarked keep", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Marking_a_directory_with_no_ledger_is_refused_loudly()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            // A plain directory that was never a room -- no flow.jsonl at all.
            Directory.CreateDirectory(testRoot);

            var exception = await Assert.ThrowsAsync<CliArgumentException>(
                () => KeepCommand.MarkAsync(new KeepOptions(testRoot), new StringWriter(), TestContext.Current.CancellationToken));

            Assert.Contains("is not a room directory", exception.Message, StringComparison.Ordinal);
            Assert.False(KeepMarker.IsKept(testRoot));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Unmarking_a_directory_with_no_ledger_is_refused_loudly()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);

            var exception = await Assert.ThrowsAsync<CliArgumentException>(
                () => KeepCommand.UnmarkAsync(new KeepOptions(testRoot), new StringWriter(), TestContext.Current.CancellationToken));

            Assert.Contains("is not a room directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Marking_a_nonexistent_path_is_refused_loudly()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"cli-e2e-missing-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<CliArgumentException>(
            () => KeepCommand.MarkAsync(new KeepOptions(missingPath), new StringWriter(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Marking_a_still_running_non_terminal_room_still_writes_the_marker()
    {
        // ArtifactPruner checks KeepMarker.IsKept BEFORE its own terminal probe
        // (src/Baton/Artifacts/ArtifactPruner.cs) -- it never requires the room to already be
        // terminal, so `baton keep` must not invent a stricter rule than the pruner it feeds.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cli-e2e-running-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-running-1");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(TestRequest(execId)), TestContext.Current.CancellationToken);
            }

            var output = new StringWriter();
            await KeepCommand.MarkAsync(new KeepOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            Assert.True(KeepMarker.IsKept(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_marker_written_through_the_new_verb_is_honored_by_the_pruner()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cli-e2e-prune-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");

            var stepId = new StepId("stepA");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1"),
                new WorkflowTemplateId("single-step"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "worker", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var execId = new ExecutionId("exec-prune-1");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(TestRequest(execId, stepId)), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(execId), TestContext.Current.CancellationToken);
            }

            var artifactsRoot = Path.Combine(roomDirectory, ArtifactManager.ArtifactsDirectoryName);
            var execDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
            var artifactFile = Path.Combine(execDir, "output.txt");
            await File.WriteAllTextAsync(artifactFile, "kept-through-the-cli-verb", TestContext.Current.CancellationToken);

            await KeepCommand.MarkAsync(new KeepOptions(roomDirectory), new StringWriter(), TestContext.Current.CancellationToken);

            var pruned = await ArtifactPruner.PruneAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.False(pruned);
            Assert.True(Directory.Exists(execDir));
            Assert.True(File.Exists(artifactFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static ExecutionRequest TestRequest(ExecutionId execId, StepId? stepId = null) => new(
        execId,
        new WorkflowId("wf-1"),
        stepId ?? new StepId("stepA"),
        "worker",
        Inputs: [],
        Outputs: ["output.txt"],
        Timeout: TimeSpan.FromMinutes(1),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static async Task RunToTerminalAsync(string testRoot, string roomDirectory)
    {
        var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot);
        var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot);
        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

        var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
        Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("single-step"),
            1,
            [new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%BATON_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$BATON_OUTPUT_DIR/{outputName}\"";
}
