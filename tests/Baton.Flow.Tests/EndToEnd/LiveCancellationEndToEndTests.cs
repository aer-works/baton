using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Store;
using Baton.Flow.Tests.TestSupport;
using static Baton.Flow.Tests.TestSupport.CrashTestHostLauncher;
using static Baton.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Flow.Tests.EndToEnd;

/// <summary>
/// M10 Phase 4 (issue #72): live-cancellation delivery and the too-late no-op on step 4, proved
/// against real, genuinely long-running processes on a real filesystem — no stub dispatcher, unlike
/// <c>MutationInterfaceLiveCancellationTests</c> (M10 Phase 2's own mutation-level suite), which this
/// file is the real-process counterpart to. Every "is it actually still running" wait below polls
/// the log for <see cref="CoreEvent.ExecutionStarted"/> rather than a fixed delay, so these tests are
/// exactly as fast as the real dispatch underneath them, not slower.
/// </summary>
public class LiveCancellationEndToEndTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId D = new("d");
    private static readonly StepId H = new("h");

    private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Cancelling_one_real_in_flight_execution_leaves_a_concurrent_sibling_to_succeed_and_never_dispatches_downstream()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = MakeSnapshot(
                Step(B, dependsOn: [], maxAttempts: 5),
                Step(C, dependsOn: [], maxAttempts: 1),
                Step(D, dependsOn: [B], maxAttempts: 1));

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["b-worker"] = new WorkerBinding.Process(
                    new WorkerContract("b-worker", [], [new ProducedOutput("out")], []),
                    SleepThenWriteFile(TimeSpan.FromSeconds(10), "out", "should-not-be-reached"),
                    WorkflowTimeout),
                ["c-worker"] = new WorkerBinding.Process(
                    new WorkerContract("c-worker", [], [new ProducedOutput("out")], []),
                    WriteFile("out", "c-out"),
                    WorkflowTimeout),
                ["d-worker"] = new WorkerBinding.Process(
                    new WorkerContract("d-worker", ["out"], [new ProducedOutput("out")], []),
                    CopyFirstInputTo("out"),
                    WorkflowTimeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();
            var workflowId = new WorkflowId("wf-live-cancel");

            var workflowTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            await WaitForLogConditionAsync(logPath, s => s.CoreEvents.OfType<CoreEvent.ExecutionStarted>().Any());
            var bExecutionId = await GetLatestAcceptedExecutionIdAsync(logPath, B);

            await registry.RequestCancellationAsync(bExecutionId, TestContext.Current.CancellationToken);

            var finalState = await AwaitWithTimeoutAsync(workflowTask);

            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == B).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == C).Status);
            Assert.Equal(StepStatus.Pending, finalState.Steps.Single(s => s.StepId == D).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            // No retry despite B's remaining budget: still exactly one attempt for it.
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>(), e => e.Request.StepId == B);
            Assert.DoesNotContain(events.OfType<FlowEvent.ExecutionRequestAccepted>(), e => e.Request.StepId == D);

            var requestIndex = IndexOf(events, e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == bExecutionId);
            var outcomeIndex = IndexOf(events, e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId == bExecutionId);
            Assert.True(requestIndex >= 0 && outcomeIndex > requestIndex);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_cancelled_real_PausePoint_step_pauses_then_RetryWithRevision_reruns_it_to_success()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var snapshot = MakeSnapshot(Step(H, dependsOn: [], maxAttempts: 2, pausePoint: new PausePoint([])));

            var sleepingBindings = new Dictionary<string, WorkerBinding>
            {
                ["h-worker"] = new WorkerBinding.Process(
                    new WorkerContract("h-worker", [], [new ProducedOutput("out")], []),
                    SleepThenWriteFile(TimeSpan.FromSeconds(10), "out", "should-not-be-reached"),
                    WorkflowTimeout),
            };
            var revisionBindings = new Dictionary<string, WorkerBinding>
            {
                ["h-worker"] = new WorkerBinding.Process(
                    new WorkerContract("h-worker", [], [new ProducedOutput("out")], []),
                    ConsumeSupplementaryInputElseFail(scriptDirectory, "out", "revision.md"),
                    WorkflowTimeout),
                ["human"] = new WorkerBinding.NonProcess(new WorkerContract("human", [], [new ProducedOutput("revision.md")], [])),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();
            var workflowId = new WorkflowId("wf-cancel-pause-recover");

            var workflowTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, sleepingBindings, artifactsRoot, reader, writer, dispatcher,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            await WaitForLogConditionAsync(logPath, s => s.CoreEvents.OfType<CoreEvent.ExecutionStarted>().Any());
            var hExecutionId = await GetLatestAcceptedExecutionIdAsync(logPath, H);

            await registry.RequestCancellationAsync(hExecutionId, TestContext.Current.CancellationToken);

            var pausedState = await AwaitWithTimeoutAsync(workflowTask);
            Assert.Equal(StepStatus.Paused, pausedState.Steps.Single().Status);
            Assert.Equal(StepStatus.Cancelled, pausedState.Steps.Single().PausedOutcome);

            var (mintedState, revisionExecutionId) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, revisionBindings, artifactsRoot, "human", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(mintedState.StepLessExecutions);
            var revisionOutputDirectory = Path.Combine(artifactsRoot, $"execution_{revisionExecutionId}");
            await File.WriteAllTextAsync(Path.Combine(revisionOutputDirectory, "revision.md"), "revised-result", TestContext.Current.CancellationToken);

            await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, revisionBindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var retriedState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, revisionBindings, artifactsRoot, reader, writer, dispatcher,
                hExecutionId, DecisionType.RetryWithRevision, supplementaryExecutionId: revisionExecutionId, cancellationToken: TestContext.Current.CancellationToken);

            var hAfterRetry = retriedState.Steps.Single();
            Assert.Equal(StepStatus.Paused, hAfterRetry.Status);
            Assert.Equal(StepStatus.Succeeded, hAfterRetry.PausedOutcome);
            Assert.NotEqual(hExecutionId, hAfterRetry.LatestExecutionId);

            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, revisionBindings, artifactsRoot, reader, writer, dispatcher,
                hAfterRetry.LatestExecutionId!.Value, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single().Status);
            var resultPath = Path.Combine(artifactsRoot, $"execution_{finalState.Steps.Single().LatestExecutionId}", "out");
            Assert.Equal("revised-result", (await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_cancellation_request_against_an_already_succeeded_real_execution_is_a_too_late_no_op()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 1));
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a-worker"] = new WorkerBinding.Process(
                    new WorkerContract("a-worker", [], [new ProducedOutput("out")], []),
                    WriteFile("out", "a-out"),
                    WorkflowTimeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-too-late-success");

            var succeededState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            var executionId = succeededState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Succeeded, succeededState.Steps.Single().Status);
            var eventCountBefore = (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).Count;

            var afterTooLateCancel = await MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, executionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, afterTooLateCancel.Steps.Single().Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventCountBefore + 1, events.Count);
            Assert.Single(events, e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == executionId);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionId}")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_cancellation_request_against_an_already_failed_real_execution_is_a_too_late_no_op()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 1));
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a-worker"] = new WorkerBinding.Process(
                    new WorkerContract("a-worker", [], [new ProducedOutput("out")], []),
                    ExitCleanlyWithoutWriting(),
                    WorkflowTimeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-too-late-failure");

            var failedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            var executionId = failedState.Steps.Single().LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Failed, failedState.Steps.Single().Status);
            var eventCountBefore = (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).Count;

            var afterTooLateCancel = await MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, executionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, afterTooLateCancel.Steps.Single().Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventCountBefore + 1, events.Count);
            Assert.Single(events, e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == executionId);
            Assert.Single(events, e => e is FlowEvent.ExecutionFailed ef && ef.ExecutionId == executionId);
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionId}")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_second_cancellation_request_against_an_already_cancelled_real_execution_is_a_too_late_no_op()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 5));
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a-worker"] = new WorkerBinding.Process(
                    new WorkerContract("a-worker", [], [new ProducedOutput("out")], []),
                    SleepThenWriteFile(TimeSpan.FromSeconds(10), "out", "should-not-be-reached"),
                    WorkflowTimeout),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();
            var workflowId = new WorkflowId("wf-too-late-cancelled");

            var workflowTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            await WaitForLogConditionAsync(logPath, s => s.CoreEvents.OfType<CoreEvent.ExecutionStarted>().Any());
            var executionId = await GetLatestAcceptedExecutionIdAsync(logPath, A);
            await registry.RequestCancellationAsync(executionId, TestContext.Current.CancellationToken);

            var cancelledState = await AwaitWithTimeoutAsync(workflowTask);
            Assert.Equal(StepStatus.Cancelled, cancelledState.Steps.Single().Status);
            var eventCountBefore = (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).Count;

            // A second, independent mutation-surface call — the too-late request itself, this time
            // with nothing live left to deliver to at all.
            var afterSecondCancel = await MutationInterface.RequestCancellationAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, executionId, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Cancelled, afterSecondCancel.Steps.Single().Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventCountBefore + 1, events.Count);
            Assert.Equal(2, events.Count(e => e is FlowEvent.CancellationRequested cr && cr.ExecutionId == executionId));
            Assert.Single(events, e => e is FlowEvent.ExecutionCancelled ec && ec.ExecutionId == executionId);
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionId}")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<ExecutionId> GetLatestAcceptedExecutionIdAsync(string logPath, StepId stepId)
    {
        var events = await new FlowEventLogReader(logPath).ReadAllAsync();
        return events.OfType<FlowEvent.ExecutionRequestAccepted>().Last(e => e.Request.StepId == stepId).Request.ExecutionId;
    }

    private static async Task<FlowState> AwaitWithTimeoutAsync(Task<FlowState> workflowTask)
    {
        var completed = await Task.WhenAny(workflowTask, Task.Delay(WorkflowTimeout));
        Assert.Same(workflowTask, completed);
        return await workflowTask;
    }

    private static int IndexOf(IReadOnlyList<FlowEvent> events, Func<FlowEvent, bool> predicate)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (predicate(events[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static WorkflowStepDefinition Step(
        StepId stepId, IReadOnlyList<StepId> dependsOn, int maxAttempts = 1, PausePoint? pausePoint = null) =>
        new(stepId, $"{stepId}-worker", [], ["out"], dependsOn, new RetryPolicy(maxAttempts), pausePoint);

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("live-cancellation-e2e-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"live-cancel-task-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }
}
