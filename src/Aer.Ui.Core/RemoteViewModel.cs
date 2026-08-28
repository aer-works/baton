using System.Collections.ObjectModel;
using Aer.RoomSession;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace Aer.Ui.Core;

/// <summary>
/// The Enable Remote Access view's state (M21 Phase 3, issue #234): shows a pairing code and QR,
/// and toggles the daemon between loopback-only and <c>--remote</c>. There's no live Kestrel
/// rebind (<see cref="RoomClient.SetRemoteEnabledAsync"/>'s remarks), so every toggle is a real
/// shutdown-and-respawn — <see cref="IsBusy"/> covers that multi-second gap.
/// <para>
/// <b>QR payload decision of record:</b> a plain <c>aer://pair?host=&lt;host&gt;&amp;code=&lt;code&gt;</c>
/// URI, not JSON — simpler to encode and parse on both ends (<c>Aer.Mobile</c>'s scanner just reads
/// query parameters off a <see cref="Uri"/>), and reads unambiguously as "this QR is for pairing"
/// if a phone's general-purpose camera app ever scans it outside the app.
/// </para>
/// </summary>
/// <summary>The Go tsnet sidecar's state as exactly one of four mutually exclusive phases (Phase 5, #242) — see <see cref="RemoteViewModel.CurrentSidecarPhase"/>.</summary>
public enum SidecarPhase
{
    Starting,
    NeedsSignIn,
    Ready,
    Unavailable,
}

public sealed partial class RemoteViewModel : ObservableObject
{
    public RemoteViewModel()
    {
        PairedClients.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPairedClients));
    }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    [NotifyPropertyChangedFor(nameof(ShouldPollSidecarStatus))]
    [NotifyPropertyChangedFor(nameof(ShowLanEncryptionWarning))]
    [NotifyPropertyChangedFor(nameof(ShowPairingBlock))]
    [NotifyPropertyChangedFor(nameof(ShowNoPairingHostMessage))]
    private bool isRemoteEnabled;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHost))]
    [NotifyPropertyChangedFor(nameof(EffectivePairingHost))]
    [NotifyPropertyChangedFor(nameof(HasEffectivePairingHost))]
    [NotifyPropertyChangedFor(nameof(IsPairingOverTailnet))]
    [NotifyPropertyChangedFor(nameof(NeedsTailscaleAuthKey))]
    [NotifyPropertyChangedFor(nameof(ShowPairingBlock))]
    [NotifyPropertyChangedFor(nameof(ShowNoPairingHostMessage))]
    private string? host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidecarTailnetHost))]
    [NotifyPropertyChangedFor(nameof(EffectivePairingHost))]
    [NotifyPropertyChangedFor(nameof(HasEffectivePairingHost))]
    [NotifyPropertyChangedFor(nameof(IsPairingOverTailnet))]
    [NotifyPropertyChangedFor(nameof(NeedsTailscaleAuthKey))]
    [NotifyPropertyChangedFor(nameof(ShowPairingBlock))]
    [NotifyPropertyChangedFor(nameof(ShowNoPairingHostMessage))]
    private int? port;

    [ObservableProperty]
    private string? pairingCode;

    [ObservableProperty]
    private int pairingCodeExpiresInSeconds;

    [ObservableProperty]
    private byte[]? qrPngBytes;

    /// <summary>
    /// A reusable Tailscale auth key (M21 Phase 7 follow-up, #246), persisted via
    /// <see cref="LocalUiConfigurationStore"/>. Embedded in the pairing QR whenever pairing is over
    /// the tailnet, so a phone's embedded tsnet node can enroll non-interactively — see
    /// <see cref="GeneratePairingCodeAsync"/> and that store's own remarks on why a network round
    /// trip to fetch this from the daemon isn't needed at all: the QR is built client-side here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsTailscaleAuthKey))]
    private string? tailscaleAuthKey;

    [ObservableProperty]
    private string statusText = "Checking...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorText;

    /// <summary>Paired devices management list (Phase 6, #243) — populated on every <see cref="RefreshAsync"/>, independent of the current toggle state, since revocation is meaningful whether or not remote access happens to be on right now.</summary>
    public ObservableCollection<PairedClientItemViewModel> PairedClients { get; } = new();

    // M21 Phase 5 (#242): the Go tsnet sidecar's state, polled while remote access is on and not
    // yet Ready (MainWindow's existing 1s pairing-countdown timer drives RefreshSidecarStatusAsync
    // too — see ShouldPollSidecarStatus). Not proven live end to end until this session's own
    // manual test (owner completed real tsnet enrollment, confirmed connected in the Tailscale
    // admin console) — see docs/milestone-history.md, M21. The four fields below are read only through
    // CurrentSidecarPhase/the Is*-phase booleans, never directly in the view: found live that
    // binding each field's own Has* flag independently let more than one of the view's sections
    // render at once during in-between polls (e.g. a just-cleared AuthUrl racing a not-yet-set
    // TailscaleIp), instead of exactly one state ever being true at a time.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldPollSidecarStatus))]
    [NotifyPropertyChangedFor(nameof(CurrentSidecarPhase))]
    [NotifyPropertyChangedFor(nameof(IsSidecarStarting))]
    [NotifyPropertyChangedFor(nameof(IsSidecarNeedsSignIn))]
    [NotifyPropertyChangedFor(nameof(IsSidecarReadyPhase))]
    [NotifyPropertyChangedFor(nameof(IsSidecarUnavailable))]
    private bool sidecarReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSidecarPhase))]
    [NotifyPropertyChangedFor(nameof(IsSidecarStarting))]
    [NotifyPropertyChangedFor(nameof(IsSidecarNeedsSignIn))]
    [NotifyPropertyChangedFor(nameof(IsSidecarReadyPhase))]
    [NotifyPropertyChangedFor(nameof(IsSidecarUnavailable))]
    private string? sidecarAuthUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidecarTailnetHost))]
    [NotifyPropertyChangedFor(nameof(EffectivePairingHost))]
    [NotifyPropertyChangedFor(nameof(HasEffectivePairingHost))]
    [NotifyPropertyChangedFor(nameof(IsPairingOverTailnet))]
    [NotifyPropertyChangedFor(nameof(NeedsTailscaleAuthKey))]
    [NotifyPropertyChangedFor(nameof(ShowPairingBlock))]
    [NotifyPropertyChangedFor(nameof(ShowNoPairingHostMessage))]
    private string? sidecarTailscaleIp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSidecarPhase))]
    [NotifyPropertyChangedFor(nameof(IsSidecarStarting))]
    [NotifyPropertyChangedFor(nameof(IsSidecarNeedsSignIn))]
    [NotifyPropertyChangedFor(nameof(IsSidecarReadyPhase))]
    [NotifyPropertyChangedFor(nameof(IsSidecarUnavailable))]
    private string? sidecarError;

    public bool HasHost => Host != null;
    public bool HasError => ErrorText != null;
    public bool HasPairedClients => PairedClients.Count > 0;
    public string? SidecarTailnetHost => SidecarTailscaleIp != null && Port != null ? $"{SidecarTailscaleIp}:{Port}" : null;
    public bool ShouldPollSidecarStatus => IsRemoteEnabled && !SidecarReady;
    public string ToggleButtonText => IsRemoteEnabled ? "Turn off remote access" : "Turn on remote access";

    /// <summary>
    /// The single host the "Pair a phone" section's QR/code target — the LAN address whenever this
    /// machine has one, falling back to the tailnet address only when it doesn't (issue #384 / eval
    /// B-03). Previously preferred the tailnet address whenever the sidecar was Ready, but a fresh
    /// phone has no route into the tailnet without an auth key, and probed live even a phone that
    /// *did* have one timed out against the tailnet address while the LAN address answered — the
    /// address that actually pairs was never offered. Still only one pairing target displayed at a
    /// time (found live that two separate QR codes was genuinely confusing).
    /// </summary>
    public string? EffectivePairingHost => Host ?? SidecarTailnetHost;
    public bool HasEffectivePairingHost => EffectivePairingHost != null;

    /// <summary>
    /// Whether the *displayed* pairing target is the tailnet address — not merely whether the
    /// sidecar happens to be ready, since <see cref="EffectivePairingHost"/> now prefers LAN whenever
    /// it's available. Drives the "Via Tailscale"/"Via local network" caption, the auth-key section,
    /// <see cref="NeedsTailscaleAuthKey"/>, and whether the QR payload embeds <c>tskey</c> — all of
    /// which must agree with the host actually shown, not with the sidecar's raw ready-state.
    /// </summary>
    public bool IsPairingOverTailnet => SidecarTailnetHost != null && EffectivePairingHost == SidecarTailnetHost;

    /// <summary>
    /// The plaintext-LAN warning applies whenever remote access is on: <c>Aer.Daemon</c> binds
    /// <c>IPAddress.Any</c> under <c>--remote</c> regardless of whether pairing happens to be routed
    /// over the tailnet, so the plain-LAN listener is reachable by anyone on the network the whole
    /// time remote is enabled. Previously gated on <c>!IsPairingOverTailnet</c>, which hid the
    /// warning exactly when a live QR was being offered — precisely when it mattered (issue #384).
    /// </summary>
    public bool ShowLanEncryptionWarning => IsRemoteEnabled;

    /// <summary>
    /// Gates the "Pair a phone" section on remote actually being enabled (issue #384), not merely on
    /// a host being available: <see cref="Host"/> is populated by every <see cref="RefreshAsync"/>
    /// regardless of <see cref="IsRemoteEnabled"/>, so the old <c>HasEffectivePairingHost</c> gate
    /// offered to pair a phone on a screen that, in the same breath, said remote access was off and
    /// nothing but this computer could connect.
    /// </summary>
    public bool ShowPairingBlock => IsRemoteEnabled && HasEffectivePairingHost;

    /// <summary>The pairing block's empty-state companion — remote is on but no LAN or tailnet address is available yet (e.g. no network connectivity). Kept mutually exclusive with <see cref="ShowPairingBlock"/> so gating the block on <see cref="IsRemoteEnabled"/> doesn't leave both an empty QR and this message showing at once.</summary>
    public bool ShowNoPairingHostMessage => IsRemoteEnabled && !HasEffectivePairingHost;

    /// <summary>Tailnet pairing needs a key to embed in the QR (see <see cref="TailscaleAuthKey"/>) — without one, the QR would reproduce the exact "no auth key and no existing session state" failure a fresh phone hits.</summary>
    public bool NeedsTailscaleAuthKey => IsPairingOverTailnet && string.IsNullOrWhiteSpace(TailscaleAuthKey);

    /// <summary>
    /// The sidecar's state as exactly one of four mutually exclusive phases — the single source of
    /// truth the view binds against, instead of three independently-bindable Has* flags that could
    /// render simultaneously. Order matters: Ready wins over a stale AuthUrl (the sidecar clears its
    /// own AuthUrl on becoming ready, but a client-side field only updates on the next poll), and an
    /// explicit error only counts once there's neither a pending sign-in nor a ready state to show.
    /// </summary>
    public SidecarPhase CurrentSidecarPhase =>
        SidecarReady ? SidecarPhase.Ready
        : SidecarAuthUrl != null ? SidecarPhase.NeedsSignIn
        : SidecarError != null ? SidecarPhase.Unavailable
        : SidecarPhase.Starting;

    public bool IsSidecarStarting => CurrentSidecarPhase == SidecarPhase.Starting;
    public bool IsSidecarNeedsSignIn => CurrentSidecarPhase == SidecarPhase.NeedsSignIn;
    public bool IsSidecarReadyPhase => CurrentSidecarPhase == SidecarPhase.Ready;
    public bool IsSidecarUnavailable => CurrentSidecarPhase == SidecarPhase.Unavailable;

    /// <summary>
    /// Refreshes the daemon's current bind mode/port, this machine's LAN address, and a fresh
    /// pairing code — everything the view needs to render, in one call (activation, and after a
    /// successful toggle).
    /// </summary>
    public async Task RefreshAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var status = await session.GetRemoteAccessStatusAsync(cancellationToken).ConfigureAwait(true);
            if (status == null)
            {
                StatusText = "Could not reach the Baton daemon.";
                return;
            }

            IsRemoteEnabled = status.IsRemote;
            Port = status.Port;
            TailscaleAuthKey = await session.LoadTailscaleAuthKeyAsync(cancellationToken).ConfigureAwait(true);

            var lanAddress = LanAddress.TryGetPrimary();
            Host = lanAddress != null ? $"{lanAddress}:{status.Port}" : null;

            StatusText = status switch
            {
                { IsRemote: true, HasRunningRooms: true } => "Remote access is on. A room is running — pairing still works, but the toggle is locked until it finishes.",
                { IsRemote: true } => "Remote access is on — anyone on this network can reach it until you turn it off.",
                { HasRunningRooms: true } => "Remote access is off. A room is running — the toggle is locked until it finishes.",
                _ => "Remote access is off — only this computer can reach the Baton daemon.",
            };

            if (IsRemoteEnabled)
            {
                await RefreshSidecarStatusAsync(session, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                SidecarReady = false;
                SidecarAuthUrl = null;
                SidecarTailscaleIp = null;
                SidecarError = null;
            }

            // Mint a pairing code exactly when the block that shows it is visible — i.e. remote is on
            // (ShowPairingBlock, #384). A code minted while remote is off is invisible and churny: the
            // daemon holds one active code, so each mint invalidates any a phone is mid-scan against.
            // Harmless when this refresh only ran on the rarely-visited Remote page; #1068 folded it
            // into Settings, so it now runs on every "check Workers"/"flip the theme" visit, where an
            // unconditional mint would silently kill a live pairing code for an unrelated reason.
            if (ShowPairingBlock)
            {
                await GeneratePairingCodeAsync(session, cancellationToken).ConfigureAwait(true);
            }

            await RefreshPairedClientsAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-reads the paired-devices list. Called from <see cref="RefreshAsync"/> and again after a successful <see cref="RemovePairedClientAsync"/> so the list reflects the revocation immediately.</summary>
    public async Task RefreshPairedClientsAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        var clients = await session.GetPairedClientsAsync(cancellationToken).ConfigureAwait(true);
        if (clients == null) return;

        PairedClients.Clear();
        foreach (var client in clients)
        {
            PairedClients.Add(new PairedClientItemViewModel(
                client.ClientId, client.Name, client.PairedAt,
                clientId => RemovePairedClientAsync(session, clientId)));
        }
    }

    /// <summary>
    /// Re-reads the tsnet sidecar's state (Phase 5, #242) — called once from <see cref="RefreshAsync"/>
    /// and then repeatedly from <c>MainWindow</c>'s existing 1s pairing-countdown timer while
    /// <see cref="ShouldPollSidecarStatus"/> is true, so the one-time Tailscale sign-in link (and,
    /// once enrollment finishes, the tailnet address) show up without a manual refresh. Doesn't
    /// touch <see cref="IsBusy"/> — unlike <see cref="RefreshAsync"/>, this runs silently in the
    /// background every second and shouldn't flicker the toggle button's enabled state.
    /// </summary>
    public async Task RefreshSidecarStatusAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        var status = await session.GetSidecarStatusAsync(cancellationToken).ConfigureAwait(true);
        if (status == null)
        {
            SidecarReady = false;
            SidecarAuthUrl = null;
            SidecarTailscaleIp = null;
            SidecarError = "Could not reach the zero-config (Tailscale) sidecar.";
            return;
        }

        var justBecameReady = status.Ready && !SidecarReady;

        SidecarReady = status.Ready;
        SidecarAuthUrl = status.AuthUrl;
        SidecarTailscaleIp = status.TailscaleIp;
        SidecarError = status.Error;

        // EffectivePairingHost only picks up the tailnet address here when there's no LAN address at
        // all (issue #384: LAN is now preferred whenever available) -- still worth an immediate
        // rebuild rather than waiting for the next unrelated code refresh, so a LAN-less machine's
        // "Pair a phone" section doesn't keep showing no QR after the sidecar reaches ready.
        if (justBecameReady && EffectivePairingHost != null)
        {
            await GeneratePairingCodeAsync(session, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Signs the tsnet sidecar out and back into re-enrollment — the in-app replacement for
    /// deleting the node from the Tailscale admin console and restarting Aer.Ui. Not a
    /// <c>[RelayCommand]</c> for the same reason <see cref="ToggleRemoteAsync"/> isn't. The daemon's
    /// <c>/api/remote/sidecar-forget</c> answers as soon as the sidecar accepts the request — this
    /// just resets local state to <see cref="SidecarPhase.Starting"/> so the view falls back to
    /// "Starting..." immediately rather than briefly showing a stale Ready/tailnet-host, and the next
    /// few polls from <see cref="RefreshSidecarStatusAsync"/> pick up the fresh sign-in link.
    /// </summary>
    public async Task ForgetSidecarAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var ok = await session.ForgetSidecarAsync(cancellationToken).ConfigureAwait(true);
            if (!ok)
            {
                ErrorText = "Could not sign the sidecar out — it may not be running.";
                return;
            }

            SidecarReady = false;
            SidecarAuthUrl = null;
            SidecarTailscaleIp = null;
            SidecarError = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Revokes a paired device's token. Not a <c>[RelayCommand]</c> for the same reason <see cref="ToggleRemoteAsync"/> isn't — the view's code-behind supplies <paramref name="session"/> at call time.</summary>
    public async Task RemovePairedClientAsync(RoomClient session, string clientId, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var removed = await session.RevokePairedClientAsync(clientId, cancellationToken).ConfigureAwait(true);
            if (!removed)
            {
                ErrorText = "Could not revoke that device — it may have already been removed.";
                return;
            }

            await RefreshPairedClientsAsync(session, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One second of the pairing code's countdown (driven by <c>MainWindow</c>'s
    /// <c>DispatcherTimer</c>, the same pattern as its live-refresh timer — <c>Aer.Ui.Core</c>
    /// stays Avalonia-free, so the timer itself lives in the view layer). Floors at 0 rather than
    /// going negative; the caller is expected to fetch a fresh code once it does, since the daemon
    /// actually invalidates the code server-side at 60s (<c>PairingCodeManager.ValidateAndConsume</c>)
    /// — before this existed, the label was set once from the fetch response and never updated, so
    /// it always read "Expires in 60s" even long after the code had gone stale.
    /// </summary>
    public void TickPairingCodeCountdown()
    {
        if (PairingCodeExpiresInSeconds > 0)
        {
            PairingCodeExpiresInSeconds--;
        }
    }

    /// <summary>
    /// Fetches a fresh 60-second pairing code and rebuilds the QR against it — a separate,
    /// explicitly re-callable step since a code expires well before most other state does. On
    /// failure, keeps whatever code/QR was already showing and surfaces an error instead of
    /// blanking the screen — the countdown's own auto-refresh calls this the instant it hits 0, and
    /// a single transient failure right at that moment used to silently wipe a still-legible QR the
    /// user might be mid-scan against.
    /// </summary>
    public async Task GeneratePairingCodeAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        if (EffectivePairingHost is not { } pairingHost)
        {
            PairingCode = null;
            QrPngBytes = null;
            return;
        }

        var code = await session.GetPairingCodeAsync(cancellationToken).ConfigureAwait(true);
        if (code == null)
        {
            ErrorText = "Could not reach the Baton daemon for a new pairing code — will keep retrying.";
            return;
        }

        ErrorText = null;
        PairingCode = code.Code;
        PairingCodeExpiresInSeconds = code.ExpiresInSeconds;

        var payload = $"aer://pair?host={Uri.EscapeDataString(pairingHost)}&code={Uri.EscapeDataString(code.Code)}";
        // M21 Phase 7 follow-up (#246): a phone's own embedded tsnet node needs a real auth key for
        // its first-ever enrollment (the `tailscale` Dart package has no keyless first-enrollment
        // path) — riding it along in the same QR keeps scanning the only setup step on the phone.
        // Never sent over the network: TailscaleAuthKey is loaded straight from local config above,
        // and this string only ever becomes pixels in QrPngBytes.
        if (IsPairingOverTailnet && TailscaleAuthKey is { Length: > 0 } key)
        {
            payload += $"&tskey={Uri.EscapeDataString(key)}";
        }

        QrPngBytes = BuildQrPng(payload);
    }

    /// <summary>Persists an edited auth key and rebuilds the QR against it immediately, so the field's Save button doesn't need a full <see cref="RefreshAsync"/> round trip to take effect.</summary>
    public async Task SaveTailscaleAuthKeyAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        await session.RecordTailscaleAuthKeyAsync(TailscaleAuthKey, cancellationToken).ConfigureAwait(true);
        if (EffectivePairingHost != null)
        {
            await GeneratePairingCodeAsync(session, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>The toggle button's action — public and directly callable (not a <c>[RelayCommand]</c>), the same reason <see cref="HomeViewModel.RefreshAsync"/> takes a <see cref="RoomClient"/> parameter rather than capturing one: this ViewModel is constructed before the session exists (<see cref="MainWindowViewModel"/>'s property-initializer <c>new()</c>), so the view's code-behind supplies it at call time instead.</summary>
    public async Task ToggleRemoteAsync(RoomClient session)
    {
        IsBusy = true;
        ErrorText = null;

        try
        {
            var outcome = await session.SetRemoteEnabledAsync(!IsRemoteEnabled).ConfigureAwait(true);
            if (outcome.ErrorMessage != null)
            {
                ErrorText = outcome.ErrorMessage;
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"Toggle failed unexpectedly: {ex.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(session).ConfigureAwait(true);
    }

    private static byte[] BuildQrPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var pngQrCode = new PngByteQRCode(data);
        return pngQrCode.GetGraphic(10);
    }
}

/// <summary>
/// One row in the "Paired Devices" list (Phase 6, #243) — same shape as <c>HomeViewModel</c>'s
/// <c>InboxItemViewModel</c>: a small, purpose-built item ViewModel whose <see cref="RemoveCommand"/>
/// closes over the parent's already-available <c>RoomClient</c>, so the XAML can bind
/// <c>Command="{Binding RemoveCommand}"</c> directly per row instead of the shell wiring a single
/// static button (there's no way to know which row's button was clicked from a static handler).
/// </summary>
public sealed partial class PairedClientItemViewModel(
    string clientId, string name, DateTime pairedAt, Func<string, Task> removeAsync)
{
    public string ClientId { get; } = clientId;
    public string Name { get; } = name;
    public DateTime PairedAt { get; } = pairedAt;

    [RelayCommand]
    private Task Remove() => removeAsync(ClientId);
}
