using System.Text.Json;
using Baton.Flow.Status;

namespace Baton.Vendors;

/// <summary>
/// The per-machine profile mapping M23 Phase 3 introduces (#272): a local, never-portable,
/// never-checked-in file naming real per-machine directories a
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> can reference by a stable key instead of a
/// literal path. The same key resolves to a different real directory on every machine that has its
/// own copy of this file — the mechanism that keeps a shared (or copied-to-a-new-machine)
/// bindings.json portable even though the project directory it points at is emphatically not.
/// <para>
/// <b>Format:</b> a flat JSON object, profile name → absolute directory path — the simplest shape
/// that satisfies "a stable key per machine," no wrapper record needed. Not validated against the
/// filesystem at load time (a profile naming a directory that doesn't exist yet, or doesn't exist on
/// this particular machine, surfaces naturally as a dispatch-time <c>BatonException</c> with
/// <c>SpawnFailed</c> — the aer-core native cwd primitive's own documented failure mode — the same
/// way a raw rooted <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> path would).
/// </para>
/// <para>
/// <b>Missing-file vs. malformed-file:</b> a missing file is "no profiles configured on this
/// machine yet," a valid and common state (most workflows never reference a
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> profile at all) — resolves to an empty
/// map. A malformed file is different: if it exists at all, a
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> resolution may be depending on it, so
/// silently treating it as empty would surface as a confusing "unknown profile" error instead of the
/// actual, fixable root cause — <see cref="LoadAsync"/> throws <see cref="ProfileStoreException"/>
/// instead.
/// </para>
/// </summary>
public static class BatonProfileStore
{
    /// <summary>
    /// The production location the phase names: <c>profiles.json</c> under <see cref="BatonPaths.Root"/>
    /// (<c>%USERPROFILE%\.baton</c> on Windows, <c>$HOME/.baton</c> on Unix, or the <c>BATON_HOME</c>
    /// override). A re-resolving property, not a captured value, so it honours the root seam. Tests
    /// construct against a temp file directly instead of this.
    /// </summary>
    public static string DefaultPath => Path.Combine(BatonPaths.Root, "profiles.json");

    /// <summary>Loads the profile map from <paramref name="path"/>; a missing file resolves to an empty map.</summary>
    /// <exception cref="ProfileStoreException">The file exists but is not valid JSON, or is not a JSON object of string values.</exception>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        Dictionary<string, string>? profiles;
        try
        {
            await using var stream = File.OpenRead(path);
            profiles = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ProfileStoreException($"Malformed profile mapping at '{path}': {ex.Message}", ex);
        }

        return profiles ?? new Dictionary<string, string>();
    }

    /// <summary>Persists <paramref name="profiles"/> to <paramref name="path"/>, creating parent directories as needed.</summary>
    public static async Task SaveAsync(
        IReadOnlyDictionary<string, string> profiles, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profiles, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }
}
