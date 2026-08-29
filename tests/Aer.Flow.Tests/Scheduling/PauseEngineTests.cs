using Aer.Flow.Domain;
using Aer.Flow.Scheduling;

namespace Aer.Flow.Tests.Scheduling;

public class PauseEngineTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static WorkflowDefinitionSnapshot SnapshotWithPausePoint(
        PausePoint? pausePoint, int architectMaxAttempts = 1) => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(architectMaxAttempts), PausePoint: pausePoint),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static StepState Terminal(
        StepId stepId, StepStatus status, ExecutionId executionId, int consecutiveFailureCount = 0,
        FailureClassification? classification = null, bool pauseRecorded = false) =>
        // Named from ConsecutiveFailureCount onward: StepState's tail is a run of defaulted
        // parameters, so a positional call silently shifts when one is inserted. #597 inserted
        // LatestFailureReason between the classification and the flag, and `pauseRecorded` bound to
        // it — caught here only because bool and string? don't convert. Had the neighbours shared a
        // type this would have compiled and quietly tested the wrong thing.
        new(stepId,
            status,
            executionId,
            NoUpstream,
            ConsecutiveFailureCount: consecutiveFailureCount,
            LatestFailureClassification: classification,
            PauseRecordedForLatestExecution: pauseRecorded);

    private static StepState Pending(StepId stepId) => new(stepId, StepStatus.Pending, LatestExecutionId: null, NoUpstream);

    [Fact]
    public void A_succeeded_PausePoint_step_owes_a_pause()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, executionId), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(new PausePoint([])));

        Assert.Equal([(Architect, executionId)], obligations);
    }

    [Fact]
    public void A_succeeded_step_without_a_PausePoint_owes_no_pause()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, executionId), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(pausePoint: null));

        Assert.Empty(obligations);
    }

    [Fact]
    public void A_PausePoint_step_already_paused_for_its_latest_execution_owes_no_further_pause()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Paused, executionId, pauseRecorded: true), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(new PausePoint([])));

        Assert.Empty(obligations);
    }

    [Fact]
    public void A_resumed_PausePoint_step_is_not_re_paused()
    {
        // Resume clears StepStatus.Paused back to the underlying terminal status, but the fact that
        // a pause already happened for this ExecutionId survives ("one resolving decision
        // per pause") — the projector's PauseRecordedForLatestExecution, not the transient Paused
        // status, is what this engine must consult.
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Succeeded, executionId, pauseRecorded: true), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(new PausePoint([])));

        Assert.Empty(obligations);
    }

    [Fact]
    public void A_running_PausePoint_step_owes_no_pause_yet()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Running, new ExecutionId("A1")), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(new PausePoint([])));

        Assert.Empty(obligations);
    }

    [Fact]
    public void A_failed_PausePoint_step_with_retry_budget_remaining_owes_no_pause_yet()
    {
        // The retry policy runs first: retrying is still available, so this attempt's round hasn't settled.
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Failed, new ExecutionId("A1"), consecutiveFailureCount: 1), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(
            state, SnapshotWithPausePoint(new PausePoint([]), architectMaxAttempts: 2));

        Assert.Empty(obligations);
    }

    [Fact]
    public void A_failed_PausePoint_step_with_an_exhausted_retry_budget_owes_a_pause()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Failed, executionId, consecutiveFailureCount: 2), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(
            state, SnapshotWithPausePoint(new PausePoint([]), architectMaxAttempts: 2));

        Assert.Equal([(Architect, executionId)], obligations);
    }

    [Fact]
    public void A_permanently_classified_failure_owes_a_pause_regardless_of_remaining_budget()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Terminal(
                    Architect, StepStatus.Failed, executionId, consecutiveFailureCount: 0, FailureClassification.Permanent),
                Pending(Critic),
            ]);

        var obligations = PauseEngine.GetPauseObligations(
            state, SnapshotWithPausePoint(new PausePoint([]), architectMaxAttempts: 5));

        Assert.Equal([(Architect, executionId)], obligations);
    }

    [Fact]
    public void A_cancelled_PausePoint_step_owes_a_pause()
    {
        var executionId = new ExecutionId("A1");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Terminal(Architect, StepStatus.Cancelled, executionId), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(new PausePoint([])));

        Assert.Equal([(Architect, executionId)], obligations);
    }

    [Fact]
    public void A_pending_PausePoint_step_with_no_execution_yet_owes_no_pause()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [Pending(Architect), Pending(Critic)]);

        var obligations = PauseEngine.GetPauseObligations(state, SnapshotWithPausePoint(new PausePoint([])));

        Assert.Empty(obligations);
    }
}
