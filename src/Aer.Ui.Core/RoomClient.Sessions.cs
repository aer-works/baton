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
using Aer.RoomSession;

namespace Aer.Ui.Core;

public sealed partial class RoomClient
{
    /// <summary>
    /// Starts an interactive chat/codebase session (M24 Phase 1, issue #262) the same daemon-first,
    /// in-process-fallback way as <see cref="RunAsync"/>. Unlike RunAsync's fallback, there is no
    /// Aer.Cli equivalent for dispatching a session's initial turn -- that logic
    /// (<c>ExecuteSessionTurnAsync</c>) lives only in <c>Aer.Daemon.Program</c> -- so without a
    /// reachable daemon, <see cref="StartSessionRequest.InitialMessage"/> materializes the session
    /// but is not auto-answered; the caller can send it afterward once a daemon is available.
    /// </summary>
    public async Task<SessionStartOutcome> StartInteractiveSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/start", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: cancellationToken).ConfigureAwait(true);
                    return new SessionStartOutcome(metadata, null);
                }

                var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                return new SessionStartOutcome(null, err);
            }
            catch (Exception ex)
            {
                return new SessionStartOutcome(null, ex.Message);
            }
        }

        // In-process fallback: materialize locally, same directory-naming rule the daemon endpoint
        // uses (InteractiveSessionMaterializer.ResolveRoomDirectoryPath), so a session created
        // without a daemon lands wherever a later-started daemon's /api/sessions endpoints would
        // also look for it.
        try
        {
            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var roomDirectoryPath = InteractiveSessionMaterializer.ResolveRoomDirectoryPath(sessionId, request.RoomName, request.DirectoryPath);

            var metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId,
                roomDirectoryPath,
                string.IsNullOrWhiteSpace(request.Adapter) ? "claude" : request.Adapter.Trim().ToLowerInvariant(),
                request.Model,
                request.WorkingDirectory,
                request.InitialMessage,
                request.SafetyCeiling ?? InteractiveSessionMaterializer.DefaultSafetyCeiling,
                request.PermissionGrant,
                cancellationToken).ConfigureAwait(true);

            SetCurrentRoomDirectory(roomDirectoryPath);
            await RecordOpenedAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);

            return new SessionStartOutcome(metadata, null);
        }
        catch (Exception ex)
        {
            return new SessionStartOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// Sends the next turn to an already-started interactive session (M24 Phase 1 desktop chat UI,
    /// issue #262). Unlike <see cref="StartInteractiveSessionAsync"/> there is no in-process
    /// fallback here: <c>ExecuteSessionTurnAsync</c> only exists in <c>Aer.Daemon.Program</c>, and
    /// <c>POST /api/sessions/send</c> itself only confirms the turn was dispatched onto the
    /// daemon's own background task, not that it completed -- the caller observes completion by
    /// polling <see cref="LoadSessionMetadataAsync"/> the same way every other live room state in
    /// this app is observed (<c>MainWindow</c>'s existing 2-second poll).
    /// </summary>
    public async Task<MutationOutcome> SendSessionMessageAsync(SendSessionMessageRequest request, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("A room turn requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/send", request, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return new MutationOutcome(null);
            }

            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(err);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>
    /// Discovered skills/commands/agents/models for a session's current vendor, plus this vendor's
    /// most-recently-used entries (M24 Phase 2 follow-up chat capability picker). No in-process
    /// fallback, same reasoning as <see cref="SendSessionMessageAsync"/> -- this is purely a daemon
    /// read.
    /// </summary>
    public async Task<(SessionCommandsResult? Result, string? ErrorMessage)> GetSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Discovering commands requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/commands", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var result = await response.Content.ReadFromJsonAsync<SessionCommandsResult>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Best-effort: a picked command failing to record as "recently used" shouldn't block or error the chat UI.</summary>
    public async Task RecordCommandUsedAsync(string sessionId, string commandName, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/commands/record", new { Name = commandName }, cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // Recency is a convenience, not a correctness requirement -- see LocalUiConfigurationStore's own remarks.
        }
    }

    /// <summary>Session-level mode (M24 Phase 2 follow-up): "auto", "default", or "plan", applying to whichever vendor is currently active.</summary>
    public async Task<MutationOutcome> SetSessionModeAsync(string sessionId, string mode, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("Setting room mode requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/mode", new { Mode = mode }, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return new MutationOutcome(null);
            }

            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(err);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>The currently active session mode (#286), reverse-mapped server-side from the persisted PermissionGrant — "auto", "default", "plan", or "custom".</summary>
    public async Task<(string? Mode, string? ErrorMessage)> GetSessionModeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Reading room mode requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/mode", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var result = await response.Content.ReadFromJsonAsync<SessionModeResult>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return (result?.Mode, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Compacts a session's history (#286): wires the chat command picker's "/compact" item to the
    /// real dedicated action instead of inserting literal text and hoping the vendor's own
    /// (unverified) slash-command handling does something with it. Fire-and-forget on the daemon
    /// side, same as <see cref="SendSessionMessageAsync"/> — completion is observed via the existing
    /// metadata poll, not this call's response.
    /// </summary>
    public async Task<MutationOutcome> CompactSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return new MutationOutcome("Compacting a room requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/compact", null, cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                return new MutationOutcome(null);
            }

            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(err);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>
    /// Clears a session's visible transcript and forces the next turn to start a genuinely fresh
    /// native session (#286) — unlike <see cref="CompactSessionAsync"/> this never talks to the
    /// vendor and completes synchronously, so the caller gets the updated (empty-turns) metadata
    /// back directly rather than needing to poll for it.
    /// </summary>
    public async Task<(SessionMetadata? Result, string? ErrorMessage)> ClearSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Clearing a room requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.PostAsync($"{_activeDaemonUrl}/api/sessions/{sessionId}/clear", null, cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var result = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: cancellationToken).ConfigureAwait(true);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private sealed record SessionModeResult(string? Mode);

    /// <summary>
    /// Reads <paramref name="roomDirectoryPath"/>'s <c>.aer/room.json</c> directly rather than
    /// round-tripping through the daemon (unlike <see cref="LoadAsync"/>'s <c>RoomProjection</c>,
    /// <see cref="SessionMetadata"/> is a directly-readable local artifact with no in-memory
    /// projection of its own) -- also doubles as the "is this room directory a chat/codebase
    /// session" check <c>MainWindow.OpenAsync</c> uses to decide whether to route to the Chat view.
    /// Returns <see langword="null"/> for a directory that isn't an interactive session at all.
    /// </summary>
    public async Task<SessionMetadata?> LoadSessionMetadataAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        // A workflow room now also has a room.json marker, but LoadMetadataAsync returns null for it
        // (its SessionId is empty), so this still answers "is this an interactive session" the same
        // way MainWindow.OpenAsync has always relied on.
        return await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, cancellationToken).ConfigureAwait(true);
    }
}
