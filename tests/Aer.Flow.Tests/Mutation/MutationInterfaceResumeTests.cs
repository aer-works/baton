using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// Issue #1359's <c>MutationInterface.RecordResumeAsync</c>: dispatching a new, linked execution
/// against an already-dispatched step's worker, and the refusals that keep it from doing so on a
/// step it should not touch (never dispatched, still running, ambiguous, or non-process). The
/// resume-shaped binding override itself (<c>ResumeSession</c>/<c>SessionId</c>/the message as
/// <c>PromptTemplate</c>) is <c>Aer.Cli.ResumeCommand</c>'s job — exercised at that layer by
/// <c>ResumeCommandEndToEndTests</c> — so these tests pass a plain <see cref="WorkerBinding.Process"/>
/// directly, the same way <see cref="MutationInterfaceCrashRecoveryTests"/> does for its own
/// mechanics-only coverage.
/// </summary>
public class MutationInterfaceResumeTests
{
    private static readonly StepId Solo = new("solo");
    private static readonly WorkerContract Contract = new("solo-worker", [], [new ProducedOutput("plan")], []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RecordResumeAsync_dispatches_a_new_execution_linked_to_the_steps_prior_latest()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "first"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-resume");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            var firstExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Succeeded, firstState.Steps.Single().Status);
            Assert.Null(firstState.Steps.Single().LinkedFromExecutionId);

            var (resumedState, resumedExecutionId) = await MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var resumedStep = resumedState.Steps.Single();
            Assert.Equal(StepStatus.Succeeded, resumedStep.Status);
            Assert.Equal(resumedExecutionId, resumedStep.LatestExecutionId);
            Assert.NotEqual(firstExecutionId, resumedExecutionId);
            Assert.Equal(firstExecutionId, resumedStep.LinkedFromExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_when_no_step_names_the_worker()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, "no-such-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_an_ambiguous_worker_bound_to_more_than_one_step()
    {
        var stepA = new StepId("a");
        var stepB = new StepId("b");
        var snapshot = MakeSnapshot(Step(stepA, worker: "shared-worker"), Step(stepB, worker: "shared-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["shared-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, "shared-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_a_step_that_has_never_run()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // No prior events at all -- the step projects as Pending.
            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_a_step_whose_latest_attempt_is_still_running()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "solo-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["solo-worker"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf");

            // A request accepted with no terminal event: the same shape a genuinely-live dispatch
            // and a crashed-mid-flight one are indistinguishable as (spec §6) -- both project Running.
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, Solo, "solo-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "solo-worker",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordResumeAsync_refuses_a_non_process_binding()
    {
        var snapshot = MakeSnapshot(Step(Solo, worker: "human"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["human"] = new WorkerBinding.Process(Contract, WriteFile("plan", "x"), Timeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, firstState.Steps.Single().Status);

            // Only NOW swap in a NonProcess binding for the resume call -- a worker with a session to
            // resume must be a Process binding; nothing here should ever reach a live dispatch.
            var nonProcessBindings = new Dictionary<string, WorkerBinding>
            {
                ["human"] = new WorkerBinding.NonProcess(Contract),
            };

            await Assert.ThrowsAsync<InvalidResumeException>(() => MutationInterface.RecordResumeAsync(
                workflowId, roomDirectory, snapshot, nonProcessBindings, artifactsRoot, "human",
                reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static WorkflowStepDefinition Step(StepId stepId, string worker) =>
        new(stepId, worker, [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1));

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("resume-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-resume-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }
}
