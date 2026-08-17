using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Flow.Concurrency;
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
    /// <summary>
    /// The room-card status for <paramref name="directoryPath"/>, or null when there is no directory
    /// to probe (#1240).
    /// </summary>
    /// <remarks>
    /// Why a remote client cannot answer this for itself, and why it is therefore sent rather than
    /// re-derived there, is recorded in <c>docs/design/02-screens.md</c>'s 2026-08-15 amendment. What
    /// this method adds is only that it goes through <see cref="RoomCardViewModel.DeriveStatus"/> —
    /// the one derivation every surface reads (#616/#976), never a second copy.
    /// <para>
    /// Null when the directory is unknown, and the sibling is then omitted rather than defaulted.
    /// <c>DeriveStatus</c>'s <c>isFlowLockHeld</c> is deliberately not defaultable for the same
    /// reason: a caller that cannot answer must not invent one. An absent sibling means "unknown",
    /// which a client renders as no card — never as Finished.
    /// </para>
    /// </remarks>
    internal static (string StatusText, RoomCardStatus Status)? DeriveRoomCardStatus(
        RoomProjection projection, string? directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return null;
        }

        return RoomCardViewModel.DeriveStatus(
            projection, projection.PendingPermission, ConcurrencyGuard.IsHeld(directoryPath),
            ConcurrencySlotGate.IsWaiting(directoryPath));
    }

    /// <param name="derivedStatus">
    /// #1240: taken ONCE per broadcast by <see cref="BroadcastStateAsync"/> and shared across its
    /// sockets, so two clients in one fan-out cannot receive two different answers about the same
    /// room — the "two probes a few statements apart" defect <c>RoomClient.RefreshRoomStoppedCard</c>
    /// records fixing on the desktop. Null means "not taken yet"; this method then takes its own
    /// single reading, which is what the one direct caller (the connect-time state send) wants.
    /// </param>
    public async Task SendStateAsync(
        WebSocket socket,
        RoomProjection projection,
        string? directoryPath,
        (string StatusText, RoomCardStatus Status)? derivedStatus = null)
    {
        var options = DaemonSerializerOptions.WebSocket;
        var node = JsonSerializer.SerializeToNode(projection, options)!.AsObject();
        node["DirectoryPath"] = directoryPath;

        if (!string.IsNullOrEmpty(directoryPath))
        {
            // Both halves, mirroring RoomFleetItem's contract: the text register carries wording no
            // client can reconstruct without copying FormatExhaustedRoomStatus ("Out of plan —
            // resumes …") into its own language, and the enum is what a client switches on. Sending
            // only the enum would guarantee the next status line on a client re-derives the words.
            var derived = derivedStatus ?? DeriveRoomCardStatus(projection, directoryPath);
            if (derived is { } status)
            {
                node["RoomCardStatus"] = status.Status.ToString();
                node["RoomCardStatusText"] = status.StatusText;
            }

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

                        // 0054 §7/#1307 ruling 7: the same additive-sibling pattern as SessionId
                        // above -- RoomProjection.Participants is the wire twin of this same
                        // SessionMetadata field, so a live push carries it without a second read
                        // model. Null on a pre-#1305 room stays absent rather than an empty array,
                        // matching every other omitted-vs-empty distinction this method already draws.
                        if (sessionMetadata.Participants != null)
                        {
                            node["Participants"] = JsonSerializer.SerializeToNode(sessionMetadata.Participants, options);
                        }
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
                    node["WorkerEffortTiers"] = BuildWorkerEffortTiers(doc.RootElement);
                }
                catch { }
            }

            // #1318 (decision 0058's scope ruling): the depth (model-tier) sibling to
            // WorkerEffortTiers above, staying null until #1330 registers the vendor-model->tier
            // mapping nothing produces yet. An additive sibling, not a RoomProjection field, mirroring
            // WorkerAdapters/WorkerEffortTiers exactly -- a client reads its absence the same way it
            // already reads an absent DirectoryPath: no tier, not a default one.
            node["WorkerDepthTiers"] = null;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(options));
        if (socket.State == WebSocketState.Open)
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    /// <summary>
    /// The additive <c>WorkerEffortTiers</c> sibling (#1318, decision 0058's scope ruling 4): worker
    /// name -> canonical effort word, read from the same bindings file <c>WorkerAdapters</c> reads
    /// above, for exactly the entries whose own <c>Effort</c> is one of 0023's four canonical words.
    /// A binding still holding a raw vendor value (or none) is simply absent from this object, never
    /// defaulted or reverse-mapped -- a raw->canonical reverse map is provably lossy (agy <c>high</c>
    /// is the image of both <c>careful</c> and <c>exhaustive</c>) and the ruling forbids building one.
    /// A client that reads no entry for a worker renders no mark, matching #1312's nullable-omission
    /// precedent (the phone chip omitting absent model text) rather than inventing a value.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject BuildWorkerEffortTiers(JsonElement bindingsRoot)
    {
        var effortTiers = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in bindingsRoot.EnumerateObject())
        {
            if ((prop.Value.TryGetProperty("Effort", out var effortProp) || prop.Value.TryGetProperty("effort", out effortProp))
                && effortProp.GetString() is { } effortStr
                && EffortTierMapping.IsCanonical(effortStr))
            {
                effortTiers[prop.Name] = effortStr;
            }
        }

        return effortTiers;
    }

    // Helper method for broadcasting state to all sockets
    public async Task BroadcastStateAsync(RoomProjection projection, string? directoryPath)
    {
        var activeSockets = _webSockets.Where(s => s.State == WebSocketState.Open).ToList();
        // #1240's second reader: return before the probe below, not after it. DeriveRoomCardStatus
        // opens and closes the room's lock file — a blocking syscall on an async path — and an
        // unattended `aer run` broadcasts on every step transition with nobody connected, so without
        // this the probe is paid per transition for a reading no one receives. The sibling method
        // above already short-circuits the same way for the same reason.
        if (activeSockets.Count == 0)
        {
            return;
        }

        // One reading, shared by every socket in this fan-out — see SendStateAsync's derivedStatus.
        var derivedStatus = DeriveRoomCardStatus(projection, directoryPath);
        foreach (var socket in activeSockets)
        {
            try
            {
                await SendStateAsync(socket, projection, directoryPath, derivedStatus);
            }
            catch
            {
                // Ignore single socket failures
            }
        }
    }
}
