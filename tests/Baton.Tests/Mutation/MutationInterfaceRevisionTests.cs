using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// M9 Phase 3 (<c>RetryWithRevision</c>/<c>Supersede</c> consequences and the
/// invalidation cascade): mutation-level tests against a <see cref="StubCoreDispatcher"/>. Proves
/// the two decision types that mint new work actually dispatch it, that the supplementary artifact
/// reaches the new dispatch as <c>BATON_SUPPLEMENTARY_INPUT</c>, that the cascade needs no mechanism
/// beyond the ordinary pump, and that both consequences are re-derivable from the log alone —
/// re-invoking the pump after a decision is recorded but before its consequence is dispatched
/// still dispatches it.
/// </summary>
public class MutationInterfaceRevisionTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Note = new("note");

    private static readonly WorkerContract Contract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);
    private static readonly CoreDispatchResult Failed = new(1, CoreExitReason.Natural);

    [Fact]
    public async Task RetryWithRevision_reopens_an_exhausted_step_and_dispatches_a_fresh_attempt()
    {
        var snapshot = MakeSnapshot(Step(Architect, dependsOn: [], pausePoint: new PausePoint([]), maxAttempts: 1));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var attempt1 = stub.EnqueueResult(Architect);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            attempt1.SetResult(Failed);
            var pausedState = await pausedTask;

            // A single-attempt RetryPolicy exhausted budget: paused with a Failed underlying outcome.
            Assert.Equal(StepStatus.Paused, Assert.Single(pausedState.Steps).Status);
            var pausedExecutionId = pausedState.Steps.Single().LatestExecutionId!.Value;

            var attempt2 = stub.EnqueueResult(Architect);
            var resumedTask = MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                pausedExecutionId, DecisionType.RetryWithRevision, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            attempt2.SetResult(Succeeded);
            var finalState = await resumedTask;

            // Architect's PausePoint pauses on every settled round, including a successful retry —
            // the fresh ExecutionId has never itself been paused.
            Assert.Equal(WorkflowStatus.Paused, finalState.Status);
            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Paused, architect.Status);
            Assert.Equal(StepStatus.Succeeded, architect.PausedOutcome);

            var accepted = (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.ExecutionRequestAccepted>().ToList();
            Assert.Equal(2, accepted.Count);
            Assert.NotEqual(accepted[0].Request.ExecutionId, accepted[1].Request.ExecutionId);
            Assert.DoesNotContain(accepted[1].Request.Environment, v => v.Name == "BATON_SUPPLEMENTARY_INPUT");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RetryWithRevision_with_a_SupplementaryExecutionId_attaches_BATON_SUPPLEMENTARY_INPUT()
    {
        var snapshot = MakeSnapshot(
            Step(Note, dependsOn: []),
            Step(Architect, dependsOn: [], pausePoint: new PausePoint([]), maxAttempts: 1));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var noteResult = stub.EnqueueResult(Note);
            var attempt1 = stub.EnqueueResult(Architect);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Note, await ReadNextDispatchAsync(stub));
            noteResult.SetResult(Succeeded);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            attempt1.SetResult(Failed);
            var pausedState = await pausedTask;

            var noteExecutionId = pausedState.Steps.Single(s => s.StepId == Note).LatestExecutionId!.Value;
            var pausedExecutionId = pausedState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;

            var attempt2 = stub.EnqueueResult(Architect);
            var resumedTask = MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                pausedExecutionId, DecisionType.RetryWithRevision, supplementaryExecutionId: noteExecutionId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            attempt2.SetResult(Succeeded);
            await resumedTask;

            var accepted = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == Architect)
                .ToList();
            var secondAttempt = accepted[1];

            var expectedPath = Path.Combine(artifactsRoot, $"execution_{noteExecutionId}");
            Assert.Contains(
                secondAttempt.Request.Environment,
                v => v is EnvironmentVariable.BatonComputed { Name: "BATON_SUPPLEMENTARY_INPUT" } baton && baton.Value == expectedPath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Re_invoking_the_pump_after_a_recorded_but_undispatched_RetryWithRevision_still_dispatches_it()
    {
        var snapshot = MakeSnapshot(Step(Architect, dependsOn: [], pausePoint: new PausePoint([]), maxAttempts: 1));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var attempt1 = stub.EnqueueResult(Architect);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            attempt1.SetResult(Failed);
            var pausedState = await pausedTask;
            var pausedExecutionId = pausedState.Steps.Single().LatestExecutionId!.Value;

            // Simulates a crash between recording the decision and PumpToFixedPointAsync dispatching
            // its consequence: append exactly what RecordDecisionAsync would have appended, then stop
            // — never calling RecordDecisionAsync itself, so nothing has run the pump yet.
            var decisionId = new DecisionId(Guid.NewGuid().ToString("n"));
            await writer.AppendAsync(new FlowEvent.ExternalDecisionRecorded(
                decisionId, pausedExecutionId, DecisionType.RetryWithRevision, TargetStepId: null, SupplementaryExecutionId: null), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.WorkflowResumed(decisionId), TestContext.Current.CancellationToken);

            var attempt2 = stub.EnqueueResult(Architect);

            // An ordinary StartWorkflowAsync call — not RecordDecisionAsync — proves the consequence
            // is a projected fact the pump re-derives on its own, not state the handler remembered.
            var recoveredTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            attempt2.SetResult(Succeeded);
            var finalState = await recoveredTask;

            // Architect's PausePoint pauses again once the reopened round settles — what
            // matters here is that the consequence dispatched at all, on a fresh ExecutionId.
            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Paused, architect.Status);
            Assert.Equal(StepStatus.Succeeded, architect.PausedOutcome);
            Assert.NotEqual(pausedExecutionId, architect.LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Re_invoking_the_pump_after_a_recorded_but_undispatched_Supersede_still_dispatches_it()
    {
        var snapshot = MakeSnapshot(
            Step(Architect, dependsOn: []),
            Step(Critic, dependsOn: [Architect], pausePoint: new PausePoint([Architect])));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var architect1 = stub.EnqueueResult(Architect);
            var critic1 = stub.EnqueueResult(Critic);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var pausedTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            architect1.SetResult(Succeeded);
            Assert.Equal(Critic, await ReadNextDispatchAsync(stub));
            critic1.SetResult(Succeeded);
            var pausedState = await pausedTask;

            var criticExecutionId = pausedState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;

            var decisionId = new DecisionId(Guid.NewGuid().ToString("n"));
            await writer.AppendAsync(new FlowEvent.ExternalDecisionRecorded(
                decisionId, criticExecutionId, DecisionType.Supersede, TargetStepId: Architect, SupplementaryExecutionId: criticExecutionId), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new FlowEvent.WorkflowResumed(decisionId), TestContext.Current.CancellationToken);

            var architect2 = stub.EnqueueResult(Architect);

            var recoveredTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            architect2.SetResult(Succeeded);

            // Architect's rerun makes Critic stale (condition 2) and Critic re-dispatches too, then
            // pauses again at its own PausePoint.
            var critic2 = stub.EnqueueResult(Critic);
            Assert.Equal(Critic, await ReadNextDispatchAsync(stub));
            critic2.SetResult(Succeeded);

            var recoveredState = await recoveredTask;
            Assert.Equal(WorkflowStatus.Paused, recoveredState.Status);
            Assert.Equal(StepStatus.Paused, recoveredState.Steps.Single(s => s.StepId == Critic).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Supersede_drives_the_full_architect_critic_cascade_to_a_second_pause_then_Resume()
    {
        // The Supersede example, reproduced end to end: Architect succeeds, Critic succeeds and
        // pauses with SupersedeTargets: [Architect], a Supersede naming Critic's own execution as
        // the supplement reruns Architect, Architect's success makes Critic stale via condition 2
        // and Critic reruns automatically with no separate cascade mechanism, Critic pauses again
        // at the same PausePoint, and a final Resume reaches the fixed point.
        var snapshot = MakeSnapshot(
            Step(Architect, dependsOn: []),
            Step(Critic, dependsOn: [Architect], pausePoint: new PausePoint([Architect])));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var architect1 = stub.EnqueueResult(Architect);
            var critic1 = stub.EnqueueResult(Critic);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var firstPauseTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            architect1.SetResult(Succeeded);
            Assert.Equal(Critic, await ReadNextDispatchAsync(stub));
            critic1.SetResult(Succeeded);
            var firstPauseState = await firstPauseTask;

            Assert.Equal(WorkflowStatus.Paused, firstPauseState.Status);
            var architectExecutionId1 = firstPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId1 = firstPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;

            var architectOutputDirectory1 = Path.Combine(artifactsRoot, $"execution_{architectExecutionId1}");
            Assert.True(Directory.Exists(architectOutputDirectory1));

            // The spec's own example: Critic's feedback artifact is its own successful execution.
            var architect2 = stub.EnqueueResult(Architect);
            var supersedeTask = MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                criticExecutionId1, DecisionType.Supersede, targetStepId: Architect, supplementaryExecutionId: criticExecutionId1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Architect, await ReadNextDispatchAsync(stub));
            architect2.SetResult(Succeeded);

            var critic2 = stub.EnqueueResult(Critic);
            Assert.Equal(Critic, await ReadNextDispatchAsync(stub));
            critic2.SetResult(Succeeded);

            var secondPauseState = await supersedeTask;
            Assert.Equal(WorkflowStatus.Paused, secondPauseState.Status);
            var architectExecutionId2 = secondPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId2 = secondPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;

            Assert.NotEqual(architectExecutionId1, architectExecutionId2);
            Assert.NotEqual(criticExecutionId1, criticExecutionId2);

            // A1's artifact directory is untouched — history is never cleaned up.
            Assert.True(Directory.Exists(architectOutputDirectory1));

            var criticAccepted = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Single(e => e.Request.ExecutionId == criticExecutionId2);
            Assert.Equal(architectExecutionId2, criticAccepted.Request.UpstreamExecutionIds[Architect]);

            var architectAccepted = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Single(e => e.Request.ExecutionId == architectExecutionId2);
            var expectedSupplementPath = Path.Combine(artifactsRoot, $"execution_{criticExecutionId1}");
            Assert.Contains(
                architectAccepted.Request.Environment,
                v => v is EnvironmentVariable.BatonComputed { Name: "BATON_SUPPLEMENTARY_INPUT" } baton && baton.Value == expectedSupplementPath);

            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                criticExecutionId2, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Architect).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Critic).Status);
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
        new WorkflowTemplateId("revision-test"),
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
