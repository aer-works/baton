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
using Aer.Flow.Projection;

namespace Aer.Ui.Core;

public sealed partial class RoomClient
{
    private (string Url, string Token)? GetDaemonConnectionInfo()
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return null;

        var aerDir = AerPaths.Root;
        if (!Directory.Exists(aerDir)) return null;

        var tokenFile = Path.Combine(aerDir, "daemon.token");
        if (!File.Exists(tokenFile)) return null;

        var token = File.ReadAllText(tokenFile).Trim();

        var portFile = Path.Combine(aerDir, "daemon.port");
        var activePort = 5000;
        if (File.Exists(portFile))
        {
            if (int.TryParse(File.ReadAllText(portFile).Trim(), out var p))
            {
                activePort = p;
            }
        }

        var url = $"http://localhost:{activePort}";
        return (url, token);
    }

    private void ConfigureHttpClientHeaders(string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<bool> EnsureDaemonConnectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return false;
        if (_isClientMode) return true;

        var conn = GetDaemonConnectionInfo();
        _activeDaemonUrl = conn?.Url ?? _daemonUrl;
        var token = conn?.Token;

        if (token != null)
        {
            ConfigureHttpClientHeaders(token);
        }

        try
        {
            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", cancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(cancellationToken: cancellationToken).ConfigureAwait(true);
                var clientVersion = typeof(RoomClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";

                if (meta != null && meta.Version == clientVersion)
                {
                    _isClientMode = true;
                    await StartWebSocketListenerAsync(_activeDaemonUrl, token, cancellationToken).ConfigureAwait(true);
                    return true;
                }
                else if (meta != null && !meta.HasRunningRooms)
                {
                    // Version skew! Force shutdown running daemon to respawn updated one (safe since no rooms are running)
                    await _httpClient.PostAsync($"{_activeDaemonUrl}/api/daemon/shutdown", null, cancellationToken).ConfigureAwait(true);
                    await Task.Delay(500, cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    // Version skew, but daemon has running rooms in flight. We must not terminate it;
                    // continue using the running daemon to preserve room integrity.
                    _isClientMode = true;
                    await StartWebSocketListenerAsync(_activeDaemonUrl, token, cancellationToken).ConfigureAwait(true);
                    return true;
                }
            }
        }
        catch
        {
            // Connect failed
        }

        // #998: gated because a RoomClient constructed in a test reaches this fallback the
        // moment its probe fails — and the spawned daemon re-registers itself in the REAL
        // ~/.aer, clobbering the operator's registration and leaking a process that holds DLL
        // locks against every later build. The desktop app keeps the default (true).
        if (!_spawnDaemonOnDemand)
        {
            return false;
        }

        return await SpawnDaemonProcessAsync("", cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Launches a fresh <c>Aer.Daemon</c> child process (appending <paramref name="extraArgs"/> to
    /// its command line, e.g. <c>--remote</c>) and polls <c>/api/version</c> until it answers or 30
    /// attempts are exhausted. Shared by <see cref="EnsureDaemonConnectedAsync"/>'s cold-start path
    /// and <see cref="SetRemoteEnabledAsync"/>'s shutdown-then-respawn (M21 Phase 3, issue #234) —
    /// both need the exact same "start it, then wait for it to come up" dance.
    /// </summary>
    private async Task<bool> SpawnDaemonProcessAsync(string extraArgs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_daemonUrl)) return false;

        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string daemonPath;
            string args = "";

            if (OperatingSystem.IsWindows())
            {
                daemonPath = Path.Combine(baseDir, "Aer.Daemon.exe"); // vocabulary-ok: binary file path
                if (!File.Exists(daemonPath))
                {
                    daemonPath = "dotnet";
                    args = $"\"{Path.Combine(baseDir, "Aer.Daemon.dll")}\"";
                }
            }
            else
            {
                daemonPath = Path.Combine(baseDir, "Aer.Daemon"); // vocabulary-ok: binary file path
                if (!File.Exists(daemonPath))
                {
                    daemonPath = "dotnet";
                    args = $"\"{Path.Combine(baseDir, "Aer.Daemon.dll")}\"";
                }
            }

            if (!string.IsNullOrEmpty(extraArgs))
            {
                args = string.IsNullOrEmpty(args) ? extraArgs : $"{args} {extraArgs}";
            }

            var hasDll = File.Exists(Path.Combine(baseDir, "Aer.Daemon.dll")); // vocabulary-ok: binary file path
            var hasExe = File.Exists(Path.Combine(baseDir, "Aer.Daemon.exe")) || File.Exists(Path.Combine(baseDir, "Aer.Daemon")); // vocabulary-ok: binary file path

            if (hasExe || hasDll)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = daemonPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                var process = Process.Start(startInfo);
                if (process != null)
                {
                    // Previously unredirected, so a spawn failure (bind conflict, unhandled
                    // exception at startup, anything) was completely invisible — the polling loop
                    // below just kept failing with no way to tell why. Async reads (not sync
                    // ReadToEnd) so a chatty child can't block on a full stdout/stderr pipe buffer.
                    try
                    {
                        var aerDir = AerPaths.Root;
                        Directory.CreateDirectory(aerDir);
                        var logPath = Path.Combine(aerDir, "daemon-spawn.log");
                        File.WriteAllText(logPath, $"--- spawn {DateTime.UtcNow:O} (args: {args}) ---{Environment.NewLine}");
                        process.OutputDataReceived += (_, e) => { if (e.Data != null) TryAppendLog(logPath, e.Data); };
                        process.ErrorDataReceived += (_, e) => { if (e.Data != null) TryAppendLog(logPath, e.Data); };
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                    }
                    catch
                    {
                        // Best-effort diagnostics only — never block a real spawn attempt on logging.
                    }

                    _ = Task.Run(() => SuperviseDaemonProcessAsync(process), CancellationToken.None);

                    for (int i = 0; i < 30; i++)
                    {
                        // A child process that has already exited (e.g. it lost the single-instance
                        // mutex race against a still-shutting-down old daemon — found live, M22
                        // planning: a caller that respawns without confirming the old process is
                        // truly gone first hits exactly this) will never answer /api/version. Fail
                        // fast instead of burning the rest of this loop's budget on a dead process.
                        if (process.HasExited) break;

                        try
                        {
                            var newConn = GetDaemonConnectionInfo();
                            _activeDaemonUrl = newConn?.Url ?? _daemonUrl;
                            var newToken = newConn?.Token;
                            if (newToken != null)
                            {
                                ConfigureHttpClientHeaders(newToken);
                            }

                            // Each attempt gets its own short deadline rather than inheriting
                            // _httpClient's full 5s default — 30 attempts at up to 5s each could
                            // stretch this loop to minutes if a request ever hangs instead of
                            // failing fast (found live: a stale port mid-handoff did exactly this).
                            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            attemptCts.CancelAfter(TimeSpan.FromMilliseconds(400));

                            var response = await _httpClient.GetAsync($"{_activeDaemonUrl}/api/version", attemptCts.Token).ConfigureAwait(true);
                            if (response.IsSuccessStatusCode)
                            {
                                _isClientMode = true;
                                await StartWebSocketListenerAsync(_activeDaemonUrl, newToken, cancellationToken).ConfigureAwait(true);
                                return true;
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // This attempt's own short deadline elapsed, not the caller's token — keep polling.
                        }
                        catch
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
                        }
                    }
                }
            }
        }
        catch
        {
            // Start process failed
        }

        _isClientMode = false;
        return false;
    }

    private static readonly Lock LogLock = new();

    private static void TryAppendLog(string logPath, string line)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private async Task SuperviseDaemonProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (_isClientMode)
            {
                _isClientMode = false;
                if (CurrentRoomDirectoryPath != null)
                {
                    _ = EnsureDaemonConnectedAsync(CancellationToken.None);
                }
            }
        }
        catch
        {
            // Supervision failed
        }
    }

    private async Task StartWebSocketListenerAsync(string resolvedUrl, string? token, CancellationToken cancellationToken)
    {
        _wsCts?.Cancel();
        _wsCts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        var wsUrl = resolvedUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/api/ws";
        if (token != null)
        {
            wsUrl += $"?token={token}";
        }
        try
        {
            await _webSocket.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(true);
            _ = Task.Run(() => ReceiveWebSocketDataAsync(_webSocket, _wsCts.Token), _wsCts.Token);
        }
        catch
        {
            // WS connect failed
        }

        await StartProgressWebSocketListenerAsync(resolvedUrl, token, cancellationToken).ConfigureAwait(true);
    }

    private sealed record ProgressFrame(string DirectoryPath, string StepId, string Kind, string Text, bool IsPartial);

    /// <summary>
    /// Mirrors <see cref="RoomProjection"/>'s shape plus the <c>DirectoryPath</c> sibling property
    /// Aer.Daemon bolts onto every <c>/api/ws</c> frame (M21 Phase 2, #232). Deserializing straight
    /// into <see cref="RoomProjection"/>, as <see cref="ReceiveWebSocketDataAsync"/> did before,
    /// silently drops that sibling — leaving no way to tell whether an incoming push is even for the
    /// directory this client has open, so every push got applied unconditionally regardless of which
    /// client's action produced it. This wrapper exists solely so the receive loop can filter.
    /// </summary>
    /// <remarks>
    /// Every <see cref="RoomProjection"/> member must be mirrored here, or it is silently dropped on
    /// every live push while the room-open HTTP load (which deserializes straight into
    /// <see cref="RoomProjection"/>) still carries it — <c>PendingPermission</c> was, #445/#390. New
    /// members carry defaults, so <see cref="ToProjection"/> keeps compiling when one is missing;
    /// <c>WebSocketProjectionFrameTests</c> is the structural check that catches the next one.
    /// </remarks>
    internal sealed record ProjectionFrame(
        string? DirectoryPath,
        WorkflowDefinitionSnapshot Snapshot,
        FlowState State,
        ExecutionHistory History,
        ArtifactLineage Lineage,
        PendingPermission? PendingPermission,
        IReadOnlyList<PermissionAnswer>? PermissionAnswers = null,
        IReadOnlyList<DormancyTransition>? DormancyTransitions = null,
        IReadOnlyList<StepPauseMoment>? StepPauseMoments = null,
        IReadOnlyList<RecordedDecisionMoment>? RecordedDecisionMoments = null);

    /// <summary>Rebuilds the projection a live WS frame carries — the one place the frame's members
    /// map back onto <see cref="RoomProjection"/>. Every member goes through here (see the frame's
    /// remarks for why an omission is invisible on the HTTP path).</summary>
    internal static RoomProjection ToProjection(ProjectionFrame frame) =>
        new(frame.Snapshot, frame.State, frame.History, frame.Lineage, frame.PendingPermission, frame.PermissionAnswers, frame.DormancyTransitions, frame.StepPauseMoments, frame.RecordedDecisionMoments);

    /// <summary>The M24 Phase 1 live in-turn streaming socket's client-side counterpart -- see <see cref="SessionProgressReceived"/>. A dedicated connection, not folded into <see cref="StartWebSocketListenerAsync"/>'s own socket, for the exact same reason the daemon keeps the two endpoints separate (<c>Aer.Daemon.Program</c>'s <c>progressWebSockets</c> remarks): this frame shape has no type discriminator, so sharing a socket risks a <see cref="RoomProjection"/> deserialization corrupting on it.</summary>
    private async Task StartProgressWebSocketListenerAsync(string resolvedUrl, string? token, CancellationToken cancellationToken)
    {
        _progressWsCts?.Cancel();
        _progressWsCts = new CancellationTokenSource();
        _progressWebSocket = new ClientWebSocket();
        var wsUrl = resolvedUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/api/ws/progress";
        if (token != null)
        {
            wsUrl += $"?token={token}";
        }
        try
        {
            await _progressWebSocket.ConnectAsync(new Uri(wsUrl), cancellationToken).ConfigureAwait(true);
            _ = Task.Run(() => ReceiveProgressWebSocketDataAsync(_progressWebSocket, _progressWsCts.Token), _progressWsCts.Token);
        }
        catch
        {
            // WS connect failed
        }
    }

    private async Task ReceiveProgressWebSocketDataAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 64];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(true);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                ms.Position = 0;
                var frame = await JsonSerializer.DeserializeAsync<ProgressFrame>(ms, DefaultJsonOptions, cancellationToken).ConfigureAwait(true);
                if (frame == null)
                {
                    continue;
                }

                var progressEvent = new WorkerProgressEvent(frame.Kind, frame.Text, frame.IsPartial);
                if (_syncContext != null)
                {
                    _syncContext.Post(_ => SessionProgressReceived?.Invoke(frame.DirectoryPath, frame.StepId, progressEvent), null);
                }
                else
                {
                    SessionProgressReceived?.Invoke(frame.DirectoryPath, frame.StepId, progressEvent);
                }
            }
        }
        catch
        {
            // WS disconnected
        }
    }

    // This WS receive loop is transport, but it mutates *client* state — it calls
    // ShouldApplyProjectionPush / UpdateProjection (in RoomClient.cs) which seed
    // CurrentRoomDirectoryPath and the live projection.
    //
    // #336 retired the assumption this comment used to record ("a client watches one session at a
    // time, so filtering on arrival is correct"). The switcher shows every session at once, so a
    // frame now has *two* consumers: the list, which wants all of them, and the detail pane, which
    // still wants only the one being viewed. That is deliberately expressed as two consumers of one
    // frame rather than as a loosened filter — ShouldApplyProjectionPush's directory equality is
    // what fixed #262 (one client opening a different room corrupting every other client's view,
    // mislabeled under whatever directory the victim had open), and widening it would resurrect
    // exactly that. Routing pushes at the server so a client is not sent what it will discard is
    // #446; this filter stays afterwards as defence in depth — a subscription bug should cost
    // traffic, not correctness.
    private async Task ReceiveWebSocketDataAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(true);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Position = 0;
                    var frame = await JsonSerializer.DeserializeAsync<ProjectionFrame>(ms, DefaultJsonOptions, cancellationToken).ConfigureAwait(true);

                    if (frame != null)
                    {
                        var projection = ToProjection(frame);

                        // Consumer 1 (#336): every frame, whichever directory it is for. The switcher's
                        // list shows every session at once, so a push for a session this client is not
                        // currently *viewing* is exactly the push its row needs. A frame with no
                        // directory at all cannot be attributed to a row, so it is not offered to the
                        // list — the detail pane's filter below handles that case on its own terms
                        // (a null seeds nothing and matches only a client that has opened nothing).
                        if (frame.DirectoryPath is { } pushedDirectoryPath)
                        {
                            RaiseFleetProjectionReceived(pushedDirectoryPath, projection);
                        }

                        // Consumer 2 (unchanged, #262): the detail pane still takes only pushes for the
                        // directory this client is actually viewing.
                        if (ShouldApplyProjectionPush(frame.DirectoryPath))
                        {
                            if (_syncContext != null)
                            {
                                _syncContext.Post(_ =>
                                {
                                    UpdateProjection(projection);
                                    if (_onProjectionUpdated != null && CurrentRoomDirectoryPath != null)
                                    {
                                        _onProjectionUpdated(projection, CurrentRoomDirectoryPath);
                                    }
                                }, null);
                            }
                            else
                            {
                                UpdateProjection(projection);
                                if (_onProjectionUpdated != null && CurrentRoomDirectoryPath != null)
                                {
                                    _onProjectionUpdated(projection, CurrentRoomDirectoryPath);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // WS disconnected
        }
    }

    public async Task ShutdownDaemonAsync()
    {
        if (_activeDaemonUrl != null)
        {
            try
            {
                await _httpClient.PostAsync($"{_activeDaemonUrl}/api/daemon/shutdown", null).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
        }
    }
}
