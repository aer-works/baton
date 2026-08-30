using System.Collections;
using Baton.Mutation;

namespace Baton.Vendors;

/// <summary>
/// Backs <see cref="WorkerBindingResolver.ResolveLazily"/>: resolves (and refuses) each config entry
/// only the first time its worker name is looked up, never merely because the entry is present in the
/// file (#662). Every consumer in <c>Baton</c> — <c>MutationInterface</c>, the outcome detectors —
/// only ever calls <see cref="TryGetValue"/> or the indexer for a specific, already-known worker name;
/// none enumerates the whole map, which is what makes deferring resolution to that lookup safe here.
/// </summary>
internal sealed class LazyWorkerBindings : IReadOnlyDictionary<string, WorkerBinding>
{
    private readonly IReadOnlyDictionary<string, WorkerBindingConfigEntry> _config;
    private readonly Dictionary<string, Lazy<WorkerBinding>> _resolved;

    public LazyWorkerBindings(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        Func<string, WorkerBindingConfigEntry, WorkerBinding> resolveEntry)
    {
        _config = config;
        _resolved = config.ToDictionary(
            kv => kv.Key,
            kv => new Lazy<WorkerBinding>(() => resolveEntry(kv.Key, kv.Value)));
    }

    public bool TryGetValue(string key, out WorkerBinding value)
    {
        if (_resolved.TryGetValue(key, out var lazy))
        {
            value = lazy.Value;
            return true;
        }

        value = null!;
        return false;
    }

    public WorkerBinding this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) => _config.ContainsKey(key);

    public int Count => _config.Count;

    public IEnumerable<string> Keys => _config.Keys;

    // Enumerating would force every entry's Lazy<T> — the one path that reintroduces #662's eager
    // refusal, silently. No shipped consumer enumerates (see class remarks), so a future one is a
    // regression by definition, and the #662 review called the silent version a live footgun: this
    // throws instead, naming the alternative, so the regression announces itself at first run.
    public IEnumerable<WorkerBinding> Values => throw EnumerationRefused();

    public IEnumerator<KeyValuePair<string, WorkerBinding>> GetEnumerator() => throw EnumerationRefused();

    IEnumerator IEnumerable.GetEnumerator() => throw EnumerationRefused();

    private static NotSupportedException EnumerationRefused() => new(
        "Enumerating lazily-resolved worker bindings would eagerly resolve — and refuse — every "
        + "entry, which is the defect ResolveLazily exists to avoid (#662). Look workers up by "
        + "name via TryGetValue, or use WorkerBindingResolver.Resolve when eager refusal is wanted.");
}
