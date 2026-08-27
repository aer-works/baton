using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Cli.Tests.TestSupport;

/// <summary>
/// <see cref="ContractOutputWorkerAdapter"/> plus <see cref="IPermissionGrantTranslator"/> (#1355 F2/F3).
/// <see cref="DispatchCommand"/>'s printed grant line and <c>WorkerBindingResolver</c>'s grant refusals
/// both key off <c>adapter is IPermissionGrantTranslator</c> -- a population the plain
/// <see cref="ContractOutputWorkerAdapter"/> sits outside of on purpose, so most dispatch tests never
/// pay for refusal checks a real vendor adapter would apply. A test asserting the printed grant line
/// needs a fake INSIDE that population instead: one that never refuses (mirroring
/// <c>FakeEchoWorkerAdapter</c> in Aer.Adapters.Tests) and reports withheld writes as outbox-reaching,
/// mirroring <c>ClaudeWorkerAdapter</c> -- the real default adapter for every read-shaped role these
/// tests dispatch, so a role's plain `write_files: false` grant does not trip
/// <c>WorkerBindingResolver</c>'s "contract cannot be written" refusal here either.
/// </summary>
internal sealed class GrantConsumingContractOutputWorkerAdapter(
    bool satisfyOutputs,
    IReadOnlyDictionary<string, string>? outputFixtures = null) : IWorkerAdapter, IPermissionGrantTranslator
{
    private readonly ContractOutputWorkerAdapter _inner = new(satisfyOutputs, outputFixtures);

    public bool WithheldWritesReachTheOutbox => true;

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
        _inner.Resolve(invocation, contract);

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);
        resolvedValue = "(fake-translated)";
        gapReason = null;
        return true;
    }
}
