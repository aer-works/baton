using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aer.Ui.Core;

public sealed partial class RoomClient
{
    private sealed record ClearDormancyRequest(string RoomDirectoryPath);

    /// <summary>
    /// Fetches turn host status for <paramref name="roomDirectoryPath"/>. Returns null on ANY non-200 status code
    /// (409 conflict when not the hosted room, connection failure, timeout, etc.) — absence, not error.
    /// </summary>
    public async Task<RoomTurnHostStatus?> TryGetTurnHostStatusAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomDirectoryPath)) return null;
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true)) return null;

        try
        {
            var encodedPath = System.Uri.EscapeDataString(roomDirectoryPath);
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/rooms/turn-host/status?roomDirectoryPath={encodedPath}", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RoomTurnHostStatus>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clears turn host dormancy for <paramref name="roomDirectoryPath"/>. Returns true on success, false otherwise.
    /// </summary>
    public async Task<bool> ClearTurnHostDormancyAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomDirectoryPath)) return false;
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true)) return false;

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_activeDaemonUrl}/api/rooms/turn-host/clear-dormancy",
                new ClearDormancyRequest(roomDirectoryPath),
                cancellationToken).ConfigureAwait(true);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// #994: refreshes RoomTurnHostBanner on ViewModel from the daemon's status endpoint.
    /// </summary>
    public async Task RefreshRoomTurnHostBannerAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var status = await TryGetTurnHostStatusAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        if (status != null)
        {
            ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(status);
        }
        else
        {
            ViewModel.RoomTurnHostBanner = null;
        }
    }
}
