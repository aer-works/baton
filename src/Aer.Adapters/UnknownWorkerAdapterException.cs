using Aer.Flow;

namespace Aer.Adapters;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when a <see cref="WorkerBindingConfigEntry.Adapter"/>
/// name has no corresponding entry in the supplied adapter registry — the config named an adapter
/// that was never registered (e.g. a typo, or a vendor not yet built). Mirrors
/// <c>Aer.Flow.Mutation.UnresolvedWorkerException</c>'s role one layer up, for worker roles with no
/// registered binding at all.
/// </summary>
public sealed class UnknownWorkerAdapterException : AerFlowException
{
    public string AdapterName { get; }

    public UnknownWorkerAdapterException(string adapterName, IEnumerable<string> availableAdapters)
        : base($"No IWorkerAdapter registered for adapter name '{adapterName}'.")
    {
        AdapterName = adapterName;

        // #1382 F6/F7: a bare "use a registered adapter name" named no field to edit, and an empty
        // registry produced "(e.g. )" -- the null-text-leak shape via an empty join. Sorted so the
        // message is deterministic across runs rather than following IEnumerable's unspecified order
        // (#1382 review, adjacent to F7).
        var sortedAdapters = availableAdapters.OrderBy(name => name, StringComparer.Ordinal).ToList();
        TryInvocation = sortedAdapters.Count == 0
            ? null
            : $"set \"Adapter\": \"{sortedAdapters[0]}\" in bindings.json (registered: {string.Join(", ", sortedAdapters)}).";
    }
}
