using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Ui.Core;

public sealed partial class RoomClient
{
    /// <summary>Current remote-access state, for the Enable Remote Access view (M21 Phase 3, issue #234).</summary>
    public sealed record RemoteAccessStatus(bool IsRemote, int Port, bool HasRunningRooms);

    /// <summary>A freshly generated pairing code, mirroring <c>/api/pairing/code</c>'s response shape.</summary>
    public sealed record PairingCode(string Code, int ExpiresInSeconds);

    /// <summary>Reads the daemon's current bind mode and port straight from <c>/api/version</c> — not cached, since the whole point is to detect drift after a toggle.</summary>
    public async Task<RemoteAccessStatus?> GetRemoteAccessStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (meta == null) return null;

            var port = new Uri(_activeDaemonUrl).Port;
            return new RemoteAccessStatus(meta.IsRemote, port, meta.HasRunningRooms);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PairingCode?> GetPairingCodeAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/pairing/code", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<PairingCode>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The concurrency caps <c>ConcurrencySlotGate</c> enforces, mirroring <c>/api/settings/concurrency</c>'s response shape (#1298).</summary>
    public sealed record ConcurrencySettings(int GlobalCap, int PerVendorCap);

    /// <summary>Reads the daemon's current concurrency caps.</summary>
    public async Task<ConcurrencySettings?> GetConcurrencySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/settings/concurrency", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<ConcurrencySettings>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets the daemon's concurrency caps, persisting them to <c>~/.aer/settings.json</c> so they survive a restart. Returns the error text on a rejected (e.g. below-1) cap, null on success.</summary>
    public async Task<string?> SetConcurrencySettingsAsync(int globalCap, int perVendorCap, CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return "Could not reach the Baton daemon.";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_activeDaemonUrl}/api/settings/concurrency", new { GlobalCap = globalCap, PerVendorCap = perVendorCap }, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return body.TryGetProperty("Error", out var error) ? error.GetString() : $"Request failed: {response.StatusCode}";
        }
        catch (Exception ex)
        {
            return $"Could not reach the Baton daemon: {ex.Message}";
        }
    }

    /// <summary>A paired device, mirroring <c>/api/pairing/clients</c>'s response shape (Phase 6, #243).</summary>
    public sealed record PairedClientInfo(string ClientId, string Name, DateTime PairedAt);

    /// <summary>Lists paired devices for the "Paired Devices" management list — desktop-owner-only, see <c>Program.cs</c>'s <c>IsLocalToken</c> gate.</summary>
    public async Task<IReadOnlyList<PairedClientInfo>?> GetPairedClientsAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/pairing/clients", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<IReadOnlyList<PairedClientInfo>>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Revokes a paired device's token — its next request 401s immediately (Phase 6, #243).</summary>
    public async Task<bool> RevokePairedClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return false;

        try
        {
            var response = await _httpClient.DeleteAsync($"{_activeDaemonUrl}/api/pairing/clients/{Uri.EscapeDataString(clientId)}", cancellationToken).ConfigureAwait(true);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The Go tsnet sidecar's own state, mirrored via <c>/api/remote/sidecar-status</c>
    /// (Phase 5, #242) — <c>Ready</c>/<c>TailscaleIp</c> once tsnet enrollment is complete,
    /// <c>AuthUrl</c> while the one-time interactive Tailscale login is still pending, or
    /// <c>Error</c> for anything else (sidecar binary missing, tsnet itself failed to start).</summary>
    public sealed record SidecarStatus(bool Ready, string? AuthUrl, string? TailscaleIp, string? Error);

    /// <summary>Not cached — proxies the sidecar's live <c>/status</c> straight through Aer.Daemon, so this never reports stale enrollment state.</summary>
    public async Task<SidecarStatus?> GetSidecarStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return null;

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/remote/sidecar-status", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync<SidecarStatus>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Signs the tsnet sidecar out of its current tailnet and re-enters the one-time interactive
    /// login flow — the in-app counterpart of deleting the node from the Tailscale admin console and
    /// restarting Aer.Ui, which was previously the only way to disconnect it. Fire-and-forget on the
    /// daemon side: the sidecar answers 202 immediately and does the actual logout/re-auth in the
    /// background, so the next few <see cref="GetSidecarStatusAsync"/> polls are what surfaces the
    /// fresh <c>AuthUrl</c> once it's ready.
    /// </summary>
    public async Task<bool> ForgetSidecarAsync(CancellationToken cancellationToken = default)
    {
        if (_activeDaemonUrl == null) return false;

        try
        {
            var response = await _httpClient.PostAsync($"{_activeDaemonUrl}/api/remote/sidecar-forget", null, cancellationToken).ConfigureAwait(true);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Flips the daemon between loopback-only and <c>--remote</c>. There's no live Kestrel rebind
    /// (<c>Aer.Daemon/Program.cs</c> bakes the bind address in at startup) — so this shuts the
    /// daemon down and respawns it with/without <c>--remote</c>, reusing the same
    /// shutdown-then-respawn move <see cref="EnsureDaemonConnectedAsync"/> already makes on version
    /// skew. Refuses while a room is in flight, since bouncing the daemon would orphan it.
    /// </summary>
    public async Task<MutationOutcome> SetRemoteEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SetRemoteEnabledAsyncCore(enabled, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogToggleDiagnostic($"SetRemoteEnabledAsync threw: {ex}");
            throw;
        }
    }

    private async Task<MutationOutcome> SetRemoteEnabledAsyncCore(bool enabled, CancellationToken cancellationToken)
    {
        LogToggleDiagnostic($"SetRemoteEnabledAsync(enabled={enabled}) start, _activeDaemonUrl={_activeDaemonUrl}");
        var status = await GetRemoteAccessStatusAsync(cancellationToken).ConfigureAwait(true);
        if (status == null)
        {
            LogToggleDiagnostic("GetRemoteAccessStatusAsync returned null -> Could not reach Aer.Daemon."); // vocabulary-ok: diagnostic log
            return new MutationOutcome("Could not reach the Baton daemon.");
        }
        LogToggleDiagnostic($"status: IsRemote={status.IsRemote}, HasRunningRooms={status.HasRunningRooms}, Port={status.Port}");
        if (status.HasRunningRooms) return new MutationOutcome("Can't change remote access while a room is running — finish or pause it first.");
        if (status.IsRemote == enabled) return new MutationOutcome(null);

        await ShutdownDaemonAsync().ConfigureAwait(true);
        LogToggleDiagnostic("ShutdownDaemonAsync completed");
        _isClientMode = false;

        // A fixed sleep here raced the old process's real shutdown time. A first fix attempt
        // replaced it with polling the old daemon's /api/version until the request failed — but
        // that misfired too: a request that times out because the daemon is briefly slow to
        // respond while mid-graceful-shutdown (still holding its listening socket and
        // single-instance mutex) looks identical, over HTTP, to a request that fails because
        // nothing is listening at all. Found live via ~/.aer/daemon-spawn.log: the respawn below
        // fired while the old process — in the OLD mode — was still actually running, hit
        // "Another instance of Aer.Daemon is already running.", and died immediately. The
        // single-instance guard itself (see Aer.Daemon/Program.cs's RunDaemonAsync) is an OS-named
        // mutex that only exists while some process holds it — polling for that directly is an
        // unambiguous signal, not a timing inference.
        var exited = await WaitForDaemonToExitAsync(cancellationToken).ConfigureAwait(true);
        LogToggleDiagnostic($"WaitForDaemonToExitAsync returned {exited}");
        if (!exited)
        {
            return new MutationOutcome("The Baton daemon is taking a while to shut down — try again in a moment.");
        }

        var started = await SpawnDaemonProcessAsync(enabled ? "--remote" : "", cancellationToken).ConfigureAwait(true);
        LogToggleDiagnostic($"SpawnDaemonProcessAsync returned {started}");
        return started ? new MutationOutcome(null) : new MutationOutcome("Could not restart the Baton daemon.");
    }

    private static void LogToggleDiagnostic(string line)
    {
        try
        {
            var aerDir = AerPaths.Root;
            Directory.CreateDirectory(aerDir);
            TryAppendLog(Path.Combine(aerDir, "toggle-diagnostic.log"), $"{DateTime.UtcNow:O} {line}");
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    /// <summary>
    /// Polls the daemon's single-instance mutex (same name Aer.Daemon/Program.cs's
    /// <c>RunDaemonAsync</c> creates) until no process holds it, or ~12s elapses. The daemon's own
    /// <c>HostOptions.ShutdownTimeout</c> is explicitly bounded to 3s (see
    /// <c>Aer.Daemon/Program.cs</c>'s <c>RunDaemonAsync</c>), so this only needs enough margin above
    /// that for the shutdown request's own round trip plus process teardown — not the old 30s
    /// default's worth. A mutex check has no false positives the way the HTTP-timing check it
    /// replaced did, so a longer budget here costs time, not correctness.
    /// </summary>
    private static async Task<bool> WaitForDaemonToExitAsync(CancellationToken cancellationToken)
    {
        var mutexName = $"Global\\AerDaemonMutex_{Environment.UserName}";

        for (int i = 0; i < 60; i++)
        {
            if (!Mutex.TryOpenExisting(mutexName, out var existing))
            {
                return true;
            }

            existing.Dispose();
            await Task.Delay(200, cancellationToken).ConfigureAwait(true);
        }

        return false;
    }
}
