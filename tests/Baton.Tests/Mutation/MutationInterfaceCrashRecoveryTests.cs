using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// M10 Phase 3 (full crash-recovery robustness): mutation-level tests proving each of the four crash states a
/// process-bound step's unfinalized latest attempt can be in is reconciled correctly by reading back
/// the Core half of the log. Every fixture manufactures the crash window directly — appending
/// exactly the <see cref="LogEntry"/> lines a real crash would leave behind via
/// <see cref="FlowEventLogWriter"/>'s <see cref="IEventLogWriter"/>/<see cref="ICoreEventLogWriter"/>
/// halves — rather than actually killing a process (Phase 4's job).
/// </summary>
public class MutationInterfaceCrashRecoveryTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId C = new("c");

    private static readonly WorkerContract ProcessContract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);

    [Fact]
    public async Task StartWorkflowAsync_resubmits_an_execution_with_no_recorded_ExecutionStarted_under_the_same_ExecutionId()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            // The named safe pre-spawn crash state: the intent is durable, but Core never got a
            // chance to run it (crash between accept-fsync and spawn).
            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);
            Assert.Equal(0, state.Steps.Single().ConsecutiveFailureCount);

            // The same attempt, not a retry: no new ExecutionRequestAccepted for this step.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_unfulfilled_cancellation_for_a_never_started_execution_and_never_dispatches_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []), Step(C, dependsOn: [A]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new FlowEvent.CancellationRequested(executionId), TestContext.Current.CancellationToken);

            // Nothing enqueued: if the cancel didn't win, StartWorkflowAsync would try to dispatch A
            // (or, worse, C) and StubCoreDispatcher would throw.
            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Cancelled, state.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, state.Steps.Single(s => s.StepId == C).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_exit_with_no_outcome_as_if_the_completion_had_just_arrived()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: []), Step(C, dependsOn: [A]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            // Ran to a natural, successful exit while Flow was down — Core recorded both
            // lifecycle events, but no Flow-side outcome was ever appended for them.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var cResult = stub.EnqueueResult(C);

            // Nothing enqueued for A: it must be classified from the recorded exit, never dispatched.
            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(C, await ReadNextDispatchAsync(stub));
            cResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == C).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_nonzero_exit_as_Failed_and_retries_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 1, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);

            // Two attempts total: the crash-recovered classification (Failed) plus the retry (Succeeded).
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            Assert.Single(events, e => e is FlowEvent.ExecutionFailed ef && ef.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_recorded_nonzero_exit_with_stderr_as_Failed_with_stderr_reason()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 1, CoreExitReason.Natural, StderrTail: "crash-recovered-stderr-fragment"), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(executionId, failedEvent.ExecutionId);
            Assert.NotNull(failedEvent.Reason);
            Assert.Contains("crash-recovered-stderr-fragment", failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_orphan_as_a_retryable_failed_attempt_and_retries_it()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 2));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var orphanExecutionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);

            // The third crash state: Core recorded the start, but this pump's predecessor died
            // before an exit was ever recorded — nothing to classify against.
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(orphanExecutionId, Pid: 4242), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var retryResult = stub.EnqueueResult(A);

            // Nothing enqueued for the orphan's own ExecutionId: it must never be dispatched again.
            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            retryResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.NotEqual(orphanExecutionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var abandoned = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(orphanExecutionId, abandoned.ExecutionId);
            Assert.Equal(FailureClassification.Retryable, abandoned.FailureClassification);

            // The orphaned attempt's own artifact directory is untouched by the retry, which
            // gets its own fresh directory under the new ExecutionId.
            Assert.True(Directory.Exists(ArtifactManager.ResolveOutputDirectory(artifactsRoot, orphanExecutionId)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_finalizes_an_orphan_as_terminally_failed_once_its_retry_budget_is_exhausted()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], maxAttempts: 1));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();
            var workflowId = new WorkflowId("wf");

            var orphanExecutionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(orphanExecutionId, Pid: 4242), TestContext.Current.CancellationToken);

            // Nothing enqueued at all: MaxAttempts: 1 forecloses the retry, so the pump must reach
            // its fixed point without dispatching anything.
            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, state.Steps.Single().Status);
            Assert.Equal(orphanExecutionId, state.Steps.Single().LatestExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Crash_recovery_classification_uses_the_live_contracts_OutputCondition_when_the_binding_resolves()
    {
        // The #724 review's gap: nothing drove a condition-bearing contract through this path. The
        // live binding resolves here, so its OutputCondition must bite — a recorded clean exit
        // whose artifact does NOT satisfy the condition classifies as failure, proving the
        // request-derived fallback (names only, no conditions) is NOT what runs when the live
        // contract is available.
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "conditioned-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, A, "conditioned-worker", Inputs: [], Outputs: ["verdict"], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "verdict"), """{"status":"rejected"}""", TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["conditioned-worker"] = new WorkerBinding.Process(
                    new WorkerContract(
                        "conditioned-worker", [],
                        [new ProducedOutput("verdict", new OutputCondition("/status", new JsonScalar.String("approved")))],
                        []),
                    Target, Timeout),
            };

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Failed, state.Steps.Single(s => s.StepId == A).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionFailed ef && ef.ExecutionId == executionId);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionSucceeded);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_crash_recovery_candidate_when_its_worker_binding_refuses_to_resolve()
    {
        // Same recorded history as the absent-worker arm below, but the binding is present and
        // REFUSING (see RefusingBindings) — the classify path must fall back to the recorded
        // request's contract, not surface the refusal.
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "unresolvable-worker"));
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var workflowId = new WorkflowId("wf");
            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId, workflowId, A, "unresolvable-worker", Inputs: [], Outputs: [], Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, new RefusingBindings(), artifactsRoot, reader, writer,
                new StubCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_crash_recovery_candidate_when_its_worker_binding_is_unresolvable()
    {
        var snapshot = MakeSnapshot(Step(A, dependsOn: [], worker: "unresolvable-worker"));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var emptyBindings = new Dictionary<string, WorkerBinding>();
            var workflowId = new WorkflowId("wf");

            var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            var request = new ExecutionRequest(
                executionId,
                workflowId,
                A,
                "unresolvable-worker",
                Inputs: [],
                Outputs: [],
                Timeout,
                ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionStarted(executionId, Pid: 4242), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new CoreEvent.ExecutionExited(executionId, ExitCode: 0, CoreExitReason.Natural), TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, emptyBindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single(s => s.StepId == A).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == executionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_journals_StepRebound_when_resubmitting_through_a_divergent_adapter()
    {
        // Issue #1583 (operator ruling 2026-09-01): when an unstarted accepted execution is resubmitted
        // after crash-recovery with a binding whose Adapter differs from the request's recorded Adapter,
        // Flow must journal FlowEvent.StepRebound (old->new) before dispatching.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "agy", model: "gemini-3-pro");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var rebound = Assert.Single(events.OfType<FlowEvent.StepRebound>());
            Assert.Equal(A, rebound.StepId);
            Assert.Equal(executionId, rebound.ForExecutionId);
            Assert.Equal("agy", rebound.PreviousAdapter);
            Assert.Equal("gemini-3-pro", rebound.PreviousModel);
            Assert.Equal("claude", rebound.NewAdapter);
            Assert.Equal("sonnet", rebound.NewModel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_journals_StepRebound_when_resubmitting_through_a_divergent_model()
    {
        // Issue #1583: model divergence (same adapter, different model) also journals StepRebound.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "opus");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "claude", model: "sonnet");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var rebound = Assert.Single(events.OfType<FlowEvent.StepRebound>());
            Assert.Equal(A, rebound.StepId);
            Assert.Equal(executionId, rebound.ForExecutionId);
            Assert.Equal("claude", rebound.PreviousAdapter);
            Assert.Equal("sonnet", rebound.PreviousModel);
            Assert.Equal("claude", rebound.NewAdapter);
            Assert.Equal("opus", rebound.NewModel);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_does_not_journal_StepRebound_for_a_legacy_request_with_no_recorded_Adapter()
    {
        // #1583 MEDIUM: a request accepted before #1567 added ExecutionRequest.Adapter/Model has both
        // fields null -- absence of a record, not absence of an adapter (ExecutionRequest.Adapter's own
        // doc). Comparing null against the current binding's "claude" must not read as a divergence:
        // nobody rebound anything, and journaling StepRebound(null->claude) would durably assert a
        // failover that never happened.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: null, model: null);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRebound>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_journals_a_second_StepRebound_when_a_rebind_reverts_after_a_pre_spawn_crash()
    {
        // #1583 HIGH, review scenario B: pump P1 accepted on "claude"; pump P2 rebound to "agy" and
        // journaled StepRebound(claude->agy) but crashed pre-spawn (never appended a Core outcome), so
        // this fixture manufactures exactly that log tail directly, the same way every other fixture
        // in this file manufactures its crash window. Pump P3 (this call) sees the binding reverted
        // back to "claude". Without StateProjector projecting the first StepRebound as an override on
        // AcceptedRequestByExecutionId, P3's replay still reads request.Adapter == "claude" (the
        // original accept), sees no divergence against the current "claude" binding, and journals
        // nothing -- silently reintroducing the exact misattribution #1583 exists to fix. With the
        // projection arm in place, P3 sees the request as bound to "agy" (the first StepRebound's
        // override), detects divergence against the current "claude" binding, and journals a SECOND
        // StepRebound naming agy->claude.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: null);
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "claude", model: null);
            await writer.AppendAsync(
                new FlowEvent.StepRebound(A, executionId, PreviousAdapter: "claude", PreviousModel: null, NewAdapter: "agy", NewModel: null),
                TestContext.Current.CancellationToken);

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var rebounds = events.OfType<FlowEvent.StepRebound>().ToList();
            Assert.Equal(2, rebounds.Count);
            var second = rebounds[1];
            Assert.Equal(executionId, second.ForExecutionId);
            Assert.Equal("agy", second.PreviousAdapter);
            Assert.Equal("claude", second.NewAdapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_dispatches_a_divergent_resubmit_with_the_rebound_Adapter_and_Model_in_the_same_round()
    {
        // #1583 MEDIUM: MutationInterface.cs's in-memory `request with { Adapter, Model }` update
        // (kept alongside the StateProjector override as the same-round fast path) has to actually
        // reach Core -- DispatchAndRecordOutcomeAsync's live classification reads prepared.Request.Adapter,
        // not the binding, so a dispatch still carrying the pre-crash Adapter/Model would pick the wrong
        // usage parser for this round's own outcome classification even though the journaled event is
        // correct. Deleting the in-memory update (MutationInterface.cs:907-912) makes this fail.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "agy", model: "gemini-3-pro");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            await runTask;

            Assert.NotNull(stub.LastDispatchedRequest);
            Assert.Equal("claude", stub.LastDispatchedRequest!.Adapter);
            Assert.Equal("sonnet", stub.LastDispatchedRequest.Model);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_does_not_journal_StepRebound_when_resubmitting_through_an_identical_binding()
    {
        // Control / non-divergent case: when the binding matches the request's recorded Adapter and Model,
        // no FlowEvent.StepRebound is emitted.
        var snapshot = MakeSnapshot(Step(A, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings(adapter: "claude", model: "sonnet");
            var workflowId = new WorkflowId("wf");

            var executionId = await AcceptRequestAsync(writer, workflowId, artifactsRoot, A, adapter: "claude", model: "sonnet");

            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);

            var runTask = MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);
            var state = await runTask;

            Assert.Equal(StepStatus.Succeeded, state.Steps.Single().Status);
            Assert.Equal(executionId, state.Steps.Single().LatestExecutionId);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.StepRebound>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<ExecutionId> AcceptRequestAsync(
        FlowEventLogWriter writer,
        WorkflowId workflowId,
        string artifactsRoot,
        StepId stepId,
        string? adapter = null,
        string? model = null)
    {
        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
        var request = new ExecutionRequest(
            executionId,
            workflowId,
            stepId,
            "stub-worker",
            Inputs: [],
            Outputs: [],
            Timeout,
            ArtifactManager.BuildEnvironment([], outputDirectory, artifactsRoot),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            Adapter: adapter,
            Model: model);

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request));
        return executionId;
    }

    private static WorkflowStepDefinition Step(
        StepId stepId,
        IReadOnlyList<StepId> dependsOn,
        int maxAttempts = 1,
        string worker = "stub-worker") =>
        new(stepId, worker, [], [], dependsOn, new RetryPolicy(maxAttempts));

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("crash-recovery-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings(string? adapter = null, string? model = null) => new()
    {
        ["stub-worker"] = new WorkerBinding.Process(ProcessContract, Target, Timeout, Adapter: adapter, Model: model),
    };

    /// <summary>
    /// #724's title case, distinct from the absent-worker arm above: the binding is PRESENT but
    /// resolution refuses (a lazily-resolved entry whose adapter is gone, #662). Every lookup
    /// throws, the way <c>WorkerBindingResolver.ResolveLazily</c>'s entries do.
    /// </summary>
    private sealed class RefusingBindings : IReadOnlyDictionary<string, WorkerBinding>
    {
        private sealed class TestResolutionRefusal(string message) : BatonFlowException(message);

        public WorkerBinding this[string key] => throw new TestResolutionRefusal($"No adapter for '{key}'.");
        public bool TryGetValue(string key, out WorkerBinding value) => throw new TestResolutionRefusal($"No adapter for '{key}'.");
        public bool ContainsKey(string key) => true;
        public int Count => 1;
        public IEnumerable<string> Keys => ["unresolvable-worker"];
        public IEnumerable<WorkerBinding> Values => throw new TestResolutionRefusal("enumerated");
        public IEnumerator<KeyValuePair<string, WorkerBinding>> GetEnumerator() => throw new TestResolutionRefusal("enumerated");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new TestResolutionRefusal("enumerated");
    }

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
