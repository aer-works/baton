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
[JsonDerivedType(typeof(VerifyStarted), "verifyStarted")]
[JsonDerivedType(typeof(VerifyPassed), "verifyPassed")]
[JsonDerivedType(typeof(VerifyFailed), "verifyFailed")]
[JsonDerivedType(typeof(VerifyNotRun), "verifyNotRun")]
[JsonDerivedType(typeof(VerifyDeclarationIgnored), "verifyDeclarationIgnored")]
[JsonDerivedType(typeof(VerifyDeclarationUnreviewed), "verifyDeclarationUnreviewed")]
[JsonDerivedType(typeof(ExecutionArrested), "executionArrested")]
[JsonDerivedType(typeof(ExecutionIndeterminate), "executionIndeterminate")]
[JsonDerivedType(typeof(CaptureResolved), "captureResolved")]
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
    /// <param name="WorkspaceChanged">
    /// #1622/#1390: carried into <see cref="Projection.ProjectionCheckpointState.WorkspaceChangedByStepId"/>.
    /// Nullable because history predates the field: an older <c>flow.jsonl</c> line written before it
    /// existed still replays, with this null, the same "history predates the field" shape <see
    /// cref="ExecutionFailed.Reason"/> already documents.
    /// </param>
    /// <param name="Hollow">Companion to <paramref name="WorkspaceChanged"/>; see its own remarks.</param>
    /// <param name="HollowReason">Non-null only when <paramref name="Hollow"/> is true.</param>
    public sealed record ExecutionSucceeded(
        ExecutionId ExecutionId,
        bool? WorkspaceChanged = null,
        bool? Hollow = null,
        string? HollowReason = null) : FlowEvent;

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
    /// #1623 (contract: <c>spec/baton.md</c> §3): the engine has begun running a
    /// role's declared verify command (<c>pixi run gates-quiet</c> for <c>implement</c>) against a
    /// worker execution that exited 0 with its output contract satisfied. Diagnostic only, the same
    /// "durable fact, no <see cref="StepState"/> consequence" shape as
    /// <see cref="ZeroOutputsDespiteSubstantialWork"/> — <see cref="VerifyPassed"/>/<see cref="VerifyFailed"/>
    /// record how it ended.
    /// </summary>
    public sealed record VerifyStarted(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>#1623: the verify command <see cref="VerifyStarted"/> named exited 0. Diagnostic only.</summary>
    public sealed record VerifyPassed(ExecutionId ExecutionId) : FlowEvent;

    /// <summary>
    /// #1623 (contract: <c>spec/baton.md</c> §3): the role's verify command exited non-zero after the
    /// worker itself exited 0 with a satisfied output contract. Settles the step
    /// <see cref="Status.WorkflowOutcome.Indeterminate"/> — the ruling's own words, "never a blind
    /// retry"; the conductor resolves it. <paramref name="FailingMembers"/>/<paramref name="Tail"/>
    /// mirror <c>tools/gates/gates.py</c>'s own <c>--quiet</c> shape (member names from its
    /// <c>summarise()</c> line, plus a bounded output tail) — never a full log dump.
    /// </summary>
    /// <param name="FailingMembers">Which gate members failed, by name — empty/null if the verify
    /// command reports no per-member breakdown.</param>
    /// <param name="Tail">Each named failing member's OWN captured output (#1701) — see
    /// <see cref="Mutation.VerifyRunner"/>'s own remarks for why a blind tail of the whole run isn't
    /// this, and what happens when the shape isn't recognized.</param>
    /// <param name="Kind">#1623 / F3: whether the failure was broken gates, a timeout, a cancellation, or an engine restart.</param>
    public sealed record VerifyFailed(
        ExecutionId ExecutionId,
        IReadOnlyList<string>? FailingMembers = null,
        string? Tail = null,
        VerifyFailedKind Kind = VerifyFailedKind.GatesFailed) : FlowEvent;

    /// <summary>
    /// #1702 — spec/baton.md §3's not-run outcome:
    /// <see cref="Mutation.VerifyCommandResolver.CheckRunnableAsync"/>'s pre-flight probe found the
    /// resolved verify command not runnable, so it was never spawned. Diagnostic only, same "no
    /// <see cref="Status.WorkflowOutcome.Indeterminate"/> consequence" shape as <see cref="VerifyPassed"/>
    /// — the execution's own already-<c>Succeeded</c> classification decides the room word unassisted.
    /// Never emitted alongside <see cref="VerifyStarted"/> for the same execution, so
    /// <see cref="ProjectionCheckpointState.UnmatchedVerifyExecutionIds"/> and the #1608
    /// <c>EngineRestart</c> recovery path are both untouched by this arm.
    /// </summary>
    /// <param name="Reason"><see cref="Mutation.VerifyCommandResolver"/>'s own verdict text, never re-derived here.</param>
    public sealed record VerifyNotRun(ExecutionId ExecutionId, string Reason) : FlowEvent;

    /// <summary>
    /// #1708 H1: the workspace's working-tree <c>.baton/verify</c> differed from the one committed in
    /// <c>HEAD</c> when this execution was dispatched, so the working-tree file was IGNORED and the
    /// committed declaration (or, if there is none, the role default) decided what verify ran. The
    /// self-verification boundary made audible: a worker can write that file, and this says when one
    /// did — or, just as often, that a legitimate declaration was never committed and therefore never
    /// took effect.
    /// <para>
    /// <b>Diagnostic only, and deliberately terminal as a record.</b> Same shape as
    /// <see cref="VerifyStarted"/>/<see cref="VerifyPassed"/>: no <see cref="StepState"/> field, no
    /// <c>WorkflowStatusView</c> surface, no <c>fleet_status</c> plumbing, no
    /// <see cref="Status.WorkflowOutcome"/> consequence. It changes no verdict, so it needs no reader
    /// beyond <c>flow.jsonl</c> — do not "complete" it into one.
    /// </para>
    /// </summary>
    /// <param name="CommittedDigest">
    /// <see cref="Mutation.VerifyCommandResolver.DeclarationDigest"/> of the COMMITTED command line —
    /// null when <c>HEAD</c> holds no declaration (including a non-git workspace), which is exactly the
    /// "an uncommitted declaration was ignored" case.
    /// </param>
    /// <param name="WorkingTreeDigest">The same digest of the working-tree command line; null when the file is absent or comment-only.</param>
    public sealed record VerifyDeclarationIgnored(
        ExecutionId ExecutionId,
        string? CommittedDigest,
        string? WorkingTreeDigest) : FlowEvent;

    /// <summary>
    /// #1708 M1: the declaration that graded this execution came from <c>HEAD</c> rather than from the
    /// merge-base with <c>origin/main</c>, because no merge-base could be computed — no remote, a
    /// default branch that is not <c>main</c>, or unrelated histories. The per-execution boundary still
    /// holds (the value was read before the worker spawned), but the WIDER property does not: on this
    /// workspace, a commit made by an earlier lane on the current branch is inside what grades the next
    /// one, and nothing has reviewed it. This is what says so out loud instead of leaving it to be
    /// inferred from the absence of a ref.
    /// <para>
    /// <b>Diagnostic only</b>, exactly like <see cref="VerifyDeclarationIgnored"/> — no
    /// <see cref="StepState"/> field, no <c>WorkflowStatusView</c> surface, no <c>fleet_status</c>
    /// plumbing, no <see cref="Status.WorkflowOutcome"/> consequence. It changes no verdict and needs no
    /// reader beyond <c>flow.jsonl</c>; do not "complete" it into one.
    /// </para>
    /// <para>
    /// Appended only when a declaration was actually FOUND that way. A workspace with no reviewed
    /// baseline and no <c>.baton/verify</c> at all has nothing unreviewed to announce — it runs the role
    /// default, same as any other.
    /// </para>
    /// </summary>
    /// <param name="Digest">
    /// <see cref="Mutation.VerifyCommandResolver.DeclarationDigest"/> of the command line that was read,
    /// so the journal names WHICH unreviewed line took effect rather than only that one did.
    /// </param>
    public sealed record VerifyDeclarationUnreviewed(
        ExecutionId ExecutionId,
        string? Digest) : FlowEvent;

    /// <summary>
    /// #1623 (contract: <c>spec/baton.md</c> §3; the addendum's own words are quoted on
    /// <see cref="Mutation.TokenBudgetMonitor"/>): a live execution's measured usage crossed its role's
    /// token budget, OR (#1682) its tool-step count crossed its role's tool-step cap, OR (#1691) its
    /// billed tokens inside one trailing <c>TokenBudgetMonitor.BilledRateWindow</c> crossed an
    /// operator-supplied <c>--billed-rate-limit</c>. The engine cancels
    /// the execution (arrest, not park) rather than let it keep running.
    /// <paramref name="Usage"/> is the measured usage at arrest time; <paramref name="LastToolNames"/>
    /// the last few tool calls observed, which is what a conductor reads to tell a runaway loop from a
    /// merely long task. Settles the step <see cref="Status.WorkflowOutcome.Indeterminate"/>, same as
    /// <see cref="VerifyFailed"/> — never a blind retry. Deliberately not
    /// <see cref="FlowEvent.CancellationRequested"/>: that event is operator intent, and this is a
    /// distinct, engine-initiated fact.
    /// </summary>
    /// <param name="Reason">
    /// #1682: which producer armed this arrest — see <see cref="ArrestReason"/>. Null on a
    /// pre-#1682 ledger line; <c>StateProjector.DescribeArrest</c> is where that reads as.
    /// </param>
    /// <param name="ToolStepCount">
    /// #1682: the tool-step count at arrest time, set independently of <paramref name="Usage"/> (spec/baton.md §3).
    /// </param>
    /// <param name="PeakBilledInWindow">
    /// #1691: the largest Σ billed tokens this execution held inside one trailing
    /// <c>TokenBudgetMonitor.BilledRateWindow</c> — the OBSERVED rate, recorded whether or not
    /// <paramref name="BilledRateLimit"/> was set. Note the scope: this is an ARREST record, so a
    /// normally-completed execution's peak reaches no ledger line at all — #1709, and spec/baton.md §3
    /// states what that does and does not buy a future calibration. Null on any ledger line written
    /// before #1691.
    /// </param>
    /// <param name="BilledRateLimit">
    /// #1691: the limit <paramref name="PeakBilledInWindow"/> was compared against, or null when no
    /// rate trigger was armed (every role's default — spec/baton.md §3).
    /// </param>
    /// <param name="Adapter">
    /// #1745: the adapter this execution actually ran on, so <c>StateProjector.DescribeArrest</c> can
    /// name it in a <see cref="ArrestReason.TokenBudget"/> arrest's text — the budget that applied is
    /// now per-adapter, so the reason it fired is incomplete without naming which vendor's figure it
    /// was. Null on a ledger line written before this field existed.
    /// </param>
    public sealed record ExecutionArrested(
        ExecutionId ExecutionId,
        WorkerUsage? Usage = null,
        IReadOnlyList<string>? LastToolNames = null,
        ArrestReason? Reason = null,
        int? ToolStepCount = null,
        long? PeakBilledInWindow = null,
        long? BilledRateLimit = null,
        string? Adapter = null) : FlowEvent;

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

    /// <summary>
    /// #1608: Flow has classified a completed execution as <see cref="Outcomes.OutcomeVerdict.Indeterminate"/>
    /// — see that type's own remarks for what disagrees with what. Distinct from
    /// <see cref="ExecutionFailed"/> rather than reusing it with a sentinel classification: a reader
    /// of this journal sees the disagreement as its own fact, not a <c>Failed</c> collapsed onto a
    /// null <see cref="FailureClassification"/>. Carries no <see cref="FailureClassification"/> at
    /// all — see <see cref="Outcomes.OutcomeVerdict.Indeterminate"/>'s own remarks for why. Projects
    /// to <see cref="StepStatus.Failed"/> (the single-added-enum-value ruling keeps this out of
    /// <see cref="StepStatus"/> itself) plus <see cref="StepState.IndeterminateAwaitingResolution"/>,
    /// which is what actually drives the room-level <c>WorkflowOutcome.Indeterminate</c> reading and
    /// <see cref="Scheduling.RetryEngine.MayRetry"/>'s refusal.
    /// </summary>
    /// <param name="Reason">See <see cref="Outcomes.OutcomeClassification.Reason"/>'s remarks — the same "null means not recorded" rule.</param>
    /// <param name="CapturedResponseFile">See <see cref="ExecutionFailed.CapturedResponseFile"/>'s remarks — carried the same hop.</param>
    /// <param name="UnsatisfiedOutputNames">See <see cref="ExecutionFailed.UnsatisfiedOutputNames"/>'s remarks — carried the same hop.</param>
    public sealed record ExecutionIndeterminate(
        ExecutionId ExecutionId,
        string? Reason = null,
        string? CapturedResponseFile = null,
        IReadOnlyList<string>? UnsatisfiedOutputNames = null) : FlowEvent;

    /// <summary>
    /// #1608: the conductor resolution verb's own room fact — <c>baton resolve</c> is the only
    /// path ever allowed to write under a declared output name from a
    /// <see cref="Outcomes.OutputMaterializer.CapturedResponse"/>, and this event is what makes that
    /// resolution durable and falsifiable from the room record alone. Recorded exactly once per
    /// <see cref="ExecutionIndeterminate"/> — <see cref="Projection.StateProjector"/> clears
    /// <see cref="StepState.IndeterminateAwaitingResolution"/> on apply, so a second resolution
    /// attempt against the same execution is refused before this is ever appended
    /// (<c>Mutation.MutationInterface.RecordCaptureResolutionAsync</c>), not silently re-applied.
    /// </summary>
    /// <param name="StepId">
    /// The step this resolution applies to — carried explicitly (not solely derived via
    /// <paramref name="ExecutionId"/>) the same way <see cref="StepRetryForeclosed"/> carries both
    /// its <c>StepId</c> and its <c>ForExecutionId</c>, so a stale target is a guarded no-op on
    /// replay rather than a silent misapplication to whichever step now owns that execution id.
    /// </param>
    /// <param name="ExecutionId">The indeterminate execution this resolution settles.</param>
    /// <param name="Accepted">
    /// <c>true</c>: the capture honestly satisfies its declared output(s) — the step settles
    /// <see cref="StepStatus.Succeeded"/>, and this event is itself journaled BEFORE the real file(s)
    /// are written (#1608 review finding 5: fact then files, not files then fact — a crash in between
    /// leaves this fact durable with a declared output still missing, which
    /// <c>Mutation.MutationInterface</c>'s own resolution surface re-materializes from the still-durable
    /// capture on the next matching <c>--execution</c>, rather than the mirror gap the opposite order
    /// left open: an orphaned file on disk with no fact and a room still reading Indeterminate).
    /// <c>false</c>: rejected — the step stays
    /// <see cref="StepStatus.Failed"/>, no file is written, and <see cref="Scheduling.RetryEngine.MayRetry"/>
    /// re-applies its ordinary predicate rather than refusing unconditionally, since the conductor
    /// has now made the call this room was blocked on.
    /// </param>
    /// <param name="Reason">
    /// The conductor's own justification — required by <c>ResolveOptionsParser</c> for a rejection,
    /// optional for an acceptance (the accept/reject choice already speaks for itself there).
    /// </param>
    /// <param name="ResolvedOutputNames">
    /// The declared output name(s) this resolution covers — <see cref="ExecutionIndeterminate.UnsatisfiedOutputNames"/>
    /// at resolution time, carried onto this event too so the durable record of "what was written, or
    /// refused" never depends on re-deriving it from projected state.
    /// </param>
    /// <param name="Decider">Attribution info for the decider. Defaults to human, same as <see cref="ExternalDecisionRecorded"/>.</param>
    public sealed record CaptureResolved(
        StepId StepId,
        ExecutionId ExecutionId,
        bool Accepted,
        string? Reason = null,
        IReadOnlyList<string>? ResolvedOutputNames = null,
        DeciderInfo? Decider = null) : FlowEvent
    {
        [JsonIgnore]
        public DeciderInfo EffectiveDecider => Decider ?? DeciderInfo.DefaultHuman;
    }
}
