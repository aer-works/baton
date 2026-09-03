using Baton;

namespace Baton.Vendors;

/// <summary>
/// #1745: raised by <see cref="TokenBudgetSpec.Resolve"/> — see that method's own remarks for exactly
/// when (a known-but-unconfigured adapter) versus when not (an adapter outside the known set, which
/// resolves to no budget instead).
/// </summary>
public sealed class TokenBudgetAdapterNotConfiguredException : BatonFlowException
{
    public string RoleId { get; }
    public string Adapter { get; }

    public TokenBudgetAdapterNotConfiguredException(string roleId, string adapter, IEnumerable<string> configuredAdapters)
        : base(BuildMessage(roleId, adapter, configuredAdapters))
    {
        RoleId = roleId;
        Adapter = adapter;
    }

    private static string BuildMessage(string roleId, string adapter, IEnumerable<string> configuredAdapters)
    {
        var sorted = configuredAdapters.OrderBy(name => name, StringComparer.Ordinal).ToList();
        return $"Worker role '{roleId}' declares a per-adapter token_budget with no entry for adapter " +
            $"'{adapter}'. Configured adapters: {(sorted.Count == 0 ? "(none)" : string.Join(", ", sorted))}.";
    }
}
