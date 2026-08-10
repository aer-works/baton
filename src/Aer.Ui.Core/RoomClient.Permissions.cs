using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;

namespace Aer.Ui.Core;

public sealed partial class RoomClient
{
    /// <summary>
    /// Answers a runtime permission (0022's ladder, #390) by posting to
    /// <c>/api/rooms/permissions/answer</c> — the daemon writes the rendezvous answer file that
    /// releases the held worker, records the <c>RuntimePermissionAnswered</c> turn, amends the room's
    /// chat-worker grant for a persisting rung (<see cref="Aer.Adapters.RuntimePermissionGrantAmender"/>),
    /// and broadcasts a fresh projection whose <c>PendingPermission</c> is now clear.
    /// </summary>
    /// <remarks>
    /// Daemon-only, no in-process fallback: the pending gate registry and the rendezvous files this
    /// resolves against live in the daemon's process (<c>PendingGateRegistry</c>), so answering without
    /// one reachable could not release the worker. The request body is an anonymous object rather than
    /// the daemon's <c>AnswerPermissionRequest</c> record because that type lives in <c>Aer.Daemon</c>,
    /// which this assembly does not (and must not) reference; ASP.NET's case-insensitive body binding
    /// matches the field names. <c>UpdatedInput</c> is omitted — this surface answers, it does not edit
    /// the asked call.
    /// </remarks>
    public async Task<MutationOutcome> AnswerPermissionAsync(
        string roomDirectoryPath, string permissionRequestId, string decisionKind, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("Answering a permission requires the daemon, and none is reachable.");
        }

        // The answer holds the room lock while it amends the grant, so it disables the other mutation
        // surfaces (and, via OnIsMutationInFlightChanged, the gate itself) for its duration — the same
        // in-flight discipline every RoomClient mutation follows, which is what stops a double-submit.
        ViewModel.IsMutationInFlight = true;
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_activeDaemonUrl}/api/rooms/permissions/answer",
                new
                {
                    DirectoryPath = roomDirectoryPath,
                    PermissionRequestId = permissionRequestId,
                    DecisionKind = decisionKind,
                    Reason = reason,
                },
                cancellationToken).ConfigureAwait(true);

            return response.IsSuccessStatusCode
                ? new MutationOutcome(null)
                : new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
        }
    }
}
