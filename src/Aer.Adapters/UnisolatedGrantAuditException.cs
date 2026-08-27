using Aer.Flow;

namespace Aer.Adapters;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when an entry specifies
/// <see cref="Aer.Flow.Domain.GrantAuditMode.AuditedNotEnforced"/> without an ACTUALLY provisioned
/// worktree — <see cref="WorkerBindingConfigEntry.IsWorktree"/>, the stamp
/// <see cref="WorktreeWorkspaces.Provision"/> leaves after rewriting the working directory. A
/// merely DECLARED <see cref="WorkerBindingConfigEntry.Worktree"/> spec is not isolation: the
/// callers that skip provisioning (#1012) would dispatch the worker into a null working directory.
/// An audit against a shared working directory would see unrelated dirt or miss nothing.
/// </summary>
public sealed class UnisolatedGrantAuditException : AerFlowException
{
    public string WorkerName { get; }

    public UnisolatedGrantAuditException(string workerName)
        : base(
            $"Worker-binding config entry for '{workerName}' specifies GrantAuditMode.AuditedNotEnforced " +
            "without a provisioned worktree. Post-run grant audit requires workspace isolation, and only " +
            "an actually-provisioned worktree provides it — a declared Worktree spec that was never " +
            "provisioned does not; an audit against a shared working directory would see unrelated dirt or miss nothing.")
    {
        WorkerName = workerName;
        TryInvocation = "use 'aer dispatch <role>' to auto-provision an isolated workspace, or add a \"Worktree\" " +
            "spec to this worker's bindings.json entry and run it through 'aer run', which provisions it -- a " +
            "declared spec alone is not isolation for a caller that resolves the binding without provisioning.";
    }
}
