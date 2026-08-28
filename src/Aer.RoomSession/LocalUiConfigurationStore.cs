using System.Text.Json;

namespace Aer.RoomSession;

/// <summary>
/// Local configuration: a remembered list of recently opened room
/// directories, plus (M15 Phase 1, issue #137) the last worker-bindings file and workflow template
/// file a Run action used — Run still asks for a bindings file and records the answer in the room
/// directory (0056), so the value remembered here pre-fills that ask.
/// Deliberately never authoritative — a room directory's own contents are (§3.1's
/// self-describing-directory contract) — so this store treats a missing or corrupt config file as
/// "nothing remembered yet" rather than a startup failure, and drops any remembered room directory
/// path that no longer exists on disk when it loads the list back, rather than surfacing it as an
/// error. This is this phase's concrete answer to §3.1's "how a UI populates its list"
/// implementation choice: ask the user for a path (or pick a remembered one), never scan a
/// configured root.
/// </summary>
public sealed class LocalUiConfigurationStore(string configFilePath)
{
    private const int MaxRecentRoomDirectories = 10;
    private const int MaxRecentCommandsPerVendor = 5;

    /// <summary>
    /// The production location: a per-user config directory, never a path a test could collide
    /// with — tests construct this store directly against a temp file instead of calling this.
    /// </summary>
    // The "Aer.Ui" folder name is DELIBERATE, not an oversight from the Ui archive (#1412): the
    // operator's existing recent-directories data lives there, and renaming the folder belongs to
    // the Baton rename window (one migration event), not a deletion PR.
    public static LocalUiConfigurationStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Aer.Ui",
        "recent-room-directories.json"));

    public async Task<IReadOnlyList<string>> LoadRecentRoomDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);

        // A listed directory that no longer exists is stale list state, reflected here by
        // omission rather than surfaced as a system error (UI spec §3.1).
        return configuration.RecentRoomDirectories.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Records <paramref name="roomDirectoryPath"/> as the most recently opened directory,
    /// deduplicated against any existing entry for the same path and capped at
    /// <see cref="MaxRecentRoomDirectories"/> — the list is a bounded convenience, not a full
    /// history.
    /// </summary>
    public async Task RecordOpenedAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(roomDirectoryPath);

        var updated = new List<string> { fullPath };
        updated.AddRange(configuration.RecentRoomDirectories.Where(
            path => !string.Equals(Path.GetFullPath(path), fullPath, StringComparison.Ordinal)));
        if (updated.Count > MaxRecentRoomDirectories)
        {
            updated.RemoveRange(MaxRecentRoomDirectories, updated.Count - MaxRecentRoomDirectories);
        }

        await SaveConfigurationAsync(configuration with { RecentRoomDirectories = updated }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Strips <paramref name="roomDirectoryPath"/> from the recents list (M24 Phase 5, #278) — used
    /// by a real delete so a stale recent doesn't 404 on the next open. A no-op if the path isn't
    /// present, matching this store's own "rebuildable convenience, not authoritative" stance.
    /// </summary>
    public async Task RemoveRecentRoomDirectoryAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(roomDirectoryPath);

        var updated = configuration.RecentRoomDirectories
            .Where(path => !string.Equals(Path.GetFullPath(path), fullPath, StringComparison.Ordinal))
            .ToList();

        await SaveConfigurationAsync(configuration with { RecentRoomDirectories = updated }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The bindings file (M15 Phase 1, issue #137): a room carries its own copy (0056), so a Run
    /// action asks the user for it and records the answer — this is only the remembered default that
    /// pre-fills the ask, exactly the same non-authoritative convenience the recents list already is
    /// (§3.1, §4).
    /// </summary>
    public async Task<string?> LoadLastBindingsFilePathAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).LastBindingsFilePath;

    public async Task RecordBindingsFilePathAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { LastBindingsFilePath = Path.GetFullPath(bindingsFilePath) }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The remembered workflow template path — only ever asked for on a fresh start (§137's resolved open question).</summary>
    public async Task<string?> LoadLastWorkflowTemplateFilePathAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).LastWorkflowTemplateFilePath;

    public async Task RecordWorkflowTemplateFilePathAsync(string workflowTemplateFilePath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { LastWorkflowTemplateFilePath = Path.GetFullPath(workflowTemplateFilePath) }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Recently-used skills/commands/agents per vendor (M24 Phase 2 follow-up, chat capability
    /// picker): the daemon is already this store's home (shared by desktop and mobile, since both
    /// talk to the same daemon process), so recency lives here rather than duplicated per-client
    /// local storage. Capped per vendor at <see cref="MaxRecentCommandsPerVendor"/> — a bounded
    /// convenience, not a full history, same idiom as <see cref="RecordOpenedAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadRecentCommandsAsync(string vendor, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var recentCommands = configuration.RecentCommands;
        return recentCommands != null && recentCommands.TryGetValue(vendor, out var commands) ? commands : [];
    }

    public async Task RecordCommandUsedAsync(string vendor, string commandName, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var recentCommands = configuration.RecentCommands ?? new Dictionary<string, List<string>>();
        var existing = recentCommands.TryGetValue(vendor, out var commands) ? commands : [];

        var updated = new List<string> { commandName };
        updated.AddRange(existing.Where(name => !string.Equals(name, commandName, StringComparison.Ordinal)));
        if (updated.Count > MaxRecentCommandsPerVendor)
        {
            updated.RemoveRange(MaxRecentCommandsPerVendor, updated.Count - MaxRecentCommandsPerVendor);
        }

        var updatedRecentCommands = new Dictionary<string, List<string>>(recentCommands)
        {
            [vendor] = updated
        };

        await SaveConfigurationAsync(configuration with { RecentCommands = updatedRecentCommands }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A reusable Tailscale auth key (M21 Phase 7 follow-up, #246): once the tsnet sidecar is ready,
    /// the pairing QR embeds this so a phone's own embedded tsnet node can join the tailnet
    /// non-interactively — the `tailscale` Dart package requires a real auth key for a device's
    /// first-ever enrollment (confirmed against its vendored source; it does not support the
    /// keyless-then-`needsLogin` flow for a device with zero prior state). One key, generated once in
    /// the Tailscale admin console and pasted here, covers every phone that ever scans the QR — never
    /// sent anywhere over the network, only rendered into the on-screen QR image.
    /// </summary>
    public async Task<string?> LoadTailscaleAuthKeyAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).TailscaleAuthKey;

    public async Task RecordTailscaleAuthKeyAsync(string? tailscaleAuthKey, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { TailscaleAuthKey = string.IsNullOrWhiteSpace(tailscaleAuthKey) ? null : tailscaleAuthKey.Trim() },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The remembered appearance theme (Settings → Appearance, #1068): one of <c>Light</c>,
    /// <c>Dark</c>, or <c>System</c>. Returns <c>null</c> when nothing has been chosen yet, which the
    /// app reads as "follow the OS" — the same default the product shipped with before an in-app
    /// control existed. Like every value in this store it is a rebuildable convenience, never
    /// authoritative: a missing or unrecognised value simply means the default.
    /// </summary>
    public async Task<string?> LoadThemeAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).Theme;

    public async Task RecordThemeAsync(string? theme, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { Theme = string.IsNullOrWhiteSpace(theme) ? null : theme.Trim() },
            cancellationToken).ConfigureAwait(false);
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<StoredConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return new StoredConfiguration([], null, null, null, new Dictionary<string, List<string>>());
        }

        for (var i = 0; i < 5; i++)
        {
            try
            {
                await using var stream = new FileStream(configFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var configuration = await JsonSerializer.DeserializeAsync<StoredConfiguration>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return configuration ?? new StoredConfiguration([], null, null, null, new Dictionary<string, List<string>>());
            }
            catch (IOException) when (i < 4)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Local UI Configuration is a rebuildable convenience, never authoritative (§3.1) — a
                // corrupt file is treated as empty, not a startup failure.
                return new StoredConfiguration([], null, null, null, new Dictionary<string, List<string>>());
            }
        }
        return new StoredConfiguration([], null, null, null, new Dictionary<string, List<string>>());
    }

    private async Task SaveConfigurationAsync(StoredConfiguration configuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(configFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            for (var i = 0; i < 5; i++)
            {
                try
                {
                    await using var stream = new FileStream(configFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    await JsonSerializer.SerializeAsync(stream, configuration, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The on-disk shape of this file. A plain JSON array was the whole file before M15 Phase 1
    /// (issue #137) added the two remembered file paths below it; that old shape deserializes as
    /// neither a JSON object nor a valid <see cref="StoredConfiguration"/>, so an upgrade from it
    /// falls through the same corrupt-file recovery <see cref="LoadConfigurationAsync"/> already has
    /// — Local UI Configuration is a rebuildable convenience, never authoritative (§3.1), so losing a
    /// stale recents list across this shape change is an acceptable, silent reset rather than a
    /// migration worth writing.
    /// </summary>
    private sealed record StoredConfiguration(
        List<string> RecentRoomDirectories,
        string? LastBindingsFilePath,
        string? LastWorkflowTemplateFilePath,
        string? TailscaleAuthKey,
        Dictionary<string, List<string>>? RecentCommands,
        // Optional with a default so the existing constructor call sites (and older config files that
        // predate this field) need no change — a missing Theme deserialises to null, i.e. "follow the OS".
        string? Theme = null);
}
