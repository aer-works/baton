using Baton.Domain;
using Baton.Mutation;

namespace Baton.Tests.Mutation;

/// <summary>
/// M9 Phase 2 (External Decision Handler): the full validation matrix for
/// <see cref="ExternalDecisionValidator.Validate"/> against hand-built <see cref="FlowState"/> —
/// no event log, no dispatch. Mirrors <see cref="Scheduling.PauseEngineTests"/>'s and
/// <see cref="Scheduling.RetryEngineTests"/>'s style: a pure function tested directly.
/// </summary>
public class ExternalDecisionValidatorTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    private static readonly ExecutionId ArchitectExecutionId = new("A1");
    private static readonly ExecutionId CriticExecutionId = new("C1");
    private static readonly ExecutionId PublisherExecutionId = new("P1");

    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();
    private static readonly IReadOnlySet<ExecutionId> NoSucceededExecutions = new HashSet<ExecutionId>();

    private static WorkflowDefinitionSnapshot Snapshot(PausePoint? criticPausePoint) => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic-publisher"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(
                Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                PausePoint: criticPausePoint),
            new WorkflowStepDefinition(Publisher, "publisher", ["review"], ["summary"], DependsOn: [Critic], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static StepState Succeeded(StepId stepId, ExecutionId executionId) =>
        new(stepId, StepStatus.Succeeded, executionId, NoUpstream);

    private static StepState Paused(StepId stepId, ExecutionId executionId, StepStatus pausedOutcome) =>
        new(stepId, StepStatus.Paused, executionId, NoUpstream, PauseRecordedForLatestExecution: true, PausedOutcome: pausedOutcome);

    private static StepState Pending(StepId stepId) => new(stepId, StepStatus.Pending, LatestExecutionId: null, NoUpstream);

    private static StepState Running(StepId stepId, ExecutionId executionId) =>
        new(stepId, StepStatus.Running, executionId, NoUpstream);

    /// <summary>See <see cref="ExternalDecisionValidator"/>'s #815 remarks.</summary>
    private static StepState FailedWithScheduledRetry(StepId stepId, ExecutionId executionId, DateTimeOffset retryNotBefore) =>
        new(stepId, StepStatus.Failed, executionId, NoUpstream, RetryNotBefore: retryNotBefore, RetryDelayMs: 60_000);

    private static StepState Failed(StepId stepId, ExecutionId executionId) =>
        new(stepId, StepStatus.Failed, executionId, NoUpstream);

    /// <summary>
    /// A step a prior Supersede already named as <c>TargetStepId</c>, whose consequence (the
    /// re-dispatch) has not landed yet — <c>Status</c> still reads the stale pre-Supersede
    /// <see cref="StepStatus.Succeeded"/> (StateProjector only advances it once a fresh
    /// <c>ExecutionRequestAccepted</c> is recorded for the step), with
    /// <see cref="StepState.IsPendingSupersedeTarget"/> the only signal a second Supersede against
    /// it is currently illegal (M23 Phase 2, #271).
    /// </summary>
    private static StepState PendingSupersede(StepId stepId, ExecutionId executionId) =>
        new(stepId, StepStatus.Succeeded, executionId, NoUpstream, IsPendingSupersedeTarget: true);

    private const string RoomDirectoryPath = "/rooms/room-1";

    private static void Validate(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        DecisionType decisionType,
        ExecutionId referencedExecutionId,
        StepId? targetStepId = null,
        ExecutionId? supplementaryExecutionId = null,
        IReadOnlySet<ExecutionId>? succeededExecutionIds = null) =>
        ExternalDecisionValidator.Validate(
            state, snapshot, succeededExecutionIds ?? NoSucceededExecutions, referencedExecutionId, decisionType,
            targetStepId, supplementaryExecutionId, RoomDirectoryPath);

    private static StepState PausedAwaitingResolution(StepId stepId, ExecutionId executionId, StepStatus pausedOutcome) =>
        new(
            stepId, StepStatus.Paused, executionId, NoUpstream, PauseRecordedForLatestExecution: true,
            PausedOutcome: pausedOutcome, IndeterminateAwaitingResolution: true,
            IndeterminateProducer: IndeterminateProducer.CapturedResponse);

    [Fact]
    public void Resume_against_a_currently_paused_execution_is_valid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.Resume, CriticExecutionId));

        Assert.Null(exception);
    }

    [Fact]
    public void Reject_against_a_currently_paused_execution_is_valid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.Reject, CriticExecutionId));

        Assert.Null(exception);
    }

    [Fact]
    public void A_decision_against_a_paused_step_with_an_unresolved_indeterminate_capture_is_refused()
    {
        // #1655 ruling (option 1) — see ExternalDecisionValidator's own remarks for the mechanism.
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                PausedAwaitingResolution(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        var exception = Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.Resume, CriticExecutionId));

        Assert.Contains(RoomDirectoryPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(Critic.Value, exception.Message, StringComparison.Ordinal);
        Assert.Contains("baton resolve", exception.Message, StringComparison.Ordinal);
        // Review of #1705: the room can hold more than one unresolved capture, and ResolveCommand
        // refuses a room-level resolve then -- the message must name the execution it already knows.
        Assert.Contains($"--execution {CriticExecutionId}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decision_against_the_same_step_is_admissible_once_CaptureResolved_clears_the_flag()
    {
        // Polarity partner: once resolution has cleared IndeterminateAwaitingResolution (the same
        // projection CaptureResolved drives in StateProjector), the ordinary Paused admission applies
        // again — decide is not permanently locked out, only until resolved.
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.Resume, CriticExecutionId));

        Assert.Null(exception);
    }

    [Fact]
    public void A_decision_against_an_unknown_ExecutionId_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.Resume, new ExecutionId("no-such-execution")));
    }

    [Fact]
    public void A_decision_against_an_execution_that_is_not_currently_paused_is_invalid()
    {
        // Also covers "one resolving decision per pause": a second decision against an
        // already-resumed execution finds it back in a non-Paused status.
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Succeeded(Critic, CriticExecutionId),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.Resume, CriticExecutionId));
    }

    [Theory]
    [InlineData(DecisionType.Resume)]
    [InlineData(DecisionType.Reject)]
    [InlineData(DecisionType.RetryWithRevision)]
    public void A_TargetStepId_is_invalid_on_any_decision_type_other_than_Supersede(DecisionType decisionType)
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(new PausePoint([Architect])), decisionType, CriticExecutionId, targetStepId: Architect));
    }

    [Fact]
    public void RetryWithRevision_against_a_step_that_has_not_yet_succeeded_is_valid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.RetryWithRevision, CriticExecutionId));

        Assert.Null(exception);
    }

    [Fact]
    public void RetryWithRevision_against_a_step_that_already_succeeded_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.RetryWithRevision, CriticExecutionId));
    }

    [Fact]
    public void RetryWithRevision_with_no_SupplementaryExecutionId_is_valid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() =>
            Validate(state, Snapshot(new PausePoint([])), DecisionType.RetryWithRevision, CriticExecutionId, supplementaryExecutionId: null));

        Assert.Null(exception);
    }

    [Fact]
    public void RetryWithRevision_with_a_SupplementaryExecutionId_naming_a_successful_execution_is_valid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() => Validate(
            state, Snapshot(new PausePoint([])), DecisionType.RetryWithRevision, CriticExecutionId,
            supplementaryExecutionId: ArchitectExecutionId, succeededExecutionIds: new HashSet<ExecutionId> { ArchitectExecutionId }));

        Assert.Null(exception);
    }

    [Fact]
    public void RetryWithRevision_with_a_SupplementaryExecutionId_naming_no_recorded_success_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Failed),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([])), DecisionType.RetryWithRevision, CriticExecutionId,
            supplementaryExecutionId: new ExecutionId("never-happened")));
    }

    [Fact]
    public void Supersede_with_no_TargetStepId_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            supplementaryExecutionId: CriticExecutionId, succeededExecutionIds: new HashSet<ExecutionId> { CriticExecutionId }));
    }

    [Fact]
    public void Supersede_targeting_a_StepId_outside_the_declared_SupersedeTargets_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        // SupersedeTargets is empty: no target is available at all.
        Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: CriticExecutionId,
            succeededExecutionIds: new HashSet<ExecutionId> { CriticExecutionId }));
    }

    [Fact]
    public void Supersede_targeting_a_step_that_has_not_succeeded_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Pending(Architect),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: CriticExecutionId,
            succeededExecutionIds: new HashSet<ExecutionId> { CriticExecutionId }));
    }

    [Fact]
    public void Supersede_with_no_SupplementaryExecutionId_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: null));
    }

    [Fact]
    public void Supersede_with_a_SupplementaryExecutionId_naming_no_recorded_success_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: new ExecutionId("never-happened")));
    }

    [Fact]
    public void A_fully_valid_Supersede_naming_the_pausing_steps_own_execution_as_the_supplement_is_valid()
    {
        // The critic produced its feedback artifact in an execution of its own.
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: CriticExecutionId,
            succeededExecutionIds: new HashSet<ExecutionId> { CriticExecutionId }));

        Assert.Null(exception);
    }

    /// <summary>
    /// M23 Phase 2's decision of record (#271): a Supersede naming a target that already has a
    /// pending, not-yet-dispatched Supersede consequence is rejected — closing the race a crash
    /// between recording <c>WorkflowResumed</c> and the pump's re-dispatch would otherwise reopen
    /// (see <see cref="PendingSupersede"/>'s remarks; StateProjector's
    /// <c>pendingSupplementaryExecutionIdByStepId</c> would silently overwrite the first decision's
    /// supplement with the second's rather than raising any error on its own).
    /// </summary>
    [Fact]
    public void Supersede_targeting_a_step_with_an_already_pending_Supersede_consequence_is_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                PendingSupersede(Architect, ArchitectExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        var ex = Assert.Throws<InvalidExternalDecisionException>(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: CriticExecutionId,
            succeededExecutionIds: new HashSet<ExecutionId> { CriticExecutionId }));

        Assert.Contains("pending Supersede", ex.Message);
    }

    /// <summary>
    /// M23 Phase 2's other half of the same decision (#271): once a target's prior Supersede
    /// consequence has actually settled — <see cref="StepState.IsPendingSupersedeTarget"/> false
    /// again, <c>Status</c> back to a fresh <see cref="StepStatus.Succeeded"/> — a second Supersede
    /// naming it is legal. This is the load-bearing case for M24's chat primitive (repeatedly
    /// superseding one step); the end-to-end chain itself is proved live in
    /// <c>PauseDecisionSupersedeHumanEndToEndTests</c>.
    /// </summary>
    [Fact]
    public void Supersede_targeting_a_step_whose_prior_Supersede_consequence_has_already_settled_is_valid()
    {
        var settledExecutionId = new ExecutionId("A2");
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, settledExecutionId),
                Paused(Critic, CriticExecutionId, StepStatus.Succeeded),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() => Validate(
            state, Snapshot(new PausePoint([Architect])), DecisionType.Supersede, CriticExecutionId,
            targetStepId: Architect, supplementaryExecutionId: CriticExecutionId,
            succeededExecutionIds: new HashSet<ExecutionId> { CriticExecutionId }));

        Assert.Null(exception);
    }

    // --- #815: operator retry-now for a quota-parked (Failed + scheduled-retry) step ---

    /// <summary>The scenario <see cref="ExternalDecisionValidator"/>'s #815 remarks describe, with no <c>PausePoint</c> declared.</summary>
    [Fact]
    public void RetryWithRevision_against_a_Failed_step_with_a_scheduled_retry_and_no_PausePoint_is_valid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                FailedWithScheduledRetry(Critic, CriticExecutionId, DateTimeOffset.UnixEpoch.AddHours(1)),
                Pending(Publisher),
            ]);

        var exception = Record.Exception(() =>
            Validate(state, Snapshot(criticPausePoint: null), DecisionType.RetryWithRevision, CriticExecutionId));

        Assert.Null(exception);
    }

    /// <summary>
    /// Polarity: a Running step (no terminal outcome at all, so certainly no scheduled retry)
    /// still gets the ordinary refusal — the widening in #815 does not touch this case.
    /// </summary>
    [Fact]
    public void RetryWithRevision_against_a_Running_step_is_still_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Running(Critic, CriticExecutionId),
                Pending(Publisher),
            ]);

        var ex = Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(criticPausePoint: null), DecisionType.RetryWithRevision, CriticExecutionId));

        Assert.Equal(
            $"Execution '{CriticExecutionId}' is not the currently paused latest attempt of any step.", ex.Message);
    }

    /// <summary>
    /// Polarity: a Succeeded (non-Paused) step still gets the ordinary refusal — unchanged from
    /// before #815, restated here alongside the other #815 polarity arms for a single reading.
    /// </summary>
    [Fact]
    public void RetryWithRevision_against_a_Succeeded_non_paused_step_is_still_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Succeeded(Critic, CriticExecutionId),
                Pending(Publisher),
            ]);

        var ex = Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(criticPausePoint: null), DecisionType.RetryWithRevision, CriticExecutionId));

        Assert.Equal(
            $"Execution '{CriticExecutionId}' is not the currently paused latest attempt of any step.", ex.Message);
    }

    /// <summary>
    /// Polarity: a Failed step with NO scheduled retry (RetryPolicy already exhausted, or a
    /// Permanent classification that never schedules one) still gets the ordinary refusal — #815's
    /// widening is scoped to "Failed AND a scheduled retry", never "Failed" alone.
    /// </summary>
    [Fact]
    public void RetryWithRevision_against_a_Failed_step_with_no_scheduled_retry_is_still_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                Failed(Critic, CriticExecutionId),
                Pending(Publisher),
            ]);

        var ex = Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(criticPausePoint: null), DecisionType.RetryWithRevision, CriticExecutionId));

        Assert.Equal(
            $"Execution '{CriticExecutionId}' is not the currently paused latest attempt of any step.", ex.Message);
    }

    /// <summary>
    /// Polarity: the scope decision names RetryWithRevision only — Reject against the same
    /// quota-parked step must still refuse with the ordinary wording.
    /// </summary>
    [Fact]
    public void Reject_against_a_Failed_step_with_a_scheduled_retry_is_still_invalid()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snapshot-1"),
            [
                Succeeded(Architect, ArchitectExecutionId),
                FailedWithScheduledRetry(Critic, CriticExecutionId, DateTimeOffset.UnixEpoch.AddHours(1)),
                Pending(Publisher),
            ]);

        var ex = Assert.Throws<InvalidExternalDecisionException>(() =>
            Validate(state, Snapshot(criticPausePoint: null), DecisionType.Reject, CriticExecutionId));

        Assert.Equal(
            $"Execution '{CriticExecutionId}' is not the currently paused latest attempt of any step.", ex.Message);
    }
}
