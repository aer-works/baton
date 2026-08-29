using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// M10 Phase 1: mutation-level tests against a
/// <see cref="StubCoreDispatcher"/> proving <see cref="MutationInterface.RequestCancellationAsync"/>
/// cancels a pending non-process execution end-to-end (intent, then finalization; downstream never
/// dispatched; no retry despite remaining budget), records a too-late request against an
/// already-terminal target without altering it, gives a cancelled <see cref="PausePoint"/> step its
/// first pause, and rejects an unknown target without appending anything.
/// </summary>
public class MutationInterfaceCancellationTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId H = new("h");
    private static readonly StepId C = new("c");

    private static readonly WorkerContract ProcessContract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly WorkerContract HumanContract = new("human", [], [new ProducedOutput("revision.md")], []);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);

    [Fact]
    public async Task RequestCancellationAsync_cancels_a_pending_human_step_and_never_dispatches_downstream()
    {
        var snapshot = MakeSnapshot(
            Step(A, dependsOn: []),
            Step(H, dependsOn: [A], worker: "human", maxAttempts: 5),
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

            var hExecutionId = firstState.Steps.Single(s => s.StepId == H).LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Running, firstState.Steps.Single(s => s.StepId == H).Status);

            // Nothing enqueued for C on the stub: if cancellation somehow let it through,
            // StubCoreDispatcher would throw.
            var cancelledState = await MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, hExecutionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Cancelled, cancelledState.Steps.Single(s => s.StepId == H).Status);
            Assert.Equal(StepStatus.Pending, cancelledState.Steps.Single(s => s.StepId == C).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == hExecutionId);
            Assert.Single(events, e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId == hExecutionId);

            // No retry despite H's remaining budget: Cancelled is never retried, and the
            // pump above already ran to its fixed point without re-dispatching H.
            Assert.Equal(hExecutionId, cancelledState.Steps.Single(s => s.StepId == H).LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RequestCancellationAsync_against_an_already_succeeded_target_appends_exactly_one_line_and_changes_nothing()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;
            var aExecutionId = state.Steps.Single().LatestExecutionId!.Value;

            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            var finalState = await MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, aExecutionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single().Status);

            var eventsAfter = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventsBefore.Count + 1, eventsAfter.Count);
            Assert.IsType<FlowEvent.CancellationRequested>(eventsAfter[^1]);
            Assert.DoesNotContain(eventsAfter, e => e is FlowEvent.ExecutionCancelled);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_cancelled_PausePoint_human_step_pauses()
    {
        var snapshot = MakeSnapshot(Step(H, dependsOn: [], worker: "human", pausePoint: new PausePoint([])));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");
            var stub = new StubCoreDispatcher();

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            var hExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Running, firstState.Steps.Single().Status);

            var cancelledState = await MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, hExecutionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Paused, cancelledState.Steps.Single().Status);
            Assert.Equal(StepStatus.Cancelled, cancelledState.Steps.Single().PausedOutcome);
            Assert.Equal(WorkflowStatus.Paused, cancelledState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RequestCancellationAsync_against_an_unknown_ExecutionId_throws_and_appends_nothing()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");
            var stub = new StubCoreDispatcher();

            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<UnknownExecutionIdException>(() => MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                new ExecutionId("no-such-execution"), cancellationToken: TestContext.Current.CancellationToken));

            var eventsAfter = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventsBefore.Count, eventsAfter.Count);
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
        new WorkflowTemplateId("cancellation-test"),
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
