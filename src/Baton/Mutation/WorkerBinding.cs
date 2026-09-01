using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// Resolves a <see cref="WorkflowStepDefinition.Worker"/> role name (e.g. <c>"architect"</c>) to
/// what the <c>MutationInterface</c> needs to dispatch and classify it, and how. This resolution is
/// left to "configuration external to the workflow" — <c>Baton.Vendors</c> doesn't
/// exist yet (no milestone), so callers supply it directly.
/// </summary>
public abstract record WorkerBinding(WorkerContract Contract, GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced)
{
    /// <summary>
    /// A Core-managed process: the concrete binary to spawn and how long a single
    /// execution may run.
    /// </summary>
    /// <param name="Adapter">
    /// The resolved config entry's <c>WorkerBindingConfigEntry.Adapter</c> name (issue #1567) —
    /// carried this far so <c>MutationInterface</c> can record it onto the
    /// <see cref="Domain.ExecutionRequest"/> it constructs, without re-deriving it from
    /// <see cref="FailureClassifier"/> or re-reading <c>bindings.json</c>. Not itself a vendor
    /// quirk: this is the same plain string <c>Baton.Status</c> already reads out of
    /// <c>bindings.json</c> today, just captured at resolve time instead of at read time.
    /// </param>
    /// <param name="Model">The resolved config entry's <c>WorkerBindingConfigEntry.Model</c>, carried for the same reason.</param>
    public sealed record Process(
        WorkerContract Contract,
        CoreDispatchTarget Target,
        TimeSpan Timeout,
        Outcomes.IFailureClassifier? FailureClassifier = null,
        GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced,
        string? Adapter = null,
        string? Model = null)
        : WorkerBinding(Contract, GrantAuditMode);

    /// <summary>
    /// A non-process external party — a human, or any other worker tier whose
    /// "execution" is an external event rather than a Core-managed process. Dispatching a step (or
    /// minting a supplementary execution via <c>MutationInterface.RecordSupplementaryExecutionAsync</c>)
    /// bound to this appends <see cref="Domain.FlowEvent.ExecutionRequestAccepted"/> and
    /// pre-allocates the output directory exactly like any other worker, but spawns nothing — no
    /// <c>Target</c>, no <c>Timeout</c>. Completion is detected later, by contract satisfaction
    /// alone (<see cref="Outcomes.NonProcessCompletionDetector"/>), never by a Core exit.
    /// </summary>
    public sealed record NonProcess(WorkerContract Contract, GrantAuditMode GrantAuditMode = GrantAuditMode.Enforced)
        : WorkerBinding(Contract, GrantAuditMode);
}

