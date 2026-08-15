using Aer.Adapters;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;

namespace Aer.Ui.Core;

/// <summary>
/// Response shape for reading a room's standing permissions (<see cref="RoomClient.GetStandingPermissionsAsync"/>).
/// </summary>
public sealed record StandingPermissionsResult(
    string Outcome,
    bool RunShellCommands,
    IReadOnlyList<string> ShellCommandPatterns,
    IReadOnlyList<string> DeniedShellCommandPatterns,
    string? ErrorMessage = null);

public sealed partial class RoomClient
{
    private sealed record RoomPermissionsResponseDto(
        string Outcome,
        bool RunShellCommands,
        IReadOnlyList<string>? ShellCommandPatterns,
        IReadOnlyList<string>? DeniedShellCommandPatterns);

    /// <summary>
    /// Reads a room's standing permissions for <paramref name="workerName"/> (or room-wide default when null),
    /// calling <c>GET /api/rooms/permissions?directoryPath=...&amp;workerName=...</c>.
    /// </summary>
    public async Task<StandingPermissionsResult> GetStandingPermissionsAsync(
        string roomDirectoryPath, string? workerName = null, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var query = $"directoryPath={Uri.EscapeDataString(roomDirectoryPath)}";
                if (!string.IsNullOrEmpty(workerName))
                {
                    query += $"&workerName={Uri.EscapeDataString(workerName)}";
                }

                var response = await _httpClient.GetAsync(
                    $"{_activeDaemonUrl}/api/rooms/permissions?{query}",
                    cancellationToken).ConfigureAwait(true);

                if (response.IsSuccessStatusCode)
                {
                    var dto = await response.Content.ReadFromJsonAsync<RoomPermissionsResponseDto>(
                        DefaultJsonOptions, cancellationToken).ConfigureAwait(true);
                    if (dto != null)
                    {
                        return new StandingPermissionsResult(
                            dto.Outcome,
                            dto.RunShellCommands,
                            dto.ShellCommandPatterns ?? [],
                            dto.DeniedShellCommandPatterns ?? []);
                    }
                }

                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                return new StandingPermissionsResult(
                    StandingPermissionReadOutcome.NoWorkerSetup.ToString(),
                    false, [], [],
                    ErrorMessage: err);
            }
            catch (Exception ex)
            {
                return new StandingPermissionsResult(
                    StandingPermissionReadOutcome.NoWorkerSetup.ToString(),
                    false, [], [],
                    ErrorMessage: ex.Message);
            }
        }

        // In-process fallback
        try
        {
            var targetWorker = string.IsNullOrEmpty(workerName)
                ? InteractiveSessionMaterializer.DefaultWorkerName
                : workerName;

            var readResult = await RuntimePermissionGrantAmender.GetStandingPermissionsAsync(
                roomDirectoryPath, targetWorker, cancellationToken).ConfigureAwait(true);

            var grant = readResult.Grant ?? new PermissionGrant();
            return new StandingPermissionsResult(
                readResult.Outcome.ToString(),
                grant.RunShellCommands,
                grant.ShellCommandPatterns ?? [],
                grant.DeniedShellCommandPatterns ?? []);
        }
        catch (Exception ex)
        {
            return new StandingPermissionsResult(
                StandingPermissionReadOutcome.NoWorkerSetup.ToString(),
                false, [], [],
                ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Revokes a standing permission in <paramref name="roomDirectoryPath"/> for <paramref name="revokeKind"/>
    /// and optional <paramref name="shellCommandPattern"/>, calling <c>POST /api/rooms/permissions/revoke</c>.
    /// </summary>
    public async Task<MutationOutcome> RevokePermissionAsync(
        string roomDirectoryPath,
        string revokeKind,
        string? shellCommandPattern = null,
        string? workerName = null,
        CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            ViewModel.IsMutationInFlight = true;
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/rooms/permissions/revoke",
                    new
                    {
                        DirectoryPath = roomDirectoryPath,
                        RevokeKind = revokeKind,
                        ShellCommandPattern = shellCommandPattern,
                        WorkerName = workerName,
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

        // In-process fallback
        ViewModel.IsMutationInFlight = true;
        try
        {
            var targetWorker = string.IsNullOrEmpty(workerName)
                ? InteractiveSessionMaterializer.DefaultWorkerName
                : workerName;

            var outcome = await RuntimePermissionGrantAmender.RevokeAsync(
                roomDirectoryPath, targetWorker, revokeKind, shellCommandPattern, cancellationToken).ConfigureAwait(true);

            // Revoked and NothingToRevoke are both success — revoking twice is the same state as
            // revoking once, per PermissionRevokeOutcome's own doc comment. Only CouldNotPersist
            // leaves the person with more than they asked to take back, which is the one to surface.
            return outcome == PermissionRevokeOutcome.CouldNotPersist
                ? new MutationOutcome($"Could not revoke permission: {outcome}")
                : new MutationOutcome(null);
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

