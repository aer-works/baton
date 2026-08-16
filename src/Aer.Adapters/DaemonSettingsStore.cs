using System.Text.Json;

namespace Aer.Adapters;

/// <summary>Daemon-side settings that apply machine-wide rather than to any one room. Starts with the
/// concurrency caps (#1298); see decision 0020's amendment for why this lives daemon-side rather than
/// per-desktop-install.</summary>
public sealed record DaemonSettings
{
    public int GlobalConcurrencyCap { get; init; } = DefaultGlobalConcurrencyCap;
    public int PerVendorConcurrencyCap { get; init; } = DefaultPerVendorConcurrencyCap;

    public const int DefaultGlobalConcurrencyCap = 3;
    public const int DefaultPerVendorConcurrencyCap = 2;
}

/// <summary>
/// Reads and writes <see cref="AerPaths.SettingsFile"/>. Unlike <see cref="AerProfileStore"/>, a
/// malformed file here is never fatal: a bad concurrency cap should not stop the daemon from starting
/// at all, so both an absent and a malformed file resolve to <see cref="DaemonSettings"/>'s defaults —
/// the latter after logging a warning so the operator can see it silently reset rather than wonder why
/// a cap they set stopped applying.
/// </summary>
public static class DaemonSettingsStore
{
    /// <summary>Loads settings from <paramref name="path"/>. Never throws: an absent file, an unreadable
    /// file, or one that fails to parse all resolve to <see cref="DaemonSettings"/>'s defaults, the last
    /// two after writing a warning to <see cref="Console.Error"/>.</summary>
    public static async Task<DaemonSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new DaemonSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<DaemonSettings>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return settings ?? new DaemonSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Malformed or unreadable settings at '{path}', using defaults: {ex.Message}");
            return new DaemonSettings();
        }
    }

    /// <summary>Persists <paramref name="settings"/> to <paramref name="path"/>, creating parent directories as needed.</summary>
    public static async Task SaveAsync(DaemonSettings settings, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }
}
