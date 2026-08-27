namespace Aer.Flow.Domain;

/// <summary>
/// <c>FlowState = Project(EventStore, WorkflowDefinitionSnapshot)</c> (spec §12) — workflow state
/// reconstructed from event history, never from live process state, wall-clock time, or anything
/// not frozen inside an event (§13). Producing this from the event log is the State Projector's
/// job (M7 Phase 4); this type is only the shape it projects into.
/// </summary>
/// <param name="Status">
/// A pure projection of <paramref name="Steps"/> (spec §12), never a stored event (§5.2), letting a
/// caller distinguish why <c>StartWorkflowAsync</c>'s pump returned — finished vs. waiting on an
/// external decision (§17.1). Defaults to <see cref="WorkflowStatus.Running"/> for call sites that
/// construct a <see cref="FlowState"/> directly rather than through <c>StateProjector.Project</c>.
/// </param>
/// <param name="StepLessExecutions">
/// Step-less supplementary executions (spec §17.3) still awaiting completion: accepted, but with no
/// terminal event recorded for them yet. Never affects <paramref name="Status"/> or any
/// <see cref="StepState"/> — by construction (§12), a step-less execution belongs to no step.
/// </param>
/// <param name="CancellationRequestedExecutionIds">
/// <see cref="ExecutionId"/>s with a recorded <see cref="FlowEvent.CancellationRequested"/> and no
/// terminal event yet (spec §9 step 1) — the intent Flow still owes a resolution for, whether that
/// resolution is direct finalization for a non-process target (this milestone's Phase 1) or delivery
/// to a live Core process (Phase 2). An <see cref="ExecutionId"/> leaves this list the moment any
/// terminal event lands for it, the same "no terminal event yet" rule every other derived obligation
/// here already follows (§6, §13) — so a too-late request (§9 step 4) never appears here at all. A
/// list, not a set, for the same reason as <paramref name="StepLessExecutions"/>: this type is
/// serialized (see <c>WorkflowDefinitionTests.FlowState_projects_a_skeleton_per_step_status</c>),
/// and <see cref="IReadOnlySet{T}"/> has no default JSON-constructible implementation.
/// </param>
public sealed record FlowState(
    WorkflowDefinitionSnapshotId WorkflowDefinitionSnapshotId,
    IReadOnlyList<StepState> Steps,
    WorkflowStatus Status = WorkflowStatus.Running,
    IReadOnlyList<StepLessExecutionState>? StepLessExecutions = null,
    IReadOnlyList<ExecutionId>? CancellationRequestedExecutionIds = null)
{
    /// <summary>Defaults to empty rather than <c>null</c> for call sites that omit the constructor argument.</summary>
    public IReadOnlyList<StepLessExecutionState> StepLessExecutions { get; init; } = StepLessExecutions ?? [];

    /// <summary>Defaults to empty rather than <c>null</c> for call sites that omit the constructor argument.</summary>
    public IReadOnlyList<ExecutionId> CancellationRequestedExecutionIds { get; init; } =
        CancellationRequestedExecutionIds ?? [];
}

/// <summary>
/// A workflow's derived, whole-of-DAG status (spec §12) — computed from <see cref="StepState.Status"/>
/// across every step, never stored as its own event (§5.2, §17.1).
/// </summary>
public enum WorkflowStatus
{
    /// <summary>At least one step's latest attempt is still in flight (or Flow crashed before recording its outcome, §6).</summary>
    Running,

    /// <summary>No step is running, and at least one is idle at a <see cref="FlowEvent.WorkflowPaused"/> awaiting a decision (§17.1).</summary>
    Paused,

    /// <summary>The pump reached its fixed point: nothing running, nothing paused, nothing further to dispatch.</summary>
    Terminal,
}

/// <summary>
/// The status of a single step's most recent execution attempt. <see cref="StepStatus.Running"/> covers both
/// "genuinely still executing" and "Flow crashed before recording the outcome" — the two are
/// indistinguishable from the event log alone (spec §6) until a terminal event is observed.
/// </summary>
public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Paused,

    /// <summary>
    /// An external <see cref="DecisionType.Reject"/> resolved this step's pause (spec §17.2):
    /// terminally failed with retry foreclosed regardless of remaining budget, and — unlike
    /// <see cref="Failed"/> — reachable even from an underlying <see cref="Succeeded"/> outcome
    /// (the approval-gate "no"). Never a stored event; derived from
    /// <see cref="FlowEvent.WorkflowResumed"/> plus the decision it resolves (§5.2).
    /// </summary>
    Rejected,
}

/// <summary>A step's projected status, as of the most recent event concerning it (spec §11.3).</summary>
/// <param name="UpstreamExecutionIds">
/// The <see cref="ExecutionRequest.UpstreamExecutionIds"/> recorded on <paramref name="LatestExecutionId"/>'s
/// request — empty when the step has no execution yet. This is what the Dependency Resolver's
/// staleness check (§11.3 condition 2) compares against a dependency's current latest successful
/// <see cref="ExecutionId"/>.
/// </param>
/// <param name="ConsecutiveFailureCount">
/// The number of trailing consecutive <see cref="FlowEvent.ExecutionFailed"/> attempts for this step,
/// resetting to zero on <see cref="FlowEvent.ExecutionSucceeded"/> — the Retry Engine's input for
/// <c>RetryPolicy.MaxAttempts</c> (spec §10).
/// </param>
/// <param name="LatestFailureClassification">
/// The <see cref="Domain.FailureClassification"/> carried on the latest attempt's
/// <see cref="FlowEvent.ExecutionFailed"/> event; <c>null</c> when the latest attempt did not fail or
/// reported no classification, which every consumer treats as <see cref="Domain.FailureClassification.Retryable"/>
/// (spec §8.1).
/// </param>
/// <param name="LatestFailureReason">
/// The Flow-derived diagnostic carried on the latest attempt's <see cref="FlowEvent.ExecutionFailed"/>
/// event (spec §8.2) — which condition of §8's table was not met, and for a contract failure which
/// declared outputs were unsatisfied. Distinct from <paramref name="LatestFailureClassification"/>,
/// which is the worker's own retry hint rather than an account of what went wrong. <c>null</c> when
/// the latest attempt did not fail, or when it failed before this field existed; absent means "not
/// recorded", never "no reason was derivable".
/// </param>
/// <param name="PauseRecordedForLatestExecution">
/// Whether a <see cref="FlowEvent.WorkflowPaused"/> was ever appended for <paramref name="LatestExecutionId"/>
/// — distinct from <see cref="StepStatus.Paused"/>, which <see cref="FlowEvent.WorkflowResumed"/> clears
/// (spec §17.1). The Pause Engine consults this, not the currently-<c>Paused</c> status, so a resumed
/// execution is never re-paused.
/// </param>
/// <param name="PausedOutcome">
/// The underlying terminal <see cref="StepStatus"/> (<see cref="StepStatus.Succeeded"/>,
/// <see cref="StepStatus.Failed"/>, or <see cref="StepStatus.Cancelled"/>) that <paramref name="LatestExecutionId"/>
/// reached before it was masked to <see cref="StepStatus.Paused"/>; <c>null</c> whenever
/// <paramref name="Status"/> is not <see cref="StepStatus.Paused"/>. This is what the External
/// Decision Handler validates <see cref="DecisionType.RetryWithRevision"/>/<see cref="DecisionType.Reject"/>
/// against, since <see cref="Status"/> itself no longer carries that information while paused (§17.2).
/// </param>
/// <param name="PendingSupplementaryExecutionId">
/// A <see cref="DecisionType.RetryWithRevision"/> or <see cref="DecisionType.Supersede"/> decision's
/// <see cref="FlowEvent.ExternalDecisionRecorded.SupplementaryExecutionId"/>, still owed to this
/// step's next dispatch: recorded against this step (as referent for <c>RetryWithRevision</c>, or as
/// <see cref="FlowEvent.ExternalDecisionRecorded.TargetStepId"/> for <c>Supersede</c>) but not yet
/// carried by a newer <see cref="FlowEvent.ExecutionRequestAccepted"/> for it. A projected fact, not
/// handler state, so a crash between recording the decision and dispatching its consequence loses
/// nothing (§7, §13, §17.5).
/// </param>
/// <param name="IsPendingSupersedeTarget">
/// Whether a <see cref="DecisionType.Supersede"/> named this (already-<see cref="StepStatus.Succeeded"/>)
/// step as <see cref="FlowEvent.ExternalDecisionRecorded.TargetStepId"/> and no newer
/// <see cref="FlowEvent.ExecutionRequestAccepted"/> has been recorded for it since — the direct
/// consequence the Dependency Resolver dispatches without regard to §11.3's ordinary conditions,
/// since a superseded step is never "ready" through staleness alone (§17.5).
/// </param>
/// <param name="LinkedFromExecutionId">
/// <see cref="Domain.ExecutionRequest.LinkedFromExecutionId"/> carried on <paramref name="LatestExecutionId"/>'s
/// own request (issue #1359) — the prior execution <c>aer resume</c> continued to produce this one.
/// <c>null</c> for every step whose latest attempt was an ordinary dispatch or retry.
/// </param>
public sealed record StepState(
    StepId StepId,
    StepStatus Status,
    ExecutionId? LatestExecutionId,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds,
    int ConsecutiveFailureCount = 0,
    FailureClassification? LatestFailureClassification = null,
    string? LatestFailureReason = null,
    bool PauseRecordedForLatestExecution = false,
    StepStatus? PausedOutcome = null,
    ExecutionId? PendingSupplementaryExecutionId = null,
    bool IsPendingSupersedeTarget = false,
    DateTimeOffset? RetryNotBefore = null,
    int? RetryDelayMs = null,
    ExecutionId? RetryScheduledForExecutionId = null,
    DateTimeOffset? LatestExecutionFailedRetryNotBefore = null,
    ExecutionId? LinkedFromExecutionId = null);

/// <summary>
/// A step-less supplementary execution still awaiting completion (spec §17.3): minted outside the
/// DAG during a pause, by <c>MutationInterface.RecordSupplementaryExecutionAsync</c>, so it belongs
/// to no <see cref="StepId"/> and never appears among <see cref="FlowState.Steps"/>.
/// </summary>
public sealed record StepLessExecutionState(ExecutionId ExecutionId, string Worker);
