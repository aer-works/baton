using Aer.Flow.Domain;

namespace Aer.RoomSession;

/// <summary>
/// The read-model surface <see cref="Aer.Flow.Domain.FlowState"/> deliberately omits: every
/// execution a step has ever gone through (not just its latest, per <c>StepState.LatestExecutionId</c>),
/// plus every recorded external decision. Walked directly from the same Flow Event Store
/// <see cref="RoomProjectionLoader"/> already reads (spec §5.1), never a new stored fact — this is
/// presentation-layer history, not an execution-authority concern, so it is owned by the
/// presentation layer (<c>Aer.Ui.Core</c> originally, this project since #1412 archived it) rather
/// than <c>Aer.Flow</c>, the same boundary M14 Phase 1's <see cref="RoomProjection"/> already
/// drew (UI spec §2).
/// </summary>
public sealed record ExecutionHistory(
    IReadOnlyDictionary<StepId, IReadOnlyList<ExecutionAttempt>> AttemptsByStepId,
    IReadOnlyList<ExecutionAttempt> StepLessExecutions,
    IReadOnlyList<DecisionRecord> Decisions);

/// <summary>
/// A single execution's projected outcome as of the latest event concerning it — the same
/// vocabulary <see cref="StepStatus"/> uses for a step's *latest* attempt (spec §12), applied here
/// to every attempt a step, or a step-less supplementary/human execution (spec §17.3), has ever had.
/// </summary>
/// <param name="IsNonProcess">
/// Whether this execution's <see cref="ExecutionRequest.Timeout"/> was <c>null</c> — the recorded
/// signal for a <c>Mutation.WorkerBinding.NonProcess</c> dispatch (spec §17.3: nothing runs as a
/// Core process, so nothing can time out). The only durable fact that distinguishes a human/non-process
/// execution from an ordinary one once bindings are no longer in scope.
/// </param>
/// <param name="Reason">
/// The diagnostic recorded on the terminal <see cref="FlowEvent.ExecutionFailed"/>, if this attempt
/// failed and the event carries one — null for an attempt with no recorded failure, and for a
/// failure recorded before this field existed.
/// <para>
/// Deliberately not "null for any non-failed status": the reason is keyed by execution, while a
/// later decision or a standing pause overrides that execution's <i>status</i> without clearing it.
/// A step that exhausts its retries is paused by the Pause Engine while already
/// <see cref="StepStatus.Failed"/>, so a <see cref="StepStatus.Paused"/> or
/// <see cref="StepStatus.Rejected"/> attempt carrying the reason it failed for is ordinary rather
/// than anomalous — and is the case where a person is being asked to decide and most needs it.
/// </para>
/// </param>
public sealed record ExecutionAttempt(
    ExecutionId ExecutionId,
    string Worker,
    StepStatus Status,
    FailureClassification? FailureClassification,
    bool IsNonProcess,
    string? Reason = null);

/// <summary>
/// An externally recorded decision (spec §17.2), paired with whether it has since been resolved by
/// a matching <see cref="FlowEvent.WorkflowResumed"/> — durable fact, not handler state, so a crash
/// between the two (however narrow that window is in practice) still renders honestly rather than
/// assuming resolution.
/// </summary>
public sealed record DecisionRecord(
    DecisionId DecisionId,
    ExecutionId ReferencedExecutionId,
    DecisionType DecisionType,
    StepId? TargetStepId,
    ExecutionId? SupplementaryExecutionId,
    bool Resolved);
