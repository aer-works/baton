using System.Text.Json.Serialization;

namespace Baton.Domain;

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
[JsonDerivedType(typeof(StepRetryForeclosed), "stepRetryForeclosed")]
[JsonDerivedType(typeof(ZeroOutputsDespiteSubstantialWork), "zeroOutputsDespiteSubstantialWork")]
[JsonDerivedType(typeof(StepRebound), "stepRebound")]
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
    public sealed record ExecutionSucceeded(
        ExecutionId ExecutionId) : FlowEvent;

    /// <summary>Flow has classified a completed execution as failed.</summary>
    /// <param name="Reason">
    /// A human-readable diagnostic computed once at classification time (see
    /// <see cref="Baton.Outcomes.OutcomeClassifier"/>), distinct from <paramref name="FailureClassification"/>'s
    /// self-reported retry hint. Nullable because history predates the field (#597): an older
    /// <c>flow.jsonl</c> line written before it existed still replays, with this null.
    /// <para>
    /// <b>The <c>= null</c> default is what makes that work, and it is load-bearing — do not remove
    /// it, and do not add a member here without one.</b> Since #604 the journal is read with
    /// <see cref="Baton.Store.FlowEventLogJson.Options"/>, under which a constructor parameter
    /// carrying no default is genuinely required and an absent one fails replay. That is deliberate:
    /// it is what makes a lost or renamed member loud instead of silent. See that type's remarks for
    /// the whole rule — this doc deliberately does not restate it, because an earlier version did and
    /// went on asserting the opposite after the behaviour changed.
    /// </para>
    /// </param>
    /// <param name="CapturedResponseFile">
    /// #1594: carries <c>Outcomes.OutputMaterializer.CapturedResponse.FileName</c> onto the durable
    /// record — <c>OutputMaterializer</c> (the class) explains why this exists at all, and its
    /// <c>CapturedResponse</c> type explains what pairing this with
    /// <paramref name="UnsatisfiedOutputNames"/> means. Null on every execution this mechanism did not
    /// touch, including all history predating it (#597's same replay reasoning applies to every
    /// additive field on this union) — a
    /// required (no-default) parameter here would fail replay of every older line, per this record's
    /// own remarks above.
    /// </param>
    /// <param name="UnsatisfiedOutputNames">
    /// <c>Outcomes.OutputMaterializer.CapturedResponse.UnsatisfiedOutputNames</c>, carried the same hop.
    /// </param>
    public sealed record ExecutionFailed(
        ExecutionId ExecutionId,
        FailureClassification? FailureClassification,
        string? Reason = null,
        DateTimeOffset? RetryNotBefore = null,
        string? CapturedResponseFile = null,
        IReadOnlyList<string>? UnsatisfiedOutputNames = null) : FlowEvent;

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

    /// <summary>
    /// #1586 S1: a scheduled retry (<see cref="StepRetryScheduled"/>) was voided without ever being
    /// dispatched — the missing primitive the state-truth design proposal on #1586 names: clearing
    /// <see cref="StepRetryScheduled.RetryNotBefore"/> alone would re-arm the step (an
    /// <see cref="FailureClassification.ExhaustedUntil"/> step bypasses <c>RetryPolicy.MaxAttempts</c>
    /// by design, 0026), so this is a foreclosure, not a clear. <see cref="Scheduling.RetryEngine.MayRetry"/>
    /// returns <c>false</c> once projected, which is what lets <see cref="Projection.StateProjector"/>'s
    /// deliverability predicate go <c>Terminal</c>. Reopened by the same two events that already clear
    /// <see cref="StepRetryScheduled"/>'s fields for a fresh attempt — <see cref="ExecutionRequestAccepted"/>
    /// and a <see cref="DecisionType.RetryWithRevision"/> <see cref="WorkflowResumed"/> — so a
    /// deliberate re-drive reopens the step and a foreclosure is never permanent. (A third event,
    /// <see cref="ExecutionCancelled"/>'s own park-abort clear (#1563), also clears those fields but
    /// does NOT reopen a foreclosure — it terminates the execution rather than re-arming the step, so
    /// there is nothing to reopen.)
    /// </summary>
    /// <param name="ForExecutionId">
    /// The execution whose retry obligation this forecloses. Guards the apply the same way
    /// <see cref="ExecutionCancelled"/>'s own retry-field clear already does (#1605): projected only
    /// when it still matches <see cref="Projection.ProjectionCheckpointState.RetryScheduledForExecutionIdByStepId"/>'s
    /// recorded value for <see cref="StepId"/> — a retry already re-scheduled for a NEWER execution of
    /// the same step must survive this event.
    /// </param>
    /// <param name="Reason">Why the retry was foreclosed — a diagnostic, never parsed back.</param>
    /// <param name="ForeclosedBy">
    /// Attribution for who/what recorded the foreclosure (e.g. <c>"settle"</c> once S2's verb exists).
    /// Nullable — this slice writes no producer, so every foreclosure a test fabricates today may
    /// legitimately omit it.
    /// </param>
    public sealed record StepRetryForeclosed(
        StepId StepId,
        ExecutionId ForExecutionId,
        string Reason,
        string? ForeclosedBy = null) : FlowEvent;

    /// <summary>
    /// #1586 S1 (the #1594 ruling's tripwire): a completed execution's own final usage line shows real
    /// work (turns and/or output tokens reported) while every one of its contract's declared outputs is
    /// simply missing — recorded independent of <see cref="ExecutionFailed"/>'s <c>Verdict</c>/
    /// <c>FailureClassification</c> so it fires whether or not <see cref="Outcomes.OutputMaterializer"/>'s
    /// response capture succeeded alongside it (<see cref="Outcomes.OutcomeClassification.SubstantialWorkNoOutputsEvidence"/>
    /// explains the predicate). A diagnostic fact only — nothing in <see cref="Projection.StateProjector"/>
    /// changes <see cref="StepState"/> because of this event; it exists to be loud and durable, not to
    /// drive scheduling.
    /// </summary>
    public sealed record ZeroOutputsDespiteSubstantialWork(
        ExecutionId ExecutionId,
        string Evidence) : FlowEvent;

    /// <summary>
    /// S6 (spec/baton.md §3, #802 section 3.3, pulled forward by #1583): records that a step's execution was rebound to a different
    /// adapter/model binding. When crash-recovery resubmission encounters a divergent binding
    /// (the current <c>bindings.json</c> differs from the accepted request's recorded <see cref="ExecutionRequest.Adapter"/>
    /// and/or <see cref="ExecutionRequest.Model"/>), Flow journals this event before dispatching so that
    /// usage attribution (<see cref="Status.ExecutionUsageProjector"/>) re-attributes this execution to the
    /// new binding rather than trusting the pre-crash frozen request.
    /// </summary>
    /// <param name="StepId">Which step was rebound.</param>
    /// <param name="ForExecutionId">The execution whose binding diverged.</param>
    /// <param name="PreviousAdapter">The adapter originally recorded on the accepted request.</param>
    /// <param name="PreviousModel">The model originally recorded on the accepted request.</param>
    /// <param name="NewAdapter">The new adapter resolved from the current worker bindings.</param>
    /// <param name="NewModel">The new model resolved from the current worker bindings.</param>
    /// <param name="Reason">Why the step was rebound (diagnostic).</param>
    public sealed record StepRebound(
        StepId StepId,
        ExecutionId ForExecutionId,
        string? PreviousAdapter = null,
        string? PreviousModel = null,
        string? NewAdapter = null,
        string? NewModel = null,
        string? Reason = null) : FlowEvent;
}
