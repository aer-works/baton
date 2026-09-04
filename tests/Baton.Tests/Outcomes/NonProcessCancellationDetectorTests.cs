using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;

namespace Baton.Tests.Outcomes;

/// <summary>
/// M10 Phase 1 (vacuous with no process): unit tests proving
/// <see cref="NonProcessCancellationDetector.GetCancelledExecutions"/> finalizes only a pending
/// execution with an unfulfilled cancellation request that has no live Core process behind it — a
/// <see cref="WorkerBinding.NonProcess"/> step or a step-less supplementary execution — and leaves
/// a <see cref="WorkerBinding.Process"/> target's request untouched (Phase 2's delivery machinery).
/// </summary>
public class NonProcessCancellationDetectorTests
{
    private static readonly StepId Human = new("human");
    private static readonly StepId Process = new("process");
    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static readonly WorkerContract HumanContract = new("human-worker", [], [new ProducedOutput("revision.md")], []);
    private static readonly WorkerContract ProcessContract = new("process-worker", [], [], []);

    [Fact]
    public void A_running_non_process_step_with_an_unfulfilled_cancellation_request_is_cancelled()
    {
        var executionId = new ExecutionId("h1");
        var snapshot = SnapshotWith(Step(Human, "human-worker"));
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [new StepState(Human, StepStatus.Running, executionId, NoUpstream)],
            CancellationRequestedExecutionIds: [executionId]);
        var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

        var cancelled = NonProcessCancellationDetector.GetCancelledExecutions(state, snapshot, bindings);

        Assert.Equal([executionId], cancelled);
    }

    [Fact]
    public void A_running_non_process_step_with_no_cancellation_request_is_untouched()
    {
        var executionId = new ExecutionId("h1");
        var snapshot = SnapshotWith(Step(Human, "human-worker"));
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [new StepState(Human, StepStatus.Running, executionId, NoUpstream)]);
        var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

        var cancelled = NonProcessCancellationDetector.GetCancelledExecutions(state, snapshot, bindings);

        Assert.Empty(cancelled);
    }

    [Fact]
    public void A_running_process_bound_step_with_an_unfulfilled_cancellation_request_is_left_for_Phase_2()
    {
        var executionId = new ExecutionId("p1");
        var snapshot = SnapshotWith(Step(Process, "process-worker"));
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [new StepState(Process, StepStatus.Running, executionId, NoUpstream)],
            CancellationRequestedExecutionIds: [executionId]);
        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["process-worker"] = new WorkerBinding.Process(ProcessContract, new CoreDispatchTarget("stub", []), TimeSpan.FromSeconds(1)),
        };

        var cancelled = NonProcessCancellationDetector.GetCancelledExecutions(state, snapshot, bindings);

        Assert.Empty(cancelled);
    }

    [Fact]
    public void An_already_terminal_step_is_never_reported_even_if_named_by_a_stale_request()
    {
        // Belt-and-suspenders: StateProjector already excludes a terminal ExecutionId from
        // CancellationRequestedExecutionIds, but this detector also gates on Status == Running.
        var executionId = new ExecutionId("h1");
        var snapshot = SnapshotWith(Step(Human, "human-worker"));
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [new StepState(Human, StepStatus.Succeeded, executionId, NoUpstream)],
            CancellationRequestedExecutionIds: [executionId]);
        var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

        var cancelled = NonProcessCancellationDetector.GetCancelledExecutions(state, snapshot, bindings);

        Assert.Empty(cancelled);
    }

    [Fact]
    public void A_pending_step_less_execution_with_an_unfulfilled_cancellation_request_is_cancelled()
    {
        var executionId = new ExecutionId("supplement-1");
        var snapshot = SnapshotWith();
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [],
            StepLessExecutions: [new StepLessExecutionState(executionId, "human-worker")],
            CancellationRequestedExecutionIds: [executionId]);

        var cancelled = NonProcessCancellationDetector.GetCancelledExecutions(
            state, snapshot, new Dictionary<string, WorkerBinding>());

        Assert.Equal([executionId], cancelled);
    }

    // #1556 PR 1: ArrestableExecutions.All (the collapsed predicate this detector now filters) also
    // yields a quota-parked step (#1607 — Failed with a scheduled RetryNotBefore), unlike this
    // detector's own pre-collapse Running-only loop. That park path stays
    // MutationInterface.SettleParkedCancelIntentsAsync's, not this detector's — this pins the filter
    // that keeps it that way.
    [Fact]
    public void A_quota_parked_non_process_step_with_an_unfulfilled_cancellation_request_is_left_for_the_parked_settle_path()
    {
        var executionId = new ExecutionId("h1");
        var snapshot = SnapshotWith(Step(Human, "human-worker"));
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [new StepState(Human, StepStatus.Failed, executionId, NoUpstream, RetryNotBefore: DateTimeOffset.UtcNow.AddHours(1))],
            CancellationRequestedExecutionIds: [executionId]);
        var bindings = new Dictionary<string, WorkerBinding> { ["human-worker"] = new WorkerBinding.NonProcess(HumanContract) };

        var cancelled = NonProcessCancellationDetector.GetCancelledExecutions(state, snapshot, bindings);

        Assert.Empty(cancelled);
    }

    private static WorkflowStepDefinition Step(StepId stepId, string worker) =>
        new(stepId, worker, [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1));

    private static WorkflowDefinitionSnapshot SnapshotWith(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("cancellation-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);
}
