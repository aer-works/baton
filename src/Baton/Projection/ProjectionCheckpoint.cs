using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// A persisted projection checkpoint (#903 Scope 1): derived state recording the projected state
/// of a workflow execution along with the event-log offset (<see cref="EventOffset"/>) it corresponds to.
/// Replaying only events past <see cref="EventOffset"/> avoids O(history) re-projection on room open.
/// </summary>
public sealed record ProjectionCheckpoint(
    long EventOffset,
    ProjectionCheckpointState State,
    long ByteOffset = 0,
    int Version = 3);

/// <summary>
/// Serializable snapshot of <see cref="StateProjector"/>'s internal working dictionaries and sets.
/// </summary>
public sealed record ProjectionCheckpointState(
    Dictionary<StepId, ExecutionId> LatestExecutionIdByStepId,
    Dictionary<StepId, Dictionary<StepId, ExecutionId>> UpstreamExecutionIdsByStepId,
    Dictionary<ExecutionId, StepStatus> TerminalStatusByExecutionId,
    HashSet<ExecutionId> PausedExecutionIds,
    HashSet<ExecutionId> EverPausedExecutionIds,
    Dictionary<DecisionId, ExecutionId> ReferencedExecutionIdByDecisionId,
    Dictionary<DecisionId, DecisionType> DecisionTypeByDecisionId,
    Dictionary<DecisionId, StepId> TargetStepIdByDecisionId,
    Dictionary<DecisionId, ExecutionId> SupplementaryExecutionIdByDecisionId,
    Dictionary<ExecutionId, StepId> StepIdByExecutionId,
    Dictionary<StepId, int> ConsecutiveFailureCountByStepId,
    Dictionary<StepId, FailureClassification?> LatestFailureClassificationByStepId,
    Dictionary<StepId, string?> LatestFailureReasonByStepId,
    Dictionary<StepId, DateTimeOffset?> LatestExecutionFailedRetryNotBeforeByStepId,
    HashSet<ExecutionId> CancellationRequestedExecutionIds,
    List<StepLessExecutionState> StepLessExecutionsInOrder,
    Dictionary<StepId, ExecutionId> PendingSupplementaryExecutionIdByStepId,
    HashSet<StepId> PendingSupersedeTargetStepIds,
    Dictionary<StepId, DateTimeOffset> RetryNotBeforeByStepId,
    Dictionary<StepId, int> RetryDelayMsByStepId,
    Dictionary<StepId, ExecutionId> RetryScheduledForExecutionIdByStepId,
    HashSet<ExecutionId> SucceededExecutionIds,
    Dictionary<ExecutionId, ExecutionRequest> AcceptedRequestByExecutionId,
    HashSet<ExecutionId> CoreStartedExecutionIds,
    Dictionary<ExecutionId, CoreEvent.ExecutionExited> CoreExitedByExecutionId,
    Dictionary<StepId, int>? ExecutionCountByStepId = null,
    Dictionary<StepId, string?>? LatestCapturedResponseFileByStepId = null,
    Dictionary<StepId, List<string>?>? LatestUnsatisfiedOutputNamesByStepId = null,
    HashSet<StepId>? RetryForeclosedStepIds = null,
    Dictionary<StepId, string?>? IndeterminateReasonByStepId = null)
{
    public Dictionary<StepId, int> ExecutionCountByStepId { get; init; } = ExecutionCountByStepId ?? new();

    /// <summary>#1594, conductor-writes shape. Absent from an older checkpoint's serialized JSON coalesces to empty here, same replay-safety shape as <see cref="ExecutionCountByStepId"/> — no <see cref="ProjectionCheckpoint.Version"/> bump needed.</summary>
    public Dictionary<StepId, string?> LatestCapturedResponseFileByStepId { get; init; } = LatestCapturedResponseFileByStepId ?? new();

    public Dictionary<StepId, List<string>?> LatestUnsatisfiedOutputNamesByStepId { get; init; } = LatestUnsatisfiedOutputNamesByStepId ?? new();

    /// <summary>
    /// #1586 S1: which steps carry a projected <see cref="FlowEvent.StepRetryForeclosed"/> not since
    /// cleared. Absent from an older checkpoint's serialized JSON coalesces to empty here, the same
    /// trailing-optional replay-safety shape as <see cref="LatestCapturedResponseFileByStepId"/> above
    /// — no <see cref="ProjectionCheckpoint.Version"/> bump needed. <b>Load-bearing in
    /// <see cref="DeepCopy"/> specifically</b>: that method constructs a new instance positionally, so
    /// a member added only here (relying on this init default) would silently lose every foreclosure
    /// on the very next incremental-checkpoint resume — the exact landmine #1594's own
    /// <c>LatestCapturedResponseFileByStepId</c>/<c>LatestUnsatisfiedOutputNamesByStepId</c> pair hit
    /// first (#1606).
    /// </summary>
    public HashSet<StepId> RetryForeclosedStepIds { get; init; } = RetryForeclosedStepIds ?? new();

    /// <summary>
    /// #1623: which steps carry a projected <see cref="FlowEvent.VerifyFailed"/> or
    /// <see cref="FlowEvent.ExecutionArrested"/> not since reopened, and the diagnostic text to show for
    /// it — same trailing-optional replay-safety shape as <see cref="RetryForeclosedStepIds"/> above, and
    /// the same <see cref="DeepCopy"/> load-bearing note applies.
    /// </summary>
    public Dictionary<StepId, string?> IndeterminateReasonByStepId { get; init; } = IndeterminateReasonByStepId ?? new();

    public static ProjectionCheckpointState CreateEmpty() => new(
        new Dictionary<StepId, ExecutionId>(),
        new Dictionary<StepId, Dictionary<StepId, ExecutionId>>(),
        new Dictionary<ExecutionId, StepStatus>(),
        new HashSet<ExecutionId>(),
        new HashSet<ExecutionId>(),
        new Dictionary<DecisionId, ExecutionId>(),
        new Dictionary<DecisionId, DecisionType>(),
        new Dictionary<DecisionId, StepId>(),
        new Dictionary<DecisionId, ExecutionId>(),
        new Dictionary<ExecutionId, StepId>(),
        new Dictionary<StepId, int>(),
        new Dictionary<StepId, FailureClassification?>(),
        new Dictionary<StepId, string?>(),
        new Dictionary<StepId, DateTimeOffset?>(),
        new HashSet<ExecutionId>(),
        new List<StepLessExecutionState>(),
        new Dictionary<StepId, ExecutionId>(),
        new HashSet<StepId>(),
        new Dictionary<StepId, DateTimeOffset>(),
        new Dictionary<StepId, int>(),
        new Dictionary<StepId, ExecutionId>(),
        new HashSet<ExecutionId>(),
        new Dictionary<ExecutionId, ExecutionRequest>(),
        new HashSet<ExecutionId>(),
        new Dictionary<ExecutionId, CoreEvent.ExecutionExited>(),
        new Dictionary<StepId, int>());

    public ProjectionCheckpointState DeepCopy() => new(
        new Dictionary<StepId, ExecutionId>(LatestExecutionIdByStepId),
        UpstreamExecutionIdsByStepId.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<StepId, ExecutionId>(kvp.Value)),
        new Dictionary<ExecutionId, StepStatus>(TerminalStatusByExecutionId),
        new HashSet<ExecutionId>(PausedExecutionIds),
        new HashSet<ExecutionId>(EverPausedExecutionIds),
        new Dictionary<DecisionId, ExecutionId>(ReferencedExecutionIdByDecisionId),
        new Dictionary<DecisionId, DecisionType>(DecisionTypeByDecisionId),
        new Dictionary<DecisionId, StepId>(TargetStepIdByDecisionId),
        new Dictionary<DecisionId, ExecutionId>(SupplementaryExecutionIdByDecisionId),
        new Dictionary<ExecutionId, StepId>(StepIdByExecutionId),
        new Dictionary<StepId, int>(ConsecutiveFailureCountByStepId),
        new Dictionary<StepId, FailureClassification?>(LatestFailureClassificationByStepId),
        new Dictionary<StepId, string?>(LatestFailureReasonByStepId),
        new Dictionary<StepId, DateTimeOffset?>(LatestExecutionFailedRetryNotBeforeByStepId),
        new HashSet<ExecutionId>(CancellationRequestedExecutionIds),
        new List<StepLessExecutionState>(StepLessExecutionsInOrder),
        new Dictionary<StepId, ExecutionId>(PendingSupplementaryExecutionIdByStepId),
        new HashSet<StepId>(PendingSupersedeTargetStepIds),
        new Dictionary<StepId, DateTimeOffset>(RetryNotBeforeByStepId),
        new Dictionary<StepId, int>(RetryDelayMsByStepId),
        new Dictionary<StepId, ExecutionId>(RetryScheduledForExecutionIdByStepId),
        new HashSet<ExecutionId>(SucceededExecutionIds),
        new Dictionary<ExecutionId, ExecutionRequest>(AcceptedRequestByExecutionId),
        new HashSet<ExecutionId>(CoreStartedExecutionIds),
        new Dictionary<ExecutionId, CoreEvent.ExecutionExited>(CoreExitedByExecutionId),
        new Dictionary<StepId, int>(ExecutionCountByStepId),
        new Dictionary<StepId, string?>(LatestCapturedResponseFileByStepId),
        LatestUnsatisfiedOutputNamesByStepId.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is null ? null : new List<string>(kvp.Value)),
        new HashSet<StepId>(RetryForeclosedStepIds),
        new Dictionary<StepId, string?>(IndeterminateReasonByStepId));
}
