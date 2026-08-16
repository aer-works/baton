using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aer.Ui.Core;

public sealed partial class RoomClient
{
    private sealed record ClearDormancyRequest(string RoomDirectoryPath);

    private sealed record SetWorkflowSwitchRequest(string RoomDirectoryPath, bool IsOn);

    private sealed record ReassignOrchestratorRequest(string RoomDirectoryPath, string WorkerId);

    /// <summary>
    /// Switches the room's workflow on or off (#1216). Returns <see langword="null"/> on success, or
    /// the engine's own reason for refusing — which the header shows the person rather than reverting
    /// the switch silently. A refusal is not a failure: it is the answer to "can I do this now", and
    /// it is the whole reason the switch refuses rather than mutating a room something is still
    /// driving (see <c>RoomMutationInterface.SetWorkflowSwitchAsync</c>).
    /// </summary>
    public async Task<string?> SetWorkflowSwitchAsync(string roomDirectoryPath, bool isOn, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomDirectoryPath)) return "No room is open.";
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true)) return "Baton's background service is not reachable.";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_activeDaemonUrl}/api/rooms/workflow-switch",
                new SetWorkflowSwitchRequest(roomDirectoryPath, isOn),
                cancellationToken).ConfigureAwait(true);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return string.IsNullOrWhiteSpace(body) ? "The workflow could not be switched." : body.Trim('"');
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Reassigns the room's orchestrator to <paramref name="workerId"/> (#592, 0054 §6). Returns
    /// <see langword="null"/> on success, or the engine's own reason for refusing — the same shape as
    /// <see cref="SetWorkflowSwitchAsync"/>, for the same reason: a refusal is the answer to "can I do
    /// this now", not a failure to be swallowed.
    /// </summary>
    public async Task<string?> ReassignOrchestratorAsync(string roomDirectoryPath, string workerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomDirectoryPath)) return "No room is open.";
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true)) return "Baton's background service is not reachable.";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_activeDaemonUrl}/api/rooms/orchestrator/reassign",
                new ReassignOrchestratorRequest(roomDirectoryPath, workerId),
                cancellationToken).ConfigureAwait(true);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return string.IsNullOrWhiteSpace(body) ? "The orchestrator could not be reassigned." : body.Trim('"');
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

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
