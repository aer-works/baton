using Aer.Flow.Tests.TestSupport;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// Integration tests: these spawn real processes through the aer-core M5 <c>AerTask</c> binding
/// (M7 Phase 7's acceptance criteria — a three-step linear workflow runs end-to-end through
/// <see cref="MutationInterface.StartWorkflowAsync"/> and a clean exit with no output is
/// classified <c>ExecutionFailed</c>). No mocking of Aer.Core itself.
/// </summary>
public class MutationInterfaceTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task StartWorkflowAsync_runs_a_three_step_linear_workflow_to_completion()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1"),
                new WorkflowTemplateId("architect-critic-publisher"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Publisher, "publisher", ["review"], ["summary"], DependsOn: [Critic], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var publisherExecutionId = finalState.Steps.Single(s => s.StepId == Publisher).LatestExecutionId!.Value;
            var summaryPath = Path.Combine(artifactsRoot, $"execution_{publisherExecutionId}", "summary");
            Assert.True(File.Exists(summaryPath));
            Assert.Equal("architect", (await File.ReadAllTextAsync(summaryPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_step_that_fails_once_then_succeeds()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerFilePath = Path.Combine(roomDirectory, "attempt-marker");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-3"),
                new WorkflowTemplateId("flaky-architect-critic"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    FailOnFirstAttemptThenSucceed(markerFilePath, "plan", "architect"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-3"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
            Assert.Equal(0, finalState.Steps.Single(s => s.StepId == Architect).ConsecutiveFailureCount);

            // §10's history shape: two distinct ExecutionIds for Architect, the first failed and
            // the second succeeded — neither event mutated or removed.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var architectAttempts = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == Architect)
                .Select(e => e.Request.ExecutionId)
                .ToList();
            Assert.Equal(2, architectAttempts.Count);
            Assert.Equal(architectAttempts.Distinct().Count(), architectAttempts.Count);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed failed && architectAttempts.Contains(failed.ExecutionId));
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && architectAttempts.Contains(succeeded.ExecutionId));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionFailed()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("silent-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-2"),
                new WorkflowTemplateId("silent"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "silent", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["silent"] = new WorkerBinding.Process(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = events.OfType<FlowEvent.ExecutionFailed>().Single();
            Assert.Null(failedEvent.FailureClassification);
            Assert.NotNull(failedEvent.Reason);
            Assert.Contains("output.txt", failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_records_a_Retryable_ExecutionFailed_when_the_OS_itself_refuses_the_spawn()
    {
        // The refusal family's generic member (#747's review): AerException, not the typed guard.
        // Retryable — not Permanent — because an OS refusal is not proven deterministic; a stuck
        // cause terminates through RetryPolicy exhaustion instead. Polarity partner to the
        // Permanent assert in the CommandLineTooLongException test below.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("os-refused-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-os-refusal"),
                new WorkflowTemplateId("os-refusal"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "os-refused", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["os-refused"] = new WorkerBinding.Process(
                    new WorkerContract("os-refused", [], [new ProducedOutput("out.txt")], []),
                    new CoreDispatchTarget("dummy", []),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-os-refusal"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer,
                new OsRefusingCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.Retryable, failedEvent.FailureClassification);
            Assert.StartsWith("Spawn refused:", failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_records_ExecutionFailed_when_dispatch_throws_CommandLineTooLongException()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("long-cmd-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-refusal"),
                new WorkflowTemplateId("refusal"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "long-cmd", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["long-cmd"] = new WorkerBinding.Process(
                    new WorkerContract("long-cmd", [], [new ProducedOutput("out.txt")], []),
                    new CoreDispatchTarget("dummy", []),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var refusalMessage = "Command line length 40000 exceeds maximum allowable length of 32767.";
            var dispatcher = new RefusingCoreDispatcher(refusalMessage);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-refusal"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.Permanent, failedEvent.FailureClassification);
            Assert.NotNull(failedEvent.Reason);
            Assert.Contains(refusalMessage, failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private sealed class RefusingCoreDispatcher(string refusalMessage) : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            throw new CommandLineTooLongException(refusalMessage);
        }
    }

    private sealed class OsRefusingCoreDispatcher : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            // The binding's own exception type, the shape a missing binary or a bad working
            // directory actually surfaces as (#747's review, finding 3).
            throw new Aer.Core.AerException(Aer.Core.AerErrorCode.SpawnFailed);
        }
    }
}


