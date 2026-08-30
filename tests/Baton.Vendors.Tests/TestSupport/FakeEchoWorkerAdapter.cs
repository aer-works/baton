using Baton.Flow.Dispatch;
using Baton.Flow.Domain;

namespace Baton.Vendors.Tests.TestSupport;

/// <summary>
/// Stands in for a real vendor adapter (M11 Phase 1 excludes the Claude adapter, #85) — asserts the
/// canonical <see cref="WorkerInvocation"/>/<see cref="WorkerContract"/> → <see cref="CoreDispatchTarget"/>
/// mapping without a real vendor or live process, per the phase's stated deliverable. Deterministic:
/// echoes every field it received onto the command line so a test can assert on them directly,
/// instead of hiding them behind vendor-specific flag/shell mechanics.
/// </summary>
internal sealed class FakeEchoWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) => new(
        "echo",
        [
            invocation.PromptTemplate,
            invocation.Model ?? "(no-model)",
            invocation.PermissionScope ?? "(no-permission-scope)",
            Translate(invocation.PermissionGrant),
            contract.WorkerName,
            .. contract.RequiredInputs,
            .. contract.ProducedOutputs.Select(o => o.Name),
        ],
        invocation.WorkingDirectory);

    /// <summary>
    /// Stands in for a vendor adapter, so it belongs to the population WorkerBindingResolver applies
    /// its grant refusals to — the refusals are scoped to adapters that actually consume a grant
    /// (#651), and a fake outside that population would make every one of them silently not fire.
    /// Never refuses: a fake that rejected grants would fail tests for a reason the real adapters'
    /// own translator tests already cover.
    /// </summary>
    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);
        resolvedValue = string.Join(
            ',',
            grant.ReadFiles ? "read" : "no-read",
            grant.WriteFiles ? "write" : "no-write",
            grant.RunShellCommands ? "shell" : "no-shell",
            grant.NetworkAccess ? "network" : "no-network");
        gapReason = null;
        return true;
    }

    private string Translate(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return "(no-permission-grant)";
        }

        TryTranslatePermissionGrant(grant, out var resolved, out _);
        return resolved!;
    }
}
