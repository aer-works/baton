using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Store;
using Baton.Flow.Tests.TestSupport;

namespace Baton.Flow.Tests.Mutation;

/// <summary>
/// M9 Phase 4 (Human worker support): mutation-level tests proving a
/// <see cref="WorkerBinding.NonProcess"/> step dispatches nothing to Core, sits pending until its
/// output satisfies its <see cref="WorkerContract"/>, then finalizes and unblocks downstream
/// exactly as any other worker's success would; and that step-less supplementary executions mint
/// independently of the DAG, without perturbing any step's own projection, and feed
/// <see cref="DecisionType.RetryWithRevision"/>'s <c>SupplementaryExecutionId</c>. The test drops
/// the contract-satisfying file itself, playing the human (no watching, no polling).
/// </summary>
public class MutationInterfaceHumanWorkerTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId H = new("h");
    private static readonly StepId C = new("c");

    private static readonly WorkerContract ProcessContract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly WorkerContract HumanContract = new("human", [], [new ProducedOutput("revision.md")], []);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);
    private static readonly CoreDispatchResult Failed = new(1, CoreExitReason.Natural);

    [Fact]
    public async Task A_human_step_pauses_the_DAG_until_its_output_appears_then_dispatches_downstream()
    {
        var snapshot = MakeSnapshot(
            Step(A, dependsOn: []),
            Step(H, dependsOn: [A], worker: "human"),
            Step(C, dependsOn: [H]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var firstRunTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var firstState = await firstRunTask;

            // H was admitted (ExecutionRequestAccepted) but no Core process was ever asked for it —
            // StubCoreDispatcher would have thrown had anything tried. Still "Running":
            // no terminal event exists for it yet, same as any other unfinalized attempt.
            Assert.Equal(StepStatus.Running, firstState.Steps.Single(s => s.StepId == H).Status);
            Assert.Equal(StepStatus.Pending, firstState.Steps.Single(s => s.StepId == C).Status);

            var hExecutionId = firstState.Steps.Single(s => s.StepId == H).LatestExecutionId!.Value;
            var hOutputDirectory = Path.Combine(artifactsRoot, $"execution_{hExecutionId}");
            Assert.True(Directory.Exists(hOutputDirectory));

            // The test *is* the human: it drops the contractually required output.
            await File.WriteAllTextAsync(Path.Combine(hOutputDirectory, "revision.md"), "revised plan", TestContext.Current.CancellationToken);

            var cResult = stub.EnqueueResult(C);
            var secondRunTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(C, await ReadNextDispatchAsync(stub));
            cResult.SetResult(Succeeded);
            var secondState = await secondRunTask;

            Assert.Equal(WorkflowStatus.Terminal, secondState.Status);
            Assert.Equal(StepStatus.Succeeded, secondState.Steps.Single(s => s.StepId == H).Status);
            Assert.Equal(StepStatus.Succeeded, secondState.Steps.Single(s => s.StepId == C).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && succeeded.ExecutionId == hExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_human_step_whose_output_fails_its_condition_stays_pending_never_fails()
    {
        var conditionContract = new WorkerContract(
            "human",
            [],
            [new ProducedOutput("verdict.json", new OutputCondition("/status", new JsonScalar.String("approved")))],
            []);

        var snapshot = MakeSnapshot(Step(H, dependsOn: [], worker: "human"));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = new Dictionary<string, WorkerBinding> { ["human"] = new WorkerBinding.NonProcess(conditionContract) };
            var workflowId = new WorkflowId("wf");
            var stub = new StubCoreDispatcher();

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            var hExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;
            var outputDirectory = Path.Combine(artifactsRoot, $"execution_{hExecutionId}");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verdict.json"), """{"status":"needs_revision"}""", TestContext.Current.CancellationToken);

            var secondState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            // Never Failed — an unsatisfied contract means still pending; there is no exit signal
            // to classify against.
            Assert.Equal(StepStatus.Running, secondState.Steps.Single().Status);
            Assert.Equal(hExecutionId, secondState.Steps.Single().LatestExecutionId);

            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verdict.json"), """{"status":"approved"}""", TestContext.Current.CancellationToken);

            var thirdState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, thirdState.Steps.Single().Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordSupplementaryExecutionAsync_mints_a_step_less_execution_that_never_perturbs_any_step()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], pausePoint: new PausePoint([])));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var pausedState = await pausedTask;
            Assert.Equal(StepStatus.Paused, pausedState.Steps.Single().Status);

            var (mintedState, revisionExecutionId) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "human", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken);

            // A's own projection is untouched by minting a step-less execution.
            Assert.Equal(StepStatus.Paused, mintedState.Steps.Single().Status);
            Assert.Equal(pausedState.Steps.Single().LatestExecutionId, mintedState.Steps.Single().LatestExecutionId);
            var stepLess = Assert.Single(mintedState.StepLessExecutions);
            Assert.Equal(revisionExecutionId, stepLess.ExecutionId);
            Assert.Equal("human", stepLess.Worker);

            var outputDirectory = Path.Combine(artifactsRoot, $"execution_{revisionExecutionId}");
            Assert.True(Directory.Exists(outputDirectory));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().Single(e => e.Request.ExecutionId == revisionExecutionId);
            Assert.Null(accepted.Request.StepId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Two_supplementary_executions_during_one_pause_produce_two_immutable_artifacts_and_the_decision_names_one()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], pausePoint: new PausePoint([]), maxAttempts: 1));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var attempt1 = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            attempt1.SetResult(Failed);
            var pausedState = await pausedTask;
            var pausedExecutionId = pausedState.Steps.Single().LatestExecutionId!.Value;

            var (_, revision1) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "human", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken);
            var (state2, revision2) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "human", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(revision1, revision2);
            Assert.Equal(2, state2.StepLessExecutions.Count);

            await File.WriteAllTextAsync(Path.Combine(artifactsRoot, $"execution_{revision1}", "revision.md"), "first revision", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(artifactsRoot, $"execution_{revision2}", "revision.md"), "second revision", TestContext.Current.CancellationToken);

            var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            // Both settle into two immutable artifacts; A itself is untouched throughout.
            Assert.Empty(settledState.StepLessExecutions);
            Assert.Equal(StepStatus.Paused, settledState.Steps.Single().Status);

            var succeededExecutionIds = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionSucceeded>()
                .Select(e => e.ExecutionId)
                .ToHashSet();
            Assert.Contains(revision1, succeededExecutionIds);
            Assert.Contains(revision2, succeededExecutionIds);

            // The decision names exactly one of the two artifacts — revision2, not revision1.
            var attempt2 = stub.EnqueueResult(A);
            var resumedTask = MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                pausedExecutionId, DecisionType.RetryWithRevision, supplementaryExecutionId: revision2, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            attempt2.SetResult(Succeeded);
            await resumedTask;

            var secondAttempt = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == A)
                .ElementAt(1);
            var expectedSupplementPath = Path.Combine(artifactsRoot, $"execution_{revision2}");
            Assert.Contains(
                secondAttempt.Request.Environment,
                v => v is EnvironmentVariable.BatonComputed { Name: "BATON_SUPPLEMENTARY_INPUT" } baton && baton.Value == expectedSupplementPath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordSupplementaryExecutionAsync_against_an_unregistered_worker_throws()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");

            await Assert.ThrowsAsync<UnresolvedWorkerException>(() => MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, new Dictionary<string, WorkerBinding>(), artifactsRoot, "human", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken));

            // A process-bound role name is just as invalid — a supplementary execution is
            // non-process by definition.
            await Assert.ThrowsAsync<UnresolvedWorkerException>(() => MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, MakeBindings(), artifactsRoot, "stub-worker", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static WorkflowStepDefinition Step(
        StepId stepId,
        IReadOnlyList<StepId> dependsOn,
        PausePoint? pausePoint = null,
        int maxAttempts = 1,
        string worker = "stub-worker") =>
        new(stepId, worker, [], [], dependsOn, new RetryPolicy(maxAttempts), pausePoint);

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("human-worker-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings() => new()
    {
        ["stub-worker"] = new WorkerBinding.Process(ProcessContract, Target, Timeout),
        ["human"] = new WorkerBinding.NonProcess(HumanContract),
    };

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }

    private static async Task<StepId> ReadNextDispatchAsync(StubCoreDispatcher stub)
    {
        var readTask = stub.DispatchStarted.ReadAsync().AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(readTask, completed);
        return await readTask;
    }
}
