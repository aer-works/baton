namespace Baton.Domain;

/// <summary>
/// The immutable unit of execution, shared by identity across Flow's and Core's halves of the
/// Event Store. Immutable once emitted — never mutated, never reused; a retry is a
/// brand-new <see cref="ExecutionRequest"/> with a brand-new <see cref="ExecutionId"/>.
/// </summary>
/// <param name="StepId">
/// <c>null</c> for a step-less supplementary execution minted outside the DAG during a pause
/// — never associated with any <see cref="WorkflowStepDefinition"/>, and therefore
/// never perturbing any step's latest-attempt projection.
/// </param>
/// <param name="Timeout">
/// <c>null</c> for a <see cref="Mutation.WorkerBinding.NonProcess"/> dispatch — nothing runs as a
/// Core process, so nothing can time out.
/// </param>
/// <param name="UpstreamExecutionIds">
/// For each <see cref="StepId"/> this step depends on, exactly which of that dependency's
/// <see cref="ExecutionId"/>s this request's <paramref name="Inputs"/> were derived from. This is
/// what makes staleness derivable purely by reading the log.
/// </param>
/// <param name="LinkedFromExecutionId">
/// The prior execution this one continues (issue #1359's <c>baton resume</c>): the same step's own
/// <c>LatestExecutionId</c> at the moment the resume was recorded, resumed via the adapter's
/// vendor-session plumbing rather than dispatched fresh. <c>null</c> for every ordinary dispatch and
/// retry — a resume is the only request shape that ever sets this. Defaulted, not required, for the
/// same JSON-replay reason <see cref="FlowEvent.ExecutionFailed.Reason"/> documents: an older
/// <c>flow.jsonl</c> line written before this field existed must still replay.
/// </param>
/// <param name="SessionId">
/// The vendor session id this resume's bindings file recorded for <see cref="Worker"/> at dispatch
/// time (issue #1359 F6) — an opaque string Flow never interprets, carried purely so a LATER resume
/// of this same execution can check the operator's bindings file still names the session this one
/// actually continued, rather than trusting an unrecorded assertion. <c>null</c> for every ordinary
/// dispatch and retry, same as <paramref name="LinkedFromExecutionId"/>.
/// </param>
public sealed record ExecutionRequest(
    ExecutionId ExecutionId,
    WorkflowId WorkflowId,
    StepId? StepId,
    string Worker,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    TimeSpan? Timeout,
    IReadOnlyList<EnvironmentVariable> Environment,
    IReadOnlyDictionary<StepId, ExecutionId> UpstreamExecutionIds,
    GrantAuditMode? GrantAuditMode = null,
    ExecutionId? LinkedFromExecutionId = null,
    string? SessionId = null);
