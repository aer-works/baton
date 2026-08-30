using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// M9 Phase 2 (External Decision Handler): mutation-level tests against a
/// <see cref="StubCoreDispatcher"/> proving <see cref="MutationInterface.RecordDecisionAsync"/>
/// resumes a paused workflow through the unchanged pump for <c>Resume</c>, forecloses a step
/// terminally for <c>Reject</c> even against a successful outcome, and rejects an invalid decision
/// without appending anything.
/// </summary>
public class MutationInterfaceDecisionTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");

    private static readonly WorkerContract Contract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);

    [Fact]
    public async Task RecordDecisionAsync_Resume_runs_downstream_to_the_fixed_point()
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
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var pausedState = await pausedTask;

            var pausedExecutionId = pausedState.Steps.Single(s => s.StepId == A).LatestExecutionId!.Value;

            var bResult = stub.EnqueueResult(B);
            var resumedTask = MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                pausedExecutionId, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(B, await ReadNextDispatchAsync(stub));
            bResult.SetResult(Succeeded);
            var finalState = await resumedTask;

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == B).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExternalDecisionRecorded>());
            Assert.Single(events.OfType<FlowEvent.WorkflowResumed>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordDecisionAsync_Reject_forecloses_a_successful_step_and_never_dispatches_downstream()
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
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var pausedState = await pausedTask;

            var pausedExecutionId = pausedState.Steps.Single(s => s.StepId == A).LatestExecutionId!.Value;

            // Nothing enqueued for B on the stub: if Reject dispatched it, StubCoreDispatcher throws.
            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                pausedExecutionId, DecisionType.Reject, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Rejected, finalState.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, finalState.Steps.Single(s => s.StepId == B).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.StepId == B);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RecordDecisionAsync_against_a_non_paused_execution_throws_and_appends_nothing()
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
            await pausedTask;

            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidExternalDecisionException>(() => MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                new ExecutionId("no-such-execution"), DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken));

            var eventsAfter = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventsBefore.Count, eventsAfter.Count);
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
        new WorkflowTemplateId("decision-test"),
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
