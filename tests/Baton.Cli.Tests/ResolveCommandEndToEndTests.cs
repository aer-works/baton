using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton resolve</c> (#1608), driven through the real <see cref="ResolveCommand.ExecuteAsync"/>
/// entry point — the exact call <c>Program.cs</c> makes — mirroring
/// <see cref="DecideCommandEndToEndTests"/>'s discipline. The resolution mutation itself (writing the
/// declared output, appending <see cref="FlowEvent.CaptureResolved"/>) is proven at the
/// <c>MutationInterface</c> layer (<c>MutationInterfaceCaptureResolutionTests</c>); this proves the
/// CLI wires room-level targeting and never loads bindings to reach it.
/// <para>
/// Nothing in <c>src/</c> can make <see cref="ShellCommandWorkerAdapter"/> (a no-op
/// <c>IWorkerResponseParser</c>) actually capture a response, so every fixture here runs a step to an
/// ordinary Failed (declared output never written) via a real <c>baton run</c>, then appends one more
/// <see cref="FlowEvent.ExecutionIndeterminate"/> for that same execution id directly — the same
/// "fabricate the terminal shape" pattern <c>WorkflowOutcomeAndExitCodeTests</c> already uses for
/// this exact value, since no producer existed before this issue.
/// </para>
/// </summary>
public class ResolveCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Accepting_the_sole_candidate_with_no_execution_given_writes_the_output_and_settles_Succeeded()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedIndeterminateRoomAsync(testRoot, roomDirectory, "advice.md", "the worker's real answer");

            var result = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(result.State));

            var outputPath = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}", "advice.md");
            Assert.Equal("the worker's real answer", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Rejecting_with_an_explicit_execution_and_reason_leaves_the_room_Failed_not_Indeterminate()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var executionId = await SeedIndeterminateRoomAsync(testRoot, roomDirectory, "advice.md", "not honest advice.md");

            var result = await ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, executionId.Value, Accept: false, Reason: "does not honestly satisfy advice.md"),
                TestContext.Current.CancellationToken);

            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(result.State));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resolving_with_no_pending_capture_refuses_to_guess()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await RunOrdinaryFailureAsync(testRoot, roomDirectory);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken));
            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_explicit_execution_naming_no_unresolved_capture_throws()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await RunOrdinaryFailureAsync(testRoot, roomDirectory);

            await Assert.ThrowsAsync<CliArgumentException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, "no-such-execution", Accept: true),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Resolving_a_room_with_no_bound_snapshot_throws_SnapshotLoadException()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cli-resolve-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<SnapshotLoadException>(() => ResolveCommand.ExecuteAsync(
                new ResolveOptions(roomDirectory, ExecutionId: null, Accept: true),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<ExecutionId> SeedIndeterminateRoomAsync(
        string testRoot, string roomDirectory, string outputName, string capturedBody)
    {
        var executionId = await RunOrdinaryFailureAsync(testRoot, roomDirectory, outputName);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionIndeterminate(
                    executionId, "captured, awaiting conductor resolution",
                    Baton.Outcomes.OutputMaterializer.CapturedResponseFileName, [outputName]),
                TestContext.Current.CancellationToken);
        }

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId.Value}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, Baton.Outcomes.OutputMaterializer.CapturedResponseFileName),
            Baton.Outcomes.OutputMaterializer.CapturedResponseHeader + "\n\n" + capturedBody,
            TestContext.Current.CancellationToken);

        return executionId;
    }

    /// <summary>Runs a single step to an ordinary Failed (declared output never written, exit 0).</summary>
    private static async Task<ExecutionId> RunOrdinaryFailureAsync(
        string testRoot, string roomDirectory, string outputName = "advice.md")
    {
        var workflowFilePath = await WriteSingleStepWorkflowAsync(testRoot, outputName);
        var bindingsFilePath = await WriteSingleStepBindingsAsync(testRoot, outputName);
        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

        var result = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
        var step = Assert.Single(result.State.Steps);
        Assert.Equal(StepStatus.Failed, step.Status);

        return step.LatestExecutionId!.Value;
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory, string outputName)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("resolve-test"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], [outputName], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsAsync(string directory, string outputName)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput(outputName)], []),
                PromptTemplate: "exit 0", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
