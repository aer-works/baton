namespace Baton.Vendors;

/// <summary>
/// #1745: a role's <c>token_budget</c> catalog entry — either one figure that applies regardless of
/// which adapter runs the role (today's shape, unchanged), or a map keyed by adapter name so a role
/// can carry a different default per vendor (claude and agy bill differently: different context
/// accounting, different cache/thinking treatment, different price per token, so the same number of
/// billed tokens means a different amount of money and a different runway on each). Parsed by
/// <see cref="WorkerRoleCatalog"/> at load time (which shape a role's JSON entry took, and whether a
/// map's keys/values are well-formed); resolved against the actual dispatched adapter at
/// <see cref="RoleDispatch.ToBinding"/> time, never earlier — the catalog only ever states intent, and
/// a role never names a vendor ([0017]).
/// </summary>
public abstract record TokenBudgetSpec
{
    private TokenBudgetSpec()
    {
    }

    /// <summary>One figure, applied no matter which adapter runs the role — today's shape.</summary>
    public sealed record Fixed(long Value) : TokenBudgetSpec;

    /// <summary>A figure per adapter name (e.g. <c>claude</c>, <c>agy</c>), keyed exactly as <see cref="WorkerRoleCatalog.KnownTokenBudgetAdapters"/> names it.</summary>
    public sealed record PerAdapter(IReadOnlyDictionary<string, long> ByAdapter) : TokenBudgetSpec;

    /// <summary>
    /// The effective per-execution budget for <paramref name="adapter"/> — the resolved figure
    /// <see cref="RoleDispatch.ToBinding"/> carries onto the binding. A <see cref="PerAdapter"/> map
    /// missing an entry for one of <see cref="WorkerRoleCatalog.KnownTokenBudgetAdapters"/> is a typed
    /// refusal at dispatch (fail closed), never a silent fallback to another adapter's figure or to no
    /// budget at all — an operator who authors a claude-only map, then dispatches with
    /// <c>--adapter agy</c>, has an unconfigured combination, not an implicitly-unwatched one. An
    /// adapter that is neither <c>claude</c> nor <c>agy</c> (a test double, the engine-run
    /// capture/no-op adapters, a future vendor this feature has not been extended to) can never appear
    /// in ANY map — <see cref="WorkerRoleCatalog"/> only accepts those two keys at load — so it
    /// resolves to null (no budget enforced) instead: the same "unwatched, not refused" treatment a
    /// single-figure role already gets when its resolved adapter has no registered usage parser
    /// (spec/baton.md §3).
    /// </summary>
    public long? Resolve(string roleId, string adapter) => this switch
    {
        Fixed fixedBudget => fixedBudget.Value,
        PerAdapter perAdapter when perAdapter.ByAdapter.TryGetValue(adapter, out var value) => value,
        PerAdapter when !WorkerRoleCatalog.KnownTokenBudgetAdapters.Contains(adapter) => null,
        PerAdapter perAdapter => throw new TokenBudgetAdapterNotConfiguredException(roleId, adapter, perAdapter.ByAdapter.Keys),
        _ => throw new NotSupportedException($"Unknown {nameof(TokenBudgetSpec)} subtype '{GetType()}'."),
    };
}
