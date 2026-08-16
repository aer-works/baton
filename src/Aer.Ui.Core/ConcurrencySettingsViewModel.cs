using CommunityToolkit.Mvvm.ComponentModel;

namespace Aer.Ui.Core;

/// <summary>
/// Settings → Workers' concurrency-cap fields (#1298, Fable's ruling on #448's adjustable half —
/// #1296 already shipped the read-only "cap bites, WaitingToStart shows" half). Daemon-side state:
/// <see cref="RoomClient.GetConcurrencySettingsAsync"/>/<see cref="RoomClient.SetConcurrencySettingsAsync"/>
/// round-trip to <c>/api/settings/concurrency</c>, which is backed by
/// <c>ConcurrencySlotGate.SetCaps</c> and persisted to <c>~/.aer/settings.json</c> — this type holds
/// only the two text-field values and in-flight/error UI state, the same split
/// <see cref="RemoteViewModel"/> uses for the same reason: constructed parameterless
/// (<see cref="MainWindowViewModel"/>'s property-initializer <c>new()</c>) before a
/// <see cref="RoomClient"/> session exists, so every daemon-touching method takes one as a parameter
/// rather than capturing one.
/// </summary>
public sealed partial class ConcurrencySettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string globalCapText = Aer.Adapters.DaemonSettings.DefaultGlobalConcurrencyCap.ToString();

    [ObservableProperty]
    private string perVendorCapText = Aer.Adapters.DaemonSettings.DefaultPerVendorConcurrencyCap.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorText;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>
    /// True once a real <see cref="RefreshAsync"/> has populated the fields from the daemon. The text
    /// fields start on <see cref="Aer.Adapters.DaemonSettings"/>'s hardcoded defaults before that,
    /// which are almost certainly not the daemon's actual caps — found on review: <c>MainWindow</c>
    /// fires <see cref="RefreshAsync"/> without awaiting it, so a Save clicked in that window (or one
    /// where the daemon was briefly unreachable and the refresh silently no-op'd) would round-trip the
    /// placeholder defaults and overwrite a real custom cap, possibly one another paired client set.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool isLoaded;

    /// <summary>What <see cref="SettingsView"/>'s Save button's <c>IsEnabled</c> binds to — busy, or never yet loaded real values, both block it.</summary>
    public bool CanSave => !IsBusy && IsLoaded;

    /// <summary>Reads the daemon's current caps into the text fields — called on every Settings activation, the same as <see cref="RemoteViewModel.RefreshAsync"/>, so a change made from another client is reflected on the next visit. Also gates <see cref="CanSave"/> (see <see cref="IsLoaded"/>'s remarks) and surfaces an unreachable daemon as an error rather than silently leaving stale/placeholder values in place.</summary>
    public async Task RefreshAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var settings = await session.GetConcurrencySettingsAsync(cancellationToken).ConfigureAwait(true);
            if (settings == null)
            {
                ErrorText = "Could not reach the Baton daemon to read the current caps.";
                return;
            }

            GlobalCapText = settings.GlobalCap.ToString();
            PerVendorCapText = settings.PerVendorCap.ToString();
            ErrorText = null;
            IsLoaded = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Parses and saves the two fields. A non-integer or below-1 value is rejected client-side with the same message the daemon would give, so a bad edit never round-trips just to be told what it already knows.</summary>
    public async Task SaveAsync(RoomClient session, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(GlobalCapText, out var globalCap) || !int.TryParse(PerVendorCapText, out var perVendorCap)
            || globalCap < 1 || perVendorCap < 1)
        {
            ErrorText = "Both caps must be whole numbers of at least 1.";
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            var error = await session.SetConcurrencySettingsAsync(globalCap, perVendorCap, cancellationToken).ConfigureAwait(true);
            ErrorText = error;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
