using Baton.Flow.Tests.TestSupport;
using Baton.Flow.Artifacts;
using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Outcomes;

namespace Baton.Flow.Tests.Outcomes;

/// <summary>
/// M9 Phase 4: unit tests against a real temp-directory artifacts root, proving
/// <see cref="NonProcessCompletionDetector.GetSettledExecutions"/> only ever finalizes a step
/// actually bound to a <see cref="WorkerBinding.NonProcess"/>, treats an unsatisfied contract as
/// still-pending rather than failed, and extends the same classification to step-less supplementary
/// executions while rejecting one whose <see cref="StepLessExecutionState.Worker"/> resolves to
/// nothing non-process.
/// </summary>
public class NonProcessCompletionDetectorTests
{
    private static readonly StepId Human = new("human");
    private static readonly StepId Process = new("process");
    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static readonly WorkerContract HumanContract = new("human-worker", [], [new ProducedOutput("revision.md")], []);
    private static readonly WorkerContract ProcessContract = new("process-worker", [], [], []);

    [Fact]
    public void A_running_non_process_step_whose_output_satisfies_its_contract_is_settled()
    {
        var directory = CreateTempDirectory();
        try
        {
            var executionId = new ExecutionId("h1");
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(directory, executionId);
            File.WriteAllText(Path.Combine(outputDirectory, "revision.md"), "revised");

            var snapshot = SnapshotWith(Step(Human, "human-worker"));
            var state = new FlowState(
                snapshot.WorkflowDefinitionSnapshotId,
                [new StepState(Human, StepStatus.Running, executionId, NoUpstream)]);
            var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

            var settled = NonProcessCompletionDetector.GetSettledExecutions(state, snapshot, bindings, directory);

            Assert.Equal([executionId], settled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_running_non_process_step_whose_output_is_missing_stays_pending()
    {
        var directory = CreateTempDirectory();
        try
        {
            var executionId = new ExecutionId("h1");
            ArtifactManager.AllocateOutputDirectory(directory, executionId);

            var snapshot = SnapshotWith(Step(Human, "human-worker"));
            var state = new FlowState(
                snapshot.WorkflowDefinitionSnapshotId,
                [new StepState(Human, StepStatus.Running, executionId, NoUpstream)]);
            var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

            var settled = NonProcessCompletionDetector.GetSettledExecutions(state, snapshot, bindings, directory);

            Assert.Empty(settled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_running_process_bound_step_is_never_settled_by_contract_inspection()
    {
        // Even if a process-bound step's output directory happens to satisfy its contract already
        // (e.g. Core just hasn't reported the exit yet), this detector is not the mechanism that
        // finalizes it — DispatchAndRecordOutcomeAsync's in-flight task owns that classification.
        var directory = CreateTempDirectory();
        try
        {
            var executionId = new ExecutionId("p1");
            ArtifactManager.AllocateOutputDirectory(directory, executionId);

            var snapshot = SnapshotWith(Step(Process, "process-worker"));
            var state = new FlowState(
                snapshot.WorkflowDefinitionSnapshotId,
                [new StepState(Process, StepStatus.Running, executionId, NoUpstream)]);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["process-worker"] = new WorkerBinding.Process(ProcessContract, new CoreDispatchTarget("stub", []), TimeSpan.FromSeconds(1)),
            };

            var settled = NonProcessCompletionDetector.GetSettledExecutions(state, snapshot, bindings, directory);

            Assert.Empty(settled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_pending_step_less_execution_whose_output_satisfies_its_contract_is_settled()
    {
        var directory = CreateTempDirectory();
        try
        {
            var executionId = new ExecutionId("supplement-1");
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(directory, executionId);
            File.WriteAllText(Path.Combine(outputDirectory, "revision.md"), "revised");

            var snapshot = SnapshotWith();
            var state = new FlowState(
                snapshot.WorkflowDefinitionSnapshotId,
                [],
                StepLessExecutions: [new StepLessExecutionState(executionId, "human-worker")]);
            var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

            var settled = NonProcessCompletionDetector.GetSettledExecutions(state, snapshot, bindings, directory);

            Assert.Equal([executionId], settled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void A_step_less_execution_with_no_non_process_binding_for_its_worker_throws()
    {
        var directory = CreateTempDirectory();
        try
        {
            var executionId = new ExecutionId("supplement-1");
            ArtifactManager.AllocateOutputDirectory(directory, executionId);

            var snapshot = SnapshotWith();
            var state = new FlowState(
                snapshot.WorkflowDefinitionSnapshotId,
                [],
                StepLessExecutions: [new StepLessExecutionState(executionId, "unregistered-worker")]);

            Assert.Throws<UnresolvedWorkerException>(() => NonProcessCompletionDetector.GetSettledExecutions(
                state, snapshot, new Dictionary<string, WorkerBinding>(), directory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    private static WorkflowStepDefinition Step(StepId stepId, string worker) =>
        new(stepId, worker, [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1));

    private static WorkflowDefinitionSnapshot SnapshotWith(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("human-worker-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
