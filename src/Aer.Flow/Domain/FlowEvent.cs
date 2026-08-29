using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// The <c>flow.jsonl</c> event discriminated union — Flow's exclusive half of the Event Store.
/// There is deliberately no workflow-level transition event: workflow-level
/// status is a pure projection of these events plus the <see cref="WorkflowDefinitionSnapshot"/>,
/// never a stored event.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(ExecutionRequestAccepted), "executionRequestAccepted")]
[JsonDerivedType(typeof(ExecutionRequestRejected), "executionRequestRejected")]
[JsonDerivedType(typeof(ExecutionSucceeded), "executionSucceeded")]
[JsonDerivedType(typeof(ExecutionFailed), "executionFailed")]
[JsonDerivedType(typeof(ExecutionCancelled), "executionCancelled")]
[JsonDerivedType(typeof(CancellationRequested), "cancellationRequested")]
[JsonDerivedType(typeof(WorkflowPaused), "workflowPaused")]
[JsonDerivedType(typeof(ExternalDecisionRecorded), "externalDecisionRecorded")]
[JsonDerivedType(typeof(WorkflowResumed), "workflowResumed")]
[JsonDerivedType(typeof(StepRetryScheduled), "stepRetryScheduled")]
public abstract record FlowEvent
{
    private FlowEvent()
    {
    }

    /// <summary>Flow has admitted this request for execution (pre-execution, admission control).</summary>
    public sealed record ExecutionRequestAccepted(
        ExecutionRequest Request,
        int? EnginePid = null,
        DateTimeOffset? EngineStartTime = null) : FlowEvent;

    /// <summary>Flow declined to submit this request, e.g. a concurrency cap.</summary>
    public sealed record ExecutionRequestRejected(ExecutionId ExecutionId, string Reason) : FlowEvent;

    /// <summary>Flow has classified a completed execution as successful.</summary>
    public sealed record ExecutionSucceeded(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>Flow has classified a completed execution as failed.</summary>
    /// <param name="Reason">
    /// A human-readable diagnostic computed once at classification time (see
    /// <see cref="Aer.Flow.Outcomes.OutcomeClassifier"/>), distinct from <paramref name="FailureClassification"/>'s
    /// self-reported retry hint. Nullable because history predates the field (#597): an older
    /// <c>flow.jsonl</c> line written before it existed still replays, with this null.
    /// <para>
    /// <b>The <c>= null</c> default is what makes that work, and it is load-bearing — do not remove
    /// it, and do not add a member here without one.</b> Since #604 the journal is read with
    /// <see cref="Aer.Flow.Store.FlowEventLogJson.Options"/>, under which a constructor parameter
    /// carrying no default is genuinely required and an absent one fails replay. That is deliberate:
    /// it is what makes a lost or renamed member loud instead of silent. See that type's remarks for
    /// the whole rule — this doc deliberately does not restate it, because an earlier version did and
    /// went on asserting the opposite after the behaviour changed.
    /// </para>
    /// </param>
    public sealed record ExecutionFailed(
        ExecutionId ExecutionId,
        FailureClassification? FailureClassification,
        string? Reason = null,
        DateTimeOffset? RetryNotBefore = null) : FlowEvent;

    /// <summary>Flow has classified a completed execution as cancelled.</summary>
    public sealed record ExecutionCancelled(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// Flow has forwarded an on-demand cancellation request toward Core for a still-running
    /// execution. Recorded and fsync'd before the request reaches Core, per the
    /// intent-first write sequence rule.
    /// </summary>
    public sealed record CancellationRequested(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// A step declaring <see cref="PausePoint"/> reached a terminal outcome; Flow is idle
    /// until a matching <see cref="FlowEvent.ExternalDecisionRecorded"/> arrives.
    /// </summary>
    public sealed record WorkflowPaused(ExecutionId ExecutionId, StepId StepId) : FlowEvent;

    /// <summary>An external party recorded a decision in response to a <see cref="WorkflowPaused"/>.</summary>
    /// <param name="ReferencedExecutionId">Which execution's outcome this decision responds to.</param>
    /// <param name="TargetStepId">Required only for <see cref="DecisionType.Supersede"/>.</param>
    /// <param name="SupplementaryExecutionId">Optional for <see cref="DecisionType.RetryWithRevision"/>; required for <see cref="DecisionType.Supersede"/>.</param>
    /// <param name="Decider">Attribution info for the decider. Defaults to human.</param>
    public sealed record ExternalDecisionRecorded(
        DecisionId DecisionId,
        ExecutionId ReferencedExecutionId,
        DecisionType DecisionType,
        StepId? TargetStepId,
        ExecutionId? SupplementaryExecutionId,
        DeciderInfo? Decider = null) : FlowEvent
    {
        [JsonIgnore]
        public DeciderInfo EffectiveDecider => Decider ?? DeciderInfo.DefaultHuman;
    }



    /// <summary>The workflow is no longer paused following the referenced decision.</summary>
    public sealed record WorkflowResumed(DecisionId DecisionId) : FlowEvent;

    /// <summary>Flow has scheduled a retry backoff deadline for a failed step attempt.</summary>
    public sealed record StepRetryScheduled(
        StepId StepId,
        ExecutionId ForExecutionId,
        DateTimeOffset RetryNotBefore,
        int RetryDelayMs) : FlowEvent;
}
