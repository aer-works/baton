using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Ui.Core;

namespace Aer.Daemon;

/// <summary>
/// The daemon's live projection/progress fan-out over its connected WebSocket clients. Owns the two
/// client bags and every send path — lifted verbatim from <c>Program.cs</c>'s <c>RunDaemonAsync</c>
/// closure (#425) so it is one addressable unit instead of scattered local functions.
///
/// This is the daemon-side seam #335 makes per-room: today every connected socket receives every
/// room's projection (<see cref="BroadcastStateAsync"/> fans out to <em>all</em> of
/// <c>_webSockets</c>). When #335 keys rooms per instance, the routing of which socket feeds which
/// room's stream lands here, rather than being spread across the endpoints that call it.
/// </summary>
internal sealed class DaemonBroadcast
{
    // Active WebSocket connections
    private readonly System.Collections.Concurrent.ConcurrentBag<WebSocket> _webSockets = new();

    // M24 Phase 1's live in-turn streaming: a deliberately separate socket/bag from
    // `_webSockets` above, not an overload of the existing `/api/ws` protocol. That endpoint's
    // frames are bare RoomProjection JSON with a couple of sibling properties bolted on
    // (DirectoryPath, WorkerAdapters) — every existing client deserializes each incoming
    // frame straight into RoomProjection with no type discriminator at all. Sending a
    // differently-shaped progress frame down that same socket risks corrupting an existing
    // client's projection state on a frame it doesn't recognize; a dedicated endpoint carries
    // zero compatibility risk for clients that never opt into it.
    private readonly System.Collections.Concurrent.ConcurrentBag<WebSocket> _progressWebSockets = new();

    /// <summary>Registers a client on the projection stream (<c>/api/ws</c>).</summary>
    public void AddClient(WebSocket socket) => _webSockets.Add(socket);

    /// <summary>Registers a client on the live in-turn progress stream (<c>/api/ws/progress</c>).</summary>
    public void AddProgressClient(WebSocket socket) => _progressWebSockets.Add(socket);

    public async Task BroadcastSessionProgressAsync(string directoryPath, string stepId, WorkerProgressEvent progressEvent)
    {
        var activeSockets = _progressWebSockets.Where(s => s.State == WebSocketState.Open).ToList();
        if (activeSockets.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            DirectoryPath = directoryPath,
            StepId = stepId,
            progressEvent.Kind,
            progressEvent.Text,
            progressEvent.IsPartial,
        });

        foreach (var socket in activeSockets)
        {
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // Ignore single socket failures — same tolerance as BroadcastStateAsync below.
            }
        }
    }

    // Helper method for sending state to a single socket. DirectoryPath (M21 Phase 2, #232)
    // is added as a sibling property rather than a RoomProjection field: the desktop client
    // deserializes this same payload straight into RoomProjection and silently ignores
    // unmapped members, so this is additive and can't break it. Aer.Mobile needs it because
    // /api/rooms/decide and /api/rooms/cancel take an explicit directoryPath — with no way
    // to derive it from the projection itself, a client that only ever observes the WS
    // stream (never having called /api/rooms/open itself) would have no directory to send
    // decisions against.
    public async Task SendStateAsync(WebSocket socket, RoomProjection projection, string? directoryPath)
    {
        var options = DaemonSerializerOptions.WebSocket;
        var node = JsonSerializer.SerializeToNode(projection, options)!.AsObject();
        node["DirectoryPath"] = directoryPath;

        if (!string.IsNullOrEmpty(directoryPath))
        {
            // M24 mobile chat UI follow-up (issue #262): lets a client that only observes
            // this push (never having called /api/sessions/start itself — e.g. a phone
            // whose _openDirectoryPath was seeded from another client's push, or picked
            // from /api/rooms/recent) learn this directory is an interactive session and
            // which SessionId to fetch turns for, without a GET /api/sessions list-scan on
            // every push. Same additive-sibling pattern as DirectoryPath/WorkerAdapters
            // above; still not part of RoomProjection itself.
            var sessionMetadataPath = Path.Combine(directoryPath, ".aer", AerPaths.RoomMetadataFileName);
            if (File.Exists(sessionMetadataPath))
            {
                try
                {
                    var sessionMetadata = await InteractiveSessionMaterializer.LoadMetadataAsync(sessionMetadataPath).ConfigureAwait(true);
                    if (sessionMetadata != null)
                    {
                        node["SessionId"] = sessionMetadata.SessionId;
                    }
                }
                catch { }
            }

            var bindingsPath = AerPaths.RoomBindingsFile(directoryPath);
            if (!File.Exists(bindingsPath))
            {
                var metaPath = Path.Combine(directoryPath, ".aer", "bindings-path");
                if (File.Exists(metaPath))
                {
                    try { bindingsPath = File.ReadAllText(metaPath).Trim(); } catch { }
                }
            }
            if (File.Exists(bindingsPath))
            {
                try
                {
                    var json = File.ReadAllText(bindingsPath);
                    using var doc = JsonDocument.Parse(json);
                    var adaptersNode = new System.Text.Json.Nodes.JsonObject();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.TryGetProperty("Adapter", out var adapterProp) || prop.Value.TryGetProperty("adapter", out adapterProp))
                        {
                            if (adapterProp.GetString() is { } adapterStr)
                            {
                                adaptersNode[prop.Name] = adapterStr;
                            }
                        }
                    }
                    node["WorkerAdapters"] = adaptersNode;
                }
                catch { }
            }
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(options));
        if (socket.State == WebSocketState.Open)
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    // Helper method for broadcasting state to all sockets
    public async Task BroadcastStateAsync(RoomProjection projection, string? directoryPath)
    {
        var activeSockets = _webSockets.Where(s => s.State == WebSocketState.Open).ToList();
        foreach (var socket in activeSockets)
        {
            try
            {
                await SendStateAsync(socket, projection, directoryPath);
            }
            catch
            {
                // Ignore single socket failures
            }
        }
    }
}
