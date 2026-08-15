using System.Collections.ObjectModel;
using Aer.Adapters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// Delegate for fetching a room's standing permissions (<see cref="StandingPermissionsViewModel"/>).
/// </summary>
public delegate Task<StandingPermissionsResult> GetStandingPermissionsDelegate(
    string roomDirectoryPath, string? workerName, CancellationToken cancellationToken);

/// <summary>
/// Delegate for revoking a room's standing permission (<see cref="StandingPermissionsViewModel"/>).
/// </summary>
public delegate Task<RoomClient.MutationOutcome> RevokePermissionDelegate(
    string roomDirectoryPath, string revokeKind, string? shellCommandPattern, string? workerName, CancellationToken cancellationToken);

/// <summary>
/// Represents one standing permission item (allowed shell, per-command allow, or standing refusal)
/// in <see cref="StandingPermissionsViewModel.Entries"/>.
/// </summary>
public sealed partial class StandingPermissionItemViewModel : ObservableObject
{
    private readonly StandingPermissionsViewModel _parent;

    public string DisplayTitle { get; }
    public string Description { get; }
    public string RevokeKind { get; }
    public string? ShellCommandPattern { get; }
    public bool IsDeniedPattern { get; }
    public bool CanRevoke => !IsDeniedPattern;
    public string RevokeToolTip => IsDeniedPattern
        ? "Standing refusals cannot be revoked from this surface"
        : "Revoke this standing permission";

    public StandingPermissionItemViewModel(
        StandingPermissionsViewModel parent,
        string displayTitle,
        string description,
        string revokeKind,
        string? shellCommandPattern,
        bool isDeniedPattern)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        DisplayTitle = displayTitle;
        Description = description;
        RevokeKind = revokeKind;
        ShellCommandPattern = shellCommandPattern;
        IsDeniedPattern = isDeniedPattern;
    }

    [RelayCommand(CanExecute = nameof(CanRevoke))]
    private Task RevokeAsync() => _parent.RequestRevokeItemAsync(this);
}

/// <summary>
/// ViewModel for viewing and revoking a room's standing permissions (issue #1272).
/// Displays shell permissions, per-command allows, and standing refusals (denied patterns),
/// enforcing confirmation before revoking a whole room's shell access.
/// </summary>
public sealed partial class StandingPermissionsViewModel : ObservableObject
{
    private readonly GetStandingPermissionsDelegate? _getPermissions;
    private readonly RevokePermissionDelegate? _revokePermission;

    private string? _currentRoomDirectoryPath;
    private string? _currentWorkerName;

    public ObservableCollection<StandingPermissionItemViewModel> Entries { get; } = [];

    [ObservableProperty]
    private bool isStandingPermissionsOpen;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoWorkerSetup))]
    [NotifyPropertyChangedFor(nameof(IsWorkerNotConfigured))]
    [NotifyPropertyChangedFor(nameof(IsConfiguredEmpty))]
    private string? outcome;

    [ObservableProperty]
    private string? outcomeMessage;

    public bool IsNoWorkerSetup => Outcome == StandingPermissionReadOutcome.NoWorkerSetup.ToString();
    public bool IsWorkerNotConfigured => Outcome == StandingPermissionReadOutcome.WorkerNotConfigured.ToString();
    public bool IsConfiguredEmpty => Outcome == StandingPermissionReadOutcome.Configured.ToString() && Entries.Count == 0;

    [ObservableProperty]
    private bool isConfirmingRevokeRoomShell;

    [ObservableProperty]
    private StandingPermissionItemViewModel? pendingConfirmItem;

    public StandingPermissionsViewModel()
    {
    }

    public StandingPermissionsViewModel(
        GetStandingPermissionsDelegate getPermissions,
        RevokePermissionDelegate revokePermission)
    {
        _getPermissions = getPermissions;
        _revokePermission = revokePermission;
    }

    /// <summary>
    /// Toggles the panel and, on opening, loads for <paramref name="roomDirectoryPath"/>. Takes the
    /// path as a parameter rather than reading a stored one: this ViewModel has no way to know which
    /// room is open — only <c>MainWindow</c>'s codebehind does (<c>RoomClient.CurrentRoomDirectoryPath</c>);
    /// see <see cref="MainWindowViewModel.StandingPermissions"/> for the split this follows.
    /// </summary>
    public void ToggleOpen(string? roomDirectoryPath, string? workerName = null)
    {
        IsStandingPermissionsOpen = !IsStandingPermissionsOpen;
        if (IsStandingPermissionsOpen && !string.IsNullOrEmpty(roomDirectoryPath))
        {
            _ = LoadAsync(roomDirectoryPath, workerName);
        }
    }

    public async Task LoadAsync(string roomDirectoryPath, string? workerName = null, CancellationToken cancellationToken = default)
    {
        _currentRoomDirectoryPath = roomDirectoryPath;
        _currentWorkerName = workerName;

        ErrorMessage = null;
        IsLoading = true;
        Entries.Clear();

        try
        {
            if (_getPermissions == null)
            {
                IsLoading = false;
                return;
            }

            var result = await _getPermissions(roomDirectoryPath, workerName, cancellationToken).ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
                Outcome = null;
                OutcomeMessage = null;
                return;
            }

            Outcome = result.Outcome;

            if (result.Outcome == StandingPermissionReadOutcome.NoWorkerSetup.ToString())
            {
                OutcomeMessage = "No worker setup in this room.";
            }
            else if (result.Outcome == StandingPermissionReadOutcome.WorkerNotConfigured.ToString())
            {
                OutcomeMessage = "Worker not configured in this room.";
            }
            else if (result.Outcome == StandingPermissionReadOutcome.Configured.ToString())
            {
                if (result.RunShellCommands)
                {
                    Entries.Add(new StandingPermissionItemViewModel(
                        this,
                        "Run shell commands",
                        "Full room shell access",
                        PermissionRevokeKind.RoomShell,
                        null,
                        isDeniedPattern: false));
                }

                foreach (var pattern in result.ShellCommandPatterns)
                {
                    Entries.Add(new StandingPermissionItemViewModel(
                        this,
                        $"Allowed command: {pattern}",
                        $"Standing allow for '{pattern}'",
                        PermissionRevokeKind.CommandInRoom,
                        pattern,
                        isDeniedPattern: false));
                }

                foreach (var pattern in result.DeniedShellCommandPatterns)
                {
                    Entries.Add(new StandingPermissionItemViewModel(
                        this,
                        $"Denied command: {pattern}",
                        $"Standing refusal for '{pattern}'",
                        "",
                        pattern,
                        isDeniedPattern: true));
                }

                if (Entries.Count == 0)
                {
                    OutcomeMessage = "No standing permissions in this room.";
                }
                else
                {
                    OutcomeMessage = null;
                }
            }

            OnPropertyChanged(nameof(IsConfiguredEmpty));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RequestRevokeItemAsync(StandingPermissionItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.IsDeniedPattern) return;

        if (item.RevokeKind == PermissionRevokeKind.RoomShell)
        {
            PendingConfirmItem = item;
            IsConfirmingRevokeRoomShell = true;
            return;
        }

        await ExecuteRevokeAsync(item.RevokeKind, item.ShellCommandPattern).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task ConfirmRevokeRoomShellAsync()
    {
        IsConfirmingRevokeRoomShell = false;
        var item = PendingConfirmItem;
        PendingConfirmItem = null;

        if (item != null)
        {
            await ExecuteRevokeAsync(item.RevokeKind, item.ShellCommandPattern).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public void CancelRevokeRoomShell()
    {
        IsConfirmingRevokeRoomShell = false;
        PendingConfirmItem = null;
    }

    private async Task ExecuteRevokeAsync(string revokeKind, string? shellCommandPattern)
    {
        if (string.IsNullOrEmpty(_currentRoomDirectoryPath) || _revokePermission == null) return;

        ErrorMessage = null;
        var outcome = await _revokePermission(_currentRoomDirectoryPath, revokeKind, shellCommandPattern, _currentWorkerName, CancellationToken.None).ConfigureAwait(true);

        if (!string.IsNullOrEmpty(outcome.ErrorMessage))
        {
            ErrorMessage = outcome.ErrorMessage;
            return;
        }

        // Re-fetch after successful revoke
        await LoadAsync(_currentRoomDirectoryPath, _currentWorkerName).ConfigureAwait(true);
    }
}
