using System.Collections;
using Baton.Flow.Mutation;

namespace Baton.Cli;

/// <summary>
/// Overlays a single <c>NonProcess</c> binding onto an otherwise-resolved <see cref="WorkerBinding"/>
/// map without enumerating it (#662): copying a <c>WorkerBindingResolver.ResolveLazily</c> result into
/// a plain <see cref="Dictionary{TKey,TValue}"/> to add one key would iterate the whole map, forcing
/// every deferred entry to resolve — and refuse — eagerly.
/// </summary>
internal sealed class WorkerBindingOverride(
    IReadOnlyDictionary<string, WorkerBinding> baseBindings, string overrideKey, WorkerBinding overrideValue)
    : IReadOnlyDictionary<string, WorkerBinding>
{
    public bool TryGetValue(string key, out WorkerBinding value)
    {
        if (key == overrideKey)
        {
            value = overrideValue;
            return true;
        }

        return baseBindings.TryGetValue(key, out value!);
    }

    public WorkerBinding this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) => key == overrideKey || baseBindings.ContainsKey(key);

    public int Count => baseBindings.ContainsKey(overrideKey) ? baseBindings.Count : baseBindings.Count + 1;

    public IEnumerable<string> Keys =>
        baseBindings.ContainsKey(overrideKey) ? baseBindings.Keys : [.. baseBindings.Keys, overrideKey];

    public IEnumerable<WorkerBinding> Values
    {
        get
        {
            foreach (var key in Keys)
            {
                yield return this[key];
            }
        }
    }

    public IEnumerator<KeyValuePair<string, WorkerBinding>> GetEnumerator()
    {
        foreach (var key in Keys)
        {
            yield return new KeyValuePair<string, WorkerBinding>(key, this[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
