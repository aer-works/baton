using Baton.Vendors;
using Baton.Flow.Dispatch;
using Baton.Flow.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A grant-consuming adapter (<see cref="IPermissionGrantTranslator"/>) that never answers
/// <see cref="IWorkerAdapter.WithheldWritesReachTheOutbox"/> — the population #662's `agy` binding
/// stands in for: a bindings-file entry whose output-declaring contract and read-only grant make it
/// permanently unresolvable via <see cref="WorkerBindingResolver.Resolve"/>, whether or not that
/// worker is ever actually dispatched. Never invoked (<see cref="Resolve"/> is unreachable) in a test
/// that binds this entry but never dispatches it.
/// </summary>
internal sealed class UnsatisfiableContractWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    public bool TryTranslatePermissionGrant(
        PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        resolvedValue = "Read";
        gapReason = null;
        return true;
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
        throw new InvalidOperationException("This adapter's binding is never meant to be dispatched.");
}
