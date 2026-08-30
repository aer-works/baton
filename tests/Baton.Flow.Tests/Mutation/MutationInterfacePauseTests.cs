using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Store;
using Baton.Flow.Tests.TestSupport;

namespace Baton.Flow.Tests.Mutation;

/// <summary>
/// M9 Phase 1 (Pause Engine): mutation-level tests against a
/// <see cref="StubCoreDispatcher"/> proving <see cref="MutationInterface.StartWorkflowAsync"/>
/// appends <see cref="FlowEvent.WorkflowPaused"/> as a derived obligation at the right moment,
/// blocks downstream readiness while paused, retries a failing <c>PausePoint</c> step per the retry policy
/// before pausing, and never re-pauses on a second call against an already-paused log.
/// </summary>
public class MutationInterfacePauseTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");

    private static readonly WorkerContract Contract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);
    private static readonly CoreDispatchResult Failed = new(1, CoreExitReason.Natural);

    [Fact]
    public async Task StartWorkflowAsync_pauses_after_success_and_never_dispatches_downstream()
    {
        var snapshot = MakeSnapshot(
            Step(A, dependsOn: [], pausePoint: new PausePoint([])),
            Step(B, dependsOn: [A]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);

            var finalState = await workflowTask;

            Assert.Equal(WorkflowStatus.Paused, finalState.Status);
            Assert.Equal(StepStatus.Paused, finalState.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, finalState.Steps.Single(s => s.StepId == B).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.WorkflowPaused paused && paused.StepId == A);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.StepId == B);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_failing_PausePoint_step_before_pausing_exactly_once()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], pausePoint: new PausePoint([]), maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var attempt1 = stub.EnqueueResult(A);
            var attempt2 = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            attempt1.SetResult(Failed);

            // The retry policy runs first: budget remains, so the first failure must not pause — a second attempt
            // dispatches instead.
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            attempt2.SetResult(Failed);

            var finalState = await workflowTask;

            Assert.Equal(StepStatus.Paused, Assert.Single(finalState.Steps).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.WorkflowPaused>());
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_never_appends_WorkflowPaused_for_a_step_without_a_PausePoint()
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

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);

            var finalState = await workflowTask;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, Assert.Single(finalState.Steps).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.WorkflowPaused);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Re_invoking_the_pump_against_an_already_paused_log_appends_nothing()
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

            var firstRunTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            await firstRunTask;

            var eventsAfterFirstRun = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            // Nothing enqueued on the stub for this second call: if the pump dispatched anything at
            // all, StubCoreDispatcher would throw.
            var secondRunState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            var eventsAfterSecondRun = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventsAfterFirstRun.Count, eventsAfterSecondRun.Count);
            Assert.Equal(WorkflowStatus.Paused, secondRunState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static WorkflowStepDefinition Step(
        StepId stepId, IReadOnlyList<StepId> dependsOn, PausePoint? pausePoint = null, int maxAttempts = 1) =>
        new(stepId, "stub-worker", [], [], dependsOn, new RetryPolicy(maxAttempts), pausePoint);

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("pause-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings() =>
        new() { ["stub-worker"] = new WorkerBinding.Process(Contract, Target, Timeout) };

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
