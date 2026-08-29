using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// M10 Phase 2: mutation-level tests against a
/// <see cref="StubCoreDispatcher"/> proving <see cref="InFlightExecutionRegistry"/> delivers an
/// on-demand cancellation to one specific in-flight <see cref="WorkerBinding.Process"/> execution
/// without touching a concurrently-dispatched sibling, that a host-initiated stop
/// (<see cref="MutationInterface.StartWorkflowAsync"/>'s own <see cref="CancellationToken"/>) reaches
/// everything currently in flight, that the intent is always durably recorded before the signal
/// reaches the stub, that no retry follows despite remaining budget, and that a cancelled
/// process-bound <see cref="PausePoint"/> step pauses exactly like Phase 1's non-process case.
/// </summary>
public class MutationInterfaceLiveCancellationTests
{
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId D = new("d");
    private static readonly StepId H = new("h");

    private static readonly WorkerContract Contract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);

    [Fact]
    public async Task RequestCancellationAsync_on_the_registry_cancels_one_in_flight_execution_leaving_its_sibling_unaffected()
    {
        // B and C are independent roots dispatched in the same round; D depends only on B, so it
        // must never dispatch once B is cancelled instead of succeeding.
        var snapshot = MakeSnapshot(
            Step(B, dependsOn: [], maxAttempts: 5),
            Step(C, dependsOn: []),
            Step(D, dependsOn: [B]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            stub.EnqueueResult(B); // deliberately never set: B is cancelled, not completed, below.
            var cResult = stub.EnqueueResult(C);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var registry = new InFlightExecutionRegistry();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            var firstRound = new[] { await ReadNextDispatchAsync(stub), await ReadNextDispatchAsync(stub) };
            Assert.Equal(new HashSet<StepId> { B, C }, new HashSet<StepId>(firstRound));

            var bExecutionId = await GetExecutionIdAsync(reader, B);

            await registry.RequestCancellationAsync(bExecutionId, TestContext.Current.CancellationToken);
            cResult.SetResult(Succeeded);

            var finalState = await AwaitWithTimeoutAsync(workflowTask);

            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == B).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == C).Status);
            Assert.Equal(StepStatus.Pending, finalState.Steps.Single(s => s.StepId == D).Status);

            // No retry despite B's remaining budget: Cancelled is never retried, so exactly one
            // ExecutionRequestAccepted for B exists, and it's still the projected latest.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionRequestAccepted era && era.Request.StepId == B);
            Assert.Equal(bExecutionId, finalState.Steps.Single(s => s.StepId == B).LatestExecutionId);

            // C's own cancellation-request/outcome vocabulary never appears — the sibling truly never
            // saw the signal.
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId != bExecutionId);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId != bExecutionId);

            // Intent-first: the recorded request precedes the classified outcome.
            var requestIndex = events.ToList().FindIndex(e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == bExecutionId);
            var outcomeIndex = events.ToList().FindIndex(e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId == bExecutionId);
            Assert.True(requestIndex >= 0);
            Assert.True(outcomeIndex > requestIndex);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_host_stop_cancels_every_execution_currently_in_flight()
    {
        var snapshot = MakeSnapshot(Step(B, dependsOn: []), Step(C, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            stub.EnqueueResult(B); // never set: both settle via the host stop below.
            stub.EnqueueResult(C);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            using var hostStop = new CancellationTokenSource();
            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                cancellationToken: hostStop.Token);

            await ReadNextDispatchAsync(stub);
            await ReadNextDispatchAsync(stub);

            hostStop.Cancel();

            var finalState = await AwaitWithTimeoutAsync(workflowTask);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Cancelled, step.Status));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.Count(e => e is FlowEvent.CancellationRequested));
            Assert.Equal(2, events.Count(e => e is FlowEvent.ExecutionCancelled));

            foreach (var stepId in new[] { B, C })
            {
                var executionId = finalState.Steps.Single(s => s.StepId == stepId).LatestExecutionId!.Value;
                var requestIndex = events.ToList().FindIndex(e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == executionId);
                var outcomeIndex = events.ToList().FindIndex(e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId == executionId);
                Assert.True(requestIndex >= 0);
                Assert.True(outcomeIndex > requestIndex);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_cancelled_process_bound_PausePoint_step_pauses()
    {
        var snapshot = MakeSnapshot(Step(H, dependsOn: [], pausePoint: new PausePoint([])));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            stub.EnqueueResult(H); // never set: cancelled instead of completing.

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var registry = new InFlightExecutionRegistry();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            await ReadNextDispatchAsync(stub);
            var hExecutionId = await GetExecutionIdAsync(reader, H);

            await registry.RequestCancellationAsync(hExecutionId, TestContext.Current.CancellationToken);

            var finalState = await AwaitWithTimeoutAsync(workflowTask);

            Assert.Equal(StepStatus.Paused, finalState.Steps.Single().Status);
            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single().PausedOutcome);
            Assert.Equal(WorkflowStatus.Paused, finalState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RequestCancellationAsync_on_the_registry_is_a_no_op_for_an_execution_not_currently_in_flight()
    {
        var snapshot = MakeSnapshot(Step(B, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var registry = new InFlightExecutionRegistry();

            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            await registry.RequestCancellationAsync(new ExecutionId("never-dispatched"), TestContext.Current.CancellationToken);

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
        int maxAttempts = 1) =>
        new(stepId, "stub-worker", [], [], dependsOn, new RetryPolicy(maxAttempts), pausePoint);

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("live-cancellation-test"),
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
        var completed = await Task.WhenAny(readTask, Task.Delay(TestTimeout));
        Assert.Same(readTask, completed);
        return await readTask;
    }

    private static async Task<ExecutionId> GetExecutionIdAsync(IEventLogReader reader, StepId stepId)
    {
        var events = await reader.ReadAllAsync();
        return events
            .OfType<FlowEvent.ExecutionRequestAccepted>()
            .Single(e => e.Request.StepId == stepId)
            .Request.ExecutionId;
    }

    private static async Task<FlowState> AwaitWithTimeoutAsync(Task<FlowState> workflowTask)
    {
        var completed = await Task.WhenAny(workflowTask, Task.Delay(TestTimeout));
        Assert.Same(workflowTask, completed);
        return await workflowTask;
    }
}
