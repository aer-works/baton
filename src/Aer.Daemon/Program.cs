using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow.Artifacts;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Ui.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

[assembly: InternalsVisibleTo("Aer.Ui.Tests")]
[assembly: InternalsVisibleTo("Aer.Daemon.Tests")]

await Aer.Daemon.DaemonHost.RunDaemonAsync(args);

namespace Aer.Daemon
{
    public static class DaemonHost
    {
        // M21 Phase 2 (#232): see the /api/rooms/artifact handler below for why this is larger
        // than HomeViewModel's 400-char inbox snippet.
        private const int ArtifactPreviewMaxLength = 50_000;

        public static WebApplication? App { get; set; }

        public static async Task RunDaemonAsync(string[] args, IReadOnlyDictionary<string, IWorkerAdapter>? adapters = null, Action<WebApplication>? onBuilt = null)
        {
            var noMutex = args.Contains("--no-mutex");
            Mutex? mutex = null;
            if (!noMutex)
            {
                var username = Environment.UserName;
                mutex = new Mutex(true, $"Global\\AerDaemonMutex_{username}", out var createdNew);
                if (!createdNew)
                {
                    Console.WriteLine("Another instance of Aer.Daemon is already running.");
                    mutex.Dispose();
                    return;
                }
            }

            // Setup local data directory ~/.aer
            var aerDir = AerPaths.Root;
            Directory.CreateDirectory(aerDir);

            // Generate token if not exists
            var tokenFile = Path.Combine(aerDir, "daemon.token");
            string token;
            if (File.Exists(tokenFile))
            {
                token = (await File.ReadAllTextAsync(tokenFile)).Trim();
            }
            else
            {
                token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(tokenFile, token);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            var builder = WebApplication.CreateBuilder(args);

            // Default graceful-shutdown budget is 30s — found live: even after tying the /api/ws
            // receive loop's ReceiveAsync to context.RequestAborted (so it CAN unblock promptly on
            // shutdown), a real shutdown-then-respawn toggle still took over 20s end to end. Rather
            // than keep chasing which specific connection/timer is still cooperative-cancellation-shy,
            // bound the worst case directly: after this, Kestrel force-aborts anything still open.
            builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

            var isRemote = args.Contains("--remote");

            // Ensure daemon listens on a fixed port (allow override via --port).
            var portIndex = Array.IndexOf(args, "--port");
            var port = (portIndex >= 0 && portIndex < args.Length - 1) ? int.Parse(args[portIndex + 1]) : 5000;

            var activePort = port;
            // Probe the port before Kestrel binds it so a busy default port degrades predictably
            // instead of crashing. Skip when an ephemeral port (0) was explicitly requested (#296's
            // test fixtures). Probe the exact interface Kestrel will bind -- IPAddress.Any under
            // --remote, loopback locally: probing loopback while --remote binds Any would pass here
            // and then let Kestrel crash on the real conflict (#347).
            if (port != 0 && (isRemote || portIndex < 0))
            {
                var probeAddress = isRemote ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
                try
                {
                    using var listener = new System.Net.Sockets.TcpListener(probeAddress, port);
                    listener.Start();
                    listener.Stop();
                }
                catch (System.Net.Sockets.SocketException)
                {
                    if (isRemote)
                    {
                        // A remote daemon's address (host:port) is baked into every paired phone at
                        // pairing time (#384), so it MUST stay stable across restarts. Falling back to
                        // an ephemeral port -- the local-dev behavior below -- would leave every paired
                        // phone dialing a dead port (#347). The single-daemon mutex above already rules
                        // out a rival Aer.Daemon, so a busy port here is an unrelated process: surface
                        // it and stop, rather than coming up reachable-but-wrong on a random port.
                        Console.Error.WriteLine(
                            $"Aer.Daemon (--remote) cannot bind port {port}: it is already in use by " +
                            "another process. A remote daemon's address is baked into paired phones, so " +
                            "it will not fall back to a random port. Free the port, or start with an " +
                            "explicit stable --port <n> that your phones are paired to.");
                        Environment.Exit(1);
                    }

                    // Local dev only: the desktop finds the daemon via ~/.aer/daemon.port, so a
                    // dynamic fallback is safe. Port is in use -- fall back to 0 (OS-assigned).
                    activePort = 0;
                }
            }

            builder.WebHost.ConfigureKestrel(options =>
            {
                if (isRemote)
                {
                    options.Listen(System.Net.IPAddress.Any, activePort);
                }
                else if (activePort == 0)
                {
                    // ListenLocalhost(0) throws InvalidOperationException ("Dynamic port binding is
                    // not supported when binding to localhost") -- it binds both the IPv4 and IPv6
                    // loopback interfaces, and a truly dynamic port can't be guaranteed identical on
                    // both (each bind(0) gets its own independently OS-assigned ephemeral port), so
                    // Kestrel refuses outright rather than silently pick one. This path is reachable
                    // both from an explicit `--port 0` (issue #296's test fixtures, so two daemon
                    // instances in concurrent test runs never fight over the same fixed port) and
                    // from the port-collision fallback just above (`activePort = 0` when the
                    // default/requested fixed port is already taken) -- loopback-only (IPv4) keeps
                    // that fallback actually usable instead of trading one crash for another.
                    options.Listen(System.Net.IPAddress.Loopback, 0);
                }
                else
                {
                    options.ListenLocalhost(activePort);
                }
            });

            // Configure JSON options — the same definition the wire fixtures are generated from
            builder.Services.ConfigureHttpJsonOptions(options =>
                DaemonSerializerOptions.Configure(options.SerializerOptions));

            // Register singletons
            builder.Services.AddSingleton(LocalUiConfigurationStore.CreateDefault());
            builder.Services.AddSingleton(adapters ?? WorkerAdapterRegistry.Default);
            builder.Services.AddSingleton<MainWindowViewModel>();
            builder.Services.AddSingleton<PairedClientsStore>();

            // #799: the room wake-bridge — pure derivation of the wake set plus a read-only workflow
            // probe, no persisted queue. RoomWakeBridgeState is the thin settable pointer to which
            // room directory to watch; the hosted RoomWakeBridge recomputes the wake set on it.
            builder.Services.AddSingleton<RoomWakeBridgeState>();
            builder.Services.AddHostedService<RoomWakeBridge>();

            // #992 / #993: resident room turn host & occupant runner
            builder.Services.AddSingleton<RoomTurnHostState>();
            builder.Services.AddSingleton<IOccupantTurnRunner, RoleTemplateOccupantRunner>();
            builder.Services.AddHostedService<RoomTurnHost>();

            // #1025: room retention sweep (journal compaction)
            builder.Services.AddHostedService<RoomRetentionSweep>();

            // Thread-safe container for bindings path
            var bindingsPathHolder = new BindingsPathHolder();
            builder.Services.AddSingleton(bindingsPathHolder);

            // The daemon's live projection/progress fan-out over its connected WebSocket
            // clients, extracted to its own type (#425) — the daemon-side seam #335 makes
            // per-room (today it broadcasts every room's projection to every socket).
            var broadcast = new DaemonBroadcast();

            // Register RoomClient
            builder.Services.AddSingleton(sp =>
            {
                var configStore = sp.GetRequiredService<LocalUiConfigurationStore>();
                var adapters = sp.GetRequiredService<IReadOnlyDictionary<string, IWorkerAdapter>>();
                var viewModel = sp.GetRequiredService<MainWindowViewModel>();
                var pathHolder = sp.GetRequiredService<BindingsPathHolder>();

                RoomClient? session = null;

                Func<string, CancellationToken, Task> reopenRoomAsync = async (roomDirectoryPath, cancellationToken) =>
                {
                    if (session != null)
                    {
                        var outcome = await session.LoadAsync(roomDirectoryPath, cancellationToken);
                        if (outcome.Projection != null)
                        {
                            await broadcast.BroadcastStateAsync(outcome.Projection, roomDirectoryPath);
                        }
                    }
                };

                session = new RoomClient(
                    configStore,
                    adapters,
                    viewModel,
                    bindingsFilePathProvider: () => pathHolder.BindingsFilePath,
                    mutationStarted: () => { },
                    mutationFailed: () => { },
                    reopenRoomAsync: reopenRoomAsync
                );

                return session;
            });

            var app = builder.Build();
            App = app;
            onBuilt?.Invoke(app);

            _ = Task.Run(async () =>
            {
                try
                {
                    // #1171: reconcile pushes each mutated room's refreshed projection, so a client
                    // connected across the restart renders a re-presented (or expiring) gate
                    // without waiting for an unrelated event to broadcast.
                    await ReconcilePendingPermissionsAsync(
                        broadcastStateAsync: async (proj, dir) =>
                            await broadcast.BroadcastStateAsync(proj, dir).ConfigureAwait(false)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Crash reconciliation failed: {ex.Message}");
                }
            });

            bool SafeEquals(string a, string b)
            {
                if (a.Length != b.Length) return false;
                var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
                var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
            }

            // M21 Phase 5 (#242): the Go tsnet sidecar, spawned only in --remote mode. This is
            // additive on top of the existing plain-LAN Kestrel bind above, not a replacement for
            // it -- the tsnet path is not yet proven live (no cross-network run has exercised it,
            // see docs/runbooks/tailscale-cross-network-proof.md), so Kestrel keeps listening on
            // IPAddress.Any exactly as it did before. Retiring that proven path in favor of an
            // unproven one in the same change would be a regression, not a hardening step -- Phase
            // 6's loopback-only rebind is deliberately deferred until the sidecar path has an
            // actual recorded green run, same convention CLAUDE.md's "Live-vendor smoke tests"
            // section already applies to vendor-CLI gates.
            Process? sidecarProcess = null;
            int? sidecarStatusPort = null;
            string? sidecarUnavailableReason = null;
            var sidecarStatusPortFile = Path.Combine(aerDir, "sidecar-status.port");
            var sidecarHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            void TryAppendSidecarLog(string path, string line)
            {
                try { File.AppendAllText(path, line + Environment.NewLine); } catch { /* best-effort */ }
            }

            void TryStartSidecar(int kestrelPort)
            {
                var sidecarExeName = OperatingSystem.IsWindows() ? "aer-sidecar.exe" : "aer-sidecar";
                var sidecarPath = Path.Combine(AppContext.BaseDirectory, sidecarExeName);
                if (!File.Exists(sidecarPath))
                {
                    // Permanent, not "starting" -- without this, /api/remote/sidecar-status would
                    // say "starting" forever instead of telling the UI (and whoever's staring at
                    // it) that zero-config needs `pixi run build-sidecar` first.
                    sidecarUnavailableReason = "aer-sidecar isn't built -- run `pixi run build-sidecar` (requires a Go toolchain), then restart remote access. Falling back to plain LAN.";
                    Console.WriteLine($"aer-sidecar not found at {sidecarPath} -- --remote falls back to plain LAN only.");
                    return;
                }

                try { if (File.Exists(sidecarStatusPortFile)) File.Delete(sidecarStatusPortFile); } catch { /* best-effort */ }

                var stateDir = Path.Combine(aerDir, "sidecar-tsnet");
                Directory.CreateDirectory(stateDir);

                var args = $"--kestrel-port {kestrelPort} --status-port-file \"{sidecarStatusPortFile}\" --state-dir \"{stateDir}\" --hostname aer-{Environment.MachineName}";

                var startInfo = new ProcessStartInfo
                {
                    FileName = sidecarPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };

                try
                {
                    sidecarProcess = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    sidecarUnavailableReason = $"aer-sidecar failed to start: {ex.Message}";
                    Console.WriteLine(sidecarUnavailableReason);
                    return;
                }

                if (sidecarProcess == null) return;

                var logPath = Path.Combine(aerDir, "sidecar-spawn.log");
                try
                {
                    File.WriteAllText(logPath, $"--- spawn {DateTime.UtcNow:O} (args: {args}) ---{Environment.NewLine}");
                    sidecarProcess.OutputDataReceived += (_, e) => { if (e.Data != null) TryAppendSidecarLog(logPath, e.Data); };
                    sidecarProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) TryAppendSidecarLog(logPath, e.Data); };
                    sidecarProcess.BeginOutputReadLine();
                    sidecarProcess.BeginErrorReadLine();
                }
                catch { /* diagnostics only, never block a real spawn attempt */ }

                var startedProcess = sidecarProcess;
                _ = Task.Run(async () =>
                {
                    // Status port is OS-assigned, so it's only known once the sidecar writes it --
                    // same file-handoff convention as this daemon's own daemon.port. Not gated on
                    // tsnet's Up() completing (that can block indefinitely on first-run interactive
                    // auth): a sidecar that's alive and answering /status, but not yet Ready, still
                    // has to surface its AuthURL somewhere -- see sidecar-spawn.log.
                    for (var i = 0; i < 30; i++)
                    {
                        if (startedProcess.HasExited) return;
                        try
                        {
                            if (File.Exists(sidecarStatusPortFile))
                            {
                                var text = (await File.ReadAllTextAsync(sidecarStatusPortFile)).Trim();
                                if (int.TryParse(text, out var p))
                                {
                                    sidecarStatusPort = p;
                                    return;
                                }
                            }
                        }
                        catch { /* keep retrying */ }
                        await Task.Delay(200);
                    }
                }, CancellationToken.None);
            }

            // Must run before the auth middleware below: context.WebSockets.IsWebSocketRequest is
            // populated by this middleware (it wires up IHttpWebSocketFeature), not by Kestrel
            // directly. Registered afterward, it silently evaluated false for every WS handshake,
            // so the auth check fell through to the plain-Authorization-header branch — which the
            // WS client never sets (only ever the ?token= query string) — and every WS connection
            // was rejected with 401. Masked until now by a bare catch{} around the client's
            // connect call (RoomClient.StartWebSocketListenerAsync); found while building
            // Aer.Mobile (M21 Phase 2, #232), whose decision inbox depends entirely on this stream.
            app.UseWebSockets();

            // Authentication Middleware verifying the Bearer token
            app.Use(async (context, next) =>
            {
                // Allow public access to version endpoint and pairing pairing endpoint
                if ((context.Request.Path == "/api/version" && context.Request.Method == "GET") ||
                    (context.Request.Path == "/api/pairing/pair" && context.Request.Method == "POST"))
                {
                    await next(context);
                    return;
                }

                string requestToken = "";
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var queryToken = context.Request.Query["token"].ToString().Trim();
                    var headerToken = context.Request.Headers["Authorization"].ToString().Replace("Bearer ", "").Trim();
                    requestToken = !string.IsNullOrEmpty(queryToken) ? queryToken : headerToken;
                }
                else
                {
                    var authHeader = context.Request.Headers["Authorization"].ToString();
                    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        requestToken = authHeader.Substring("Bearer ".Length).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(requestToken))
                {
                    // 1. Verify against local loopback token
                    if (SafeEquals(requestToken, token))
                    {
                        await next(context);
                        return;
                    }

                    // 2. Verify against paired clients
                    var store = context.RequestServices.GetRequiredService<PairedClientsStore>();
                    if (store.ValidateToken(requestToken))
                    {
                        await next(context);
                        return;
                    }
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
            });

            // WebSocket endpoint
            app.Map("/api/ws", async (HttpContext context, RoomClient session) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    broadcast.AddClient(webSocket);

                    // Send current projection immediately if loaded
                    if (session.CurrentRoomDirectoryPath != null && session.LastLoadSucceeded)
                    {
                        var outcome = await session.LoadAsync(session.CurrentRoomDirectoryPath);
                        if (outcome.Projection != null)
                        {
                            await broadcast.SendStateAsync(webSocket, outcome.Projection, session.CurrentRoomDirectoryPath);
                        }
                    }

                    // Keep connection open
                    var buffer = new byte[1024 * 4];
                    try
                    {
                        while (webSocket.State == WebSocketState.Open)
                        {
                            // CancellationToken.None here meant this loop had no way to unblock on
                            // app shutdown — found live: SetRemoteEnabledAsync's shutdown-then-respawn
                            // toggle stalled for the full ~30s default graceful-shutdown grace period
                            // (HostOptions.ShutdownTimeout) before the host force-aborted this stuck
                            // connection, since the receive loop itself never observed shutdown at
                            // all. context.RequestAborted is signaled promptly on app shutdown as well
                            // as client disconnect, so this now unblocks immediately either way.
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore socket disconnect errors
                    }
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                }
            });

            // M24 Phase 1's live in-turn streaming WebSocket endpoint — see DaemonBroadcast's
            // remarks for why this is separate from /api/ws rather than sharing it.
            app.Map("/api/ws/progress", async (HttpContext context) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    broadcast.AddProgressClient(webSocket);

                    var buffer = new byte[1024 * 4];
                    try
                    {
                        while (webSocket.State == WebSocketState.Open)
                        {
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore socket disconnect errors
                    }
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                }
            });

            // Version metadata endpoint
            app.MapGet("/api/version", (RoomClient session) => Results.Ok(new
            {
                Version = typeof(DaemonHost).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                HasRunningRooms = session.ShouldLiveRefresh,
                IsRemote = isRemote
            }));

            // Graceful shutdown endpoint
            app.MapPost("/api/daemon/shutdown", (IHostApplicationLifetime lifetime) =>
            {
                lifetime.StopApplication();
                return Results.Ok("Shutting down...");
            });

            // Generates a 6-digit pairing code (only callable if authorized, typically by local UI)
            app.MapGet("/api/pairing/code", () =>
            {
                var code = PairingCodeManager.GenerateCode();
                return Results.Ok(new { Code = code, ExpiresInSeconds = 60 });
            });

            // Exposes pairing verification (public endpoint)
            app.MapPost("/api/pairing/pair", ([FromBody] PairRequest request, PairedClientsStore store) =>
            {
                if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.ClientName))
                {
                    return Results.BadRequest("Code and ClientName are required.");
                }

                if (PairingCodeManager.ValidateAndConsume(request.Code))
                {
                    var token = store.AddClient(request.ClientName);
                    return Results.Ok(new { Token = token });
                }

                return Results.Json(new { Error = "Invalid or expired pairing code." }, statusCode: StatusCodes.Status400BadRequest);
            });

            // Paired-device management (Phase 6, #243): revocation is a desktop-owner action, not
            // something a paired mobile client should be able to do to itself or to siblings — gated
            // to the local loopback token specifically, unlike most endpoints below which accept
            // either the local token or any paired client's token.
            bool IsLocalToken(HttpContext context)
            {
                var authHeader = context.Request.Headers["Authorization"].ToString();
                if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
                return SafeEquals(authHeader["Bearer ".Length..].Trim(), token);
            }

            app.MapGet("/api/pairing/clients", (HttpContext context, PairedClientsStore store) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can list paired devices." }, statusCode: StatusCodes.Status403Forbidden);
                }

                var clients = store.ListClients()
                    .Select(c => new { c.ClientId, c.Name, c.PairedAt })
                    .ToList();
                return Results.Ok(clients);
            });

            app.MapDelete("/api/pairing/clients/{clientId}", (string clientId, HttpContext context, PairedClientsStore store) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can revoke paired devices." }, statusCode: StatusCodes.Status403Forbidden);
                }

                return store.RemoveClient(clientId) ? Results.Ok() : Results.NotFound();
            });

            // Sidecar readiness/auth-URL surfacing (#242): loopback-owner-only, same gating as the
            // paired-clients endpoints above -- this is desktop setup state, not something a paired
            // mobile client needs. Proxies the sidecar's own /status rather than caching it, so it's
            // never stale relative to what the sidecar actually knows right now.
            app.MapGet("/api/remote/sidecar-status", async (HttpContext context) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can view sidecar status." }, statusCode: StatusCodes.Status403Forbidden);
                }

                if (!isRemote)
                {
                    return Results.Ok(new { Ready = false, Error = "Remote access is off." });
                }

                if (sidecarUnavailableReason is { } reason)
                {
                    return Results.Ok(new { Ready = false, Error = reason });
                }

                if (sidecarStatusPort is not { } port)
                {
                    // No Error here -- absence of AuthUrl/Error/Ready is itself "still starting" to
                    // the client (RemoteViewModel.CurrentSidecarPhase's fallback case), not a
                    // distinct sentinel string to keep in sync between the two ends.
                    return Results.Ok(new { Ready = false });
                }

                try
                {
                    var response = await sidecarHttpClient.GetAsync($"http://127.0.0.1:{port}/status");
                    var body = await response.Content.ReadAsStringAsync();
                    return Results.Content(body, "application/json");
                }
                catch (Exception ex)
                {
                    return Results.Ok(new { Ready = false, Error = $"sidecar unreachable: {ex.Message}" });
                }
            });

            // Sidecar sign-out (#242 follow-up): the only way to disconnect the tsnet node used to
            // be deleting it from the Tailscale admin console and restarting Aer.Ui -- this proxies
            // the sidecar's own /forget, which logs the node out and immediately re-enters the
            // interactive-login flow (a fresh AuthUrl shows up on the next sidecar-status poll).
            app.MapPost("/api/remote/sidecar-forget", async (HttpContext context) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can sign the sidecar out." }, statusCode: StatusCodes.Status403Forbidden);
                }

                if (sidecarStatusPort is not { } port)
                {
                    return Results.Json(new { Error = "Sidecar isn't running." }, statusCode: StatusCodes.Status409Conflict);
                }

                try
                {
                    var response = await sidecarHttpClient.PostAsync($"http://127.0.0.1:{port}/forget", null);
                    return response.IsSuccessStatusCode
                        ? Results.Accepted()
                        : Results.Json(new { Error = $"sidecar rejected forget: {response.StatusCode}" }, statusCode: StatusCodes.Status502BadGateway);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Error = $"sidecar unreachable: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
                }
            });

            // #799: room wake-bridge surface — J19's "event→notification pipeline" leg's first
            // piece. Watch is a thin pointer-set (like pathHolder.BindingsFilePath above); wakes
            // reads back whatever RoomWakeBridge's background loop last derived. Neither endpoint
            // stores a wake itself -- resolving the held-work ref (RoomMutationInterface, not this
            // surface) is what clears one.
            app.MapPost("/api/rooms/watch", ([FromBody] WatchRoomRequest request, RoomWakeBridgeState wakeState) =>
            {
                if (string.IsNullOrWhiteSpace(request.RoomDirectoryPath))
                {
                    return Results.BadRequest("RoomDirectoryPath is required.");
                }

                wakeState.RoomDirectoryPath = request.RoomDirectoryPath;
                return Results.Ok();
            });

            app.MapGet("/api/rooms/wakes", (RoomWakeBridgeState wakeState) => Results.Ok(new
            {
                RoomDirectoryPath = wakeState.RoomDirectoryPath,
                Wakes = wakeState.CurrentWakes.Select(w => new { Ref = w.Ref.Value, Kind = w.Kind.ToString() }).ToList(),
                ProbeFailures = wakeState.CurrentProbeFailures.Select(f => new { Ref = f.Ref.Value, f.Error }).ToList(),
            }));

            // #992: turn host status & clear-dormancy endpoints. Both serve THE one hosted room:
            // RoomTurnHostState's counters (failures, in-flight, the machine-turn ledger) are only
            // meaningful for the room the daemon is ticking, so a request naming any other room is
            // refused rather than answered with silently-blended state (second-reader finding).
            app.MapGet("/api/rooms/turn-host/status", async (string? roomDirectoryPath, RoomTurnHostState hostState) =>
            {
                var targetDir = !string.IsNullOrWhiteSpace(roomDirectoryPath)
                    ? roomDirectoryPath
                    : hostState.RoomDirectoryPath;

                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
                {
                    return Results.BadRequest("roomDirectoryPath is required and must exist.");
                }

                var hostedRoom = hostState.RoomDirectoryPath;
                if (hostedRoom is null || !PathsReferToSameDirectory(targetDir, hostedRoom))
                {
                    return Results.Conflict(
                        $"The turn host is not hosting '{targetDir}' (hosted: '{hostedRoom ?? "none"}'); its counters describe only the hosted room.");
                }

                var (throttles, loadError) = RoomTurnThrottles.Load(targetDir);
                var hasCustomFile = File.Exists(Path.Combine(targetDir, "turn-throttles.json"));

                var roomLogPath = Path.Combine(targetDir, "room.jsonl");
                var isDormant = false;
                string? dormancyEscalationDetail = null;
                if (File.Exists(roomLogPath))
                {
                    var reader = new RoomEventLogReader(roomLogPath);
                    var events = await reader.ReadAllRoomEventsAsync().ConfigureAwait(false);
                    var roomState = RoomProjector.Project(events);
                    isDormant = roomState.IsDormant;
                    if (isDormant)
                    {
                        // #994 acceptance: dormancy is shown WITH the escalation that tripped it.
                        dormancyEscalationDetail = roomState.OpenEscalations
                            .Select(e => e.Subject)
                            .OfType<EscalationSubject.HostCondition>()
                            .LastOrDefault(s => s.Condition == RoomTurnHost.DormancyConditionName)
                            ?.Detail;
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var hourlyWindowStart = now.AddHours(-1);
                int turnsInLastHour = hostState.MachineTurnStarts.Count(t => t >= hourlyWindowStart);

                return Results.Ok(new
                {
                    RoomDirectoryPath = targetDir,
                    Throttles = new
                    {
                        MachineTurnMinimumGapSeconds = throttles.MachineTurnMinimumGap.TotalSeconds,
                        MachineTurnsPerHour = throttles.MachineTurnsPerHour,
                        ConsecutiveFailureLimit = throttles.ConsecutiveFailureLimit,
                    },
                    ThrottlesSource = hasCustomFile ? "file" : "defaults",
                    LoadError = loadError,
                    MachineTurnsInTrailingHour = $"{turnsInLastHour}/{throttles.MachineTurnsPerHour}",
                    TurnsInTrailingHourCount = turnsInLastHour,
                    MachineTurnsPerHourCap = throttles.MachineTurnsPerHour,
                    ConsecutiveFailures = hostState.ConsecutiveFailures,
                    InFlight = hostState.InFlight,
                    IsDormant = isDormant,
                    DormancyEscalationDetail = dormancyEscalationDetail,
                    LastDecisionReason = hostState.LastDecisionReason,
                });
            });

            app.MapPost("/api/rooms/turn-host/clear-dormancy", async ([FromBody] ClearDormancyRequest request, RoomTurnHostState hostState) =>
            {
                if (string.IsNullOrWhiteSpace(request.RoomDirectoryPath))
                {
                    return Results.BadRequest("RoomDirectoryPath is required.");
                }

                if (!Directory.Exists(request.RoomDirectoryPath))
                {
                    return Results.BadRequest($"RoomDirectoryPath '{request.RoomDirectoryPath}' does not exist.");
                }

                var hostedRoomDir = hostState.RoomDirectoryPath;
                if (hostedRoomDir is null || !PathsReferToSameDirectory(request.RoomDirectoryPath, hostedRoomDir))
                {
                    return Results.Conflict(
                        $"The turn host is not hosting '{request.RoomDirectoryPath}' (hosted: '{hostedRoomDir ?? "none"}'); clearing another room's dormancy here would reset the hosted room's breaker.");
                }

                var roomLogPath = Path.Combine(request.RoomDirectoryPath, "room.jsonl");
                var reader = new RoomEventLogReader(roomLogPath);
                await using var writer = new RoomEventLogWriter(roomLogPath);

                var roomEvents = await reader.ReadAllRoomEventsAsync().ConfigureAwait(false);
                var roomState = RoomProjector.Project(roomEvents);

                if (!roomState.IsDormant)
                {
                    return Results.Conflict("Room is not dormant.");
                }

                await RoomMutationInterface.ClearTurnHostDormancyAsync(
                    request.RoomDirectoryPath, clearedBy: "operator", reader, writer)
                    .ConfigureAwait(false);

                hostState.ResetConsecutiveFailures();

                return Results.Ok();
            });

            // #1216: the room header's Workflow ON/OFF switch. A durable room-level fact, so it goes
            // through RoomMutationInterface like every other room.jsonl append rather than being
            // written here -- and that is also where the in-flight refusal lives, so a phone and a
            // desktop cannot disagree about when the switch is available. A refusal comes back as a
            // 409 carrying the engine's own reason, which is what the surface shows the person; it is
            // never flattened into a bare failure, since "why not" is the whole point of refusing
            // rather than silently mutating.
            app.MapPost("/api/rooms/workflow-switch", async ([FromBody] SetWorkflowSwitchRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.RoomDirectoryPath))
                {
                    return Results.BadRequest("RoomDirectoryPath is required.");
                }

                // Same guard as /api/rooms/held-work/resolve below: RoomMutationInterface's own
                // ConcurrencyGuard creates the directory it locks, so a typo'd path would leave a
                // stray directory behind before any "not found" could fire.
                if (!Directory.Exists(request.RoomDirectoryPath))
                {
                    return Results.BadRequest($"RoomDirectoryPath '{request.RoomDirectoryPath}' does not exist.");
                }

                var switchSnapshotPath = Path.Combine(request.RoomDirectoryPath, "snapshot.json");
                if (!File.Exists(switchSnapshotPath))
                {
                    return Results.BadRequest($"'{request.RoomDirectoryPath}' is not a room directory (no snapshot.json).");
                }

                var switchSnapshot = await Aer.Flow.Templates.SnapshotBinder.LoadFromFileAsync(switchSnapshotPath).ConfigureAwait(false);
                var switchFlowReader = new FlowEventLogReader(Path.Combine(request.RoomDirectoryPath, "flow.jsonl"));

                var switchRoomLogPath = Path.Combine(request.RoomDirectoryPath, "room.jsonl");
                var switchReader = new RoomEventLogReader(switchRoomLogPath);
                await using var switchWriter = new RoomEventLogWriter(switchRoomLogPath);

                try
                {
                    await RoomMutationInterface.SetWorkflowSwitchAsync(
                        request.RoomDirectoryPath,
                        request.IsOn,
                        switchedBy: "operator",
                        switchReader,
                        switchWriter,
                        switchFlowReader,
                        switchSnapshot).ConfigureAwait(false);
                }
                catch (InvalidRoomMutationException ex)
                {
                    return Results.Conflict(ex.Message);
                }

                return Results.Ok();
            });

            // #672: the operator's decision surface for held work escalated into a room, and the
            // seam where approving a memory-proposal-shaped item actually applies it (decision
            // 0044 point 3). One endpoint with an Outcome field, mirroring /api/rooms/decide's own
            // shape above, rather than two endpoints -- the daemon's existing decide-style
            // convention, not a new one invented for this workflow. Synchronous (unlike
            // /api/rooms/decide's fire-and-forget dispatch): a resolve is one journal append plus,
            // at most, one small file write -- not a worker turn -- so the operator gets the actual
            // outcome (including a traversal refusal or a loud missing-delete-target failure) back
            // in the response instead of polling turn-errors.log for it.
            app.MapPost("/api/rooms/held-work/resolve", async ([FromBody] ResolveHeldWorkRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.RoomDirectoryPath))
                {
                    return Results.BadRequest("RoomDirectoryPath is required.");
                }

                if (string.IsNullOrWhiteSpace(request.Ref))
                {
                    return Results.BadRequest("Ref is required.");
                }

                // #672 review: RoomMutationInterface's own ConcurrencyGuard.Acquire unconditionally
                // creates the directory it locks, so without this check a typo'd RoomDirectoryPath
                // would still leave a stray directory (with a flow.lock) on disk before the "not
                // found" 400 below ever fires -- matching /api/rooms/open's own invalid-directory
                // guard above.
                if (!Directory.Exists(request.RoomDirectoryPath))
                {
                    return Results.BadRequest($"RoomDirectoryPath '{request.RoomDirectoryPath}' does not exist.");
                }

                bool approve;
                if (string.Equals(request.Outcome, "approve", StringComparison.OrdinalIgnoreCase))
                {
                    approve = true;
                }
                else if (string.Equals(request.Outcome, "reject", StringComparison.OrdinalIgnoreCase))
                {
                    approve = false;
                }
                else
                {
                    return Results.BadRequest("Outcome must be 'approve' or 'reject'.");
                }

                var roomLogPath = Path.Combine(request.RoomDirectoryPath, "room.jsonl");
                var reader = new RoomEventLogReader(roomLogPath);
                await using var writer = new RoomEventLogWriter(roomLogPath);

                try
                {
                    var state = await MemoryProposalResolution.ResolveAsync(
                        request.RoomDirectoryPath, new HeldWorkRef(request.Ref), approve, reader, writer)
                        .ConfigureAwait(false);

                    var resolved = state.HeldWork[new HeldWorkRef(request.Ref)];
                    return Results.Ok(new
                    {
                        Ref = resolved.Ref.Value,
                        Status = resolved.Status.ToString(),
                        Outcome = request.Outcome,
                    });
                }
                catch (InvalidRoomMutationException ex)
                {
                    return Results.BadRequest(ex.Message);
                }
                catch (WorkflowLockedException ex)
                {
                    return Results.Conflict(ex.Message);
                }
            });

            // REST endpoints
            app.MapGet("/api/templates", () =>
            {
                var templates = BuiltInWorkflowTemplates.Catalog;
                var availableVendors = VendorCliPresence.Probe();
                return Results.Ok(new { Templates = templates, AvailableVendors = availableVendors });
            });

            app.MapPost("/api/templates/run", async ([FromBody] RunTemplateRequest request, RoomClient session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrWhiteSpace(request.TemplateId))
                {
                    return Results.BadRequest("TemplateId is required.");
                }

                // #333: new records are created in the one record root, never the legacy split.
                var baseRoomsDir = AerPaths.Rooms;
                var folderName = string.IsNullOrWhiteSpace(request.RoomName)
                    ? $"room-{DateTime.UtcNow:yyyyMMddHHmmss}"
                    : request.RoomName.Trim();
                var roomDirectoryPath = Path.GetFullPath(Path.Combine(baseRoomsDir, folderName));
                var normalizedBaseRoomsDir = Path.GetFullPath(baseRoomsDir);
                if (!roomDirectoryPath.StartsWith(normalizedBaseRoomsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return Results.BadRequest("RoomName must be a simple folder name, not a path.");
                }

                try
                {
                    await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                        request.TemplateId,
                        request.PrimaryAdapter ?? "claude",
                        request.SecondaryAdapter,
                        roomDirectoryPath,
                        request.CustomPrompt,
                        request.SecondaryCustomPrompt).ConfigureAwait(true);

                    var workflowFilePath = Path.Combine(roomDirectoryPath, "workflow.json");
                    var bindingsFilePath = AerPaths.RoomBindingsFile(roomDirectoryPath);

                    pathHolder.BindingsFilePath = bindingsFilePath;
                    session.SetCurrentRoomDirectory(roomDirectoryPath);
                    await session.RecordOpenedAsync(roomDirectoryPath).ConfigureAwait(true);
                    var outcome = await session.LoadAsync(roomDirectoryPath).ConfigureAwait(true);
                    if (outcome.Projection != null)
                    {
                        await broadcast.BroadcastStateAsync(outcome.Projection, roomDirectoryPath).ConfigureAwait(true);
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.RunAsync(roomDirectoryPath, workflowFilePath, bindingsFilePath).ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error running template room in background: {ex}");
                        }
                    });

                    return Results.Ok(new { RoomDirectoryPath = roomDirectoryPath });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(ex.Message);
                }
            });

            app.MapGet("/api/rooms/recent", async (RoomClient session) =>
            {
                var directories = await session.LoadRecentRoomDirectoriesAsync();
                return Results.Ok(directories);
            });

            app.MapPost("/api/rooms/open", async ([FromBody] OpenRoomRequest request, RoomClient session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }

                // #324: a room whose flow.lock is held by another live writer (a running 'aer run'
                // pump, or this daemon's own background run) is readable but not safe to re-point the
                // daemon at -- and the projected read succeeds regardless of the lock, so without this
                // gate the caller would silently latch onto a room another client is actively mutating.
                // Surface it as a message a UI can show rather than a bare failure.
                if (ConcurrencyGuard.IsHeld(request.DirectoryPath))
                {
                    return Results.BadRequest("This room is being run by another client.");
                }

                session.SetCurrentRoomDirectory(request.DirectoryPath);
                await session.RecordOpenedAsync(request.DirectoryPath);
                var outcome = await session.LoadAsync(request.DirectoryPath);
                if (outcome.Projection != null)
                {
                    var bindingsPath = await session.LoadLastBindingsFilePathAsync();
                    if (bindingsPath != null)
                    {
                        pathHolder.BindingsFilePath = bindingsPath;
                    }
                    await broadcast.BroadcastStateAsync(outcome.Projection, request.DirectoryPath);
                    return Results.Ok(outcome.Projection);
                }
                // #324: LoadAsync can fail without setting a message (its in-process fallback only
                // fills ErrorMessage from a caught AerFlowException). Never return a bare 400 -- a
                // client, especially a phone, must always get a sentence it can show the user.
                return Results.BadRequest(string.IsNullOrEmpty(outcome.ErrorMessage)
                    ? "Could not open the room. Its saved state could not be read."
                    : outcome.ErrorMessage);
            });

            app.MapPost("/api/rooms/run", async ([FromBody] RunRoomRequest request, RoomClient session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");
                if (string.IsNullOrEmpty(request.BindingsFilePath)) return Results.BadRequest("BindingsFilePath is required.");

                // #1230 / decision 0056: the room keeps its own copy of the bindings it was run with.
                // Run still asks — this records the answer rather than inferring one, which is what the
                // M14 Phase 2 note was actually about. Overwritten every run, so re-binding stays an
                // explicit per-run choice. /api/templates/run and `aer dispatch` already did this; this
                // endpoint was the one that did not, which is why deciding such a room had nothing of
                // its own to resolve and fell back to whichever room was run last.
                try
                {
                    MaterializeRoomBindings(request.DirectoryPath, request.BindingsFilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Told, not swallowed: without its own copy this room is exactly the one #1230 is
                    // about, so failing quietly here would re-create the defect one release later.
                    return Results.BadRequest(
                        "Baton could not give this room its own copy of the worker setup, so the run was "
                        + $"not started: {ex.Message}");
                }

                pathHolder.BindingsFilePath = request.BindingsFilePath;

                // #330: unlike /api/rooms/open and /api/templates/run, this endpoint -- the one the
                // desktop's own RoomClient.RunAsync HTTP branch posts to -- never gave already-
                // connected clients (a paired phone) any immediate sign that a run just started here.
                // Best-effort and may no-op for a brand-new room (no snapshot.json until the pump
                // below binds one): the guaranteed broadcast is still the one RunAsync's own
                // reopenRoomAsync hook fires on completion. This closes the gap for the common case
                // this projection already exists -- a resumed/re-run room -- immediately instead of
                // only once the whole pump finishes.
                session.SetCurrentRoomDirectory(request.DirectoryPath);
                var immediateOutcome = await session.LoadAsync(request.DirectoryPath);
                if (immediateOutcome.Projection != null)
                {
                    await broadcast.BroadcastStateAsync(immediateOutcome.Projection, request.DirectoryPath);
                }

                // #590: same per-directory serialisation as chat turns (SessionTurnLockFor's
                // remarks) -- bindings.json here can carry the same persisted vendor SessionId a
                // chat turn minted (Program.cs's ExecuteSessionTurnCoreAsync), and this endpoint has
                // no lock of its own, so a /api/sessions/send racing this call would otherwise
                // dispatch that id twice concurrently (see vendor-doc-audit.md on --session-id).
                _ = Task.Run(async () =>
                {
                    var turnLock = SessionTurnLockFor(request.DirectoryPath);
                    await turnLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // #828: this dispatch runs fire-and-forget behind an already-returned 200 --
                        // Console.Error alone is gone the moment the daemon exits, and unlike the chat
                        // path (#341's turn-errors.log) nothing else recorded the failure. RoomClient.
                        // RunAsync's in-process fallback catches every AerFlowException itself and
                        // returns a MutationOutcome rather than throwing (see its own remarks), so the
                        // outcome's ErrorMessage -- not an escaping exception -- is the only place most
                        // dispatch failures (e.g. this directory's own WorkflowLockedException) are ever
                        // observable; the catch below is defense in depth for whatever can still escape.
                        var outcome = await session.RunAsync(
                            request.DirectoryPath,
                            request.WorkflowTemplateFilePath,
                            request.BindingsFilePath);
                        if (outcome.ErrorMessage is { } errorMessage)
                        {
                            await AppendTurnErrorAsync(request.DirectoryPath, "/api/rooms/run", errorMessage).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing room run in background: {ex}");
                        await AppendTurnErrorAsync(request.DirectoryPath, "/api/rooms/run", ex).ConfigureAwait(false);
                    }
                    finally
                    {
                        turnLock.Release();
                    }
                });

                return Results.Ok();
            });

            app.MapPost("/api/rooms/decide", async ([FromBody] DecideRoomRequest request, RoomClient session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");

                // #1227: answered, accepted, and silently lost. Deciding a step resumes the room, which
                // needs the worker bindings — and the daemon used to learn them only from the one
                // user-global slot decision 0056 describes (docs/decisions/0056-a-room-carries-its-own-worker-bindings.md).
                // For a room it did not start there was none, and the empty path reached File.ReadAllTextAsync
                // as an ArgumentException — not in DecideAsync's
                // `when (ex is AerFlowException or FileNotFoundException)` filter, so it escaped into the
                // fire-and-forget below, after this endpoint had already answered 200. The phone showed
                // "Approved review" over a room that never moved.
                //
                // Refused here, before that task exists, because this is the last point at which the
                // person can be told: two lines up, /api/rooms/open's own comment states the rule this
                // restores — "a client, especially a phone, must always get a sentence it can show the
                // user."
                //
                // #1230 then settled *which* bindings a decision uses: the room's own, per decision 0056.
                // The global slot is off this path entirely.
                //
                // The check itself sits AFTER the artifact-reference validation below, deliberately.
                // Put first, it answers 400 before that validation runs — which would leave the
                // traversal-refusal test passing for the wrong reason (a bindings 400 rather than the
                // reference 400 it exists to pin), the precise failure mode a second reader caught in
                // this milestone a day earlier.
                var revisionFilePath = request.RevisionFilePath;
                if (string.IsNullOrEmpty(revisionFilePath) && request.ArtifactReference != null)
                {
                    var referenceOutcome = await session.LoadAsync(request.DirectoryPath);
                    if (referenceOutcome.Projection is not { } referenceProjection)
                    {
                        // #324: same guarantee as /api/rooms/open -- LoadAsync may fail without a message.
                        return Results.BadRequest(string.IsNullOrEmpty(referenceOutcome.ErrorMessage)
                            ? "Could not open the room. Its saved state could not be read."
                            : referenceOutcome.ErrorMessage);
                    }

                    var referencedExecution = referenceProjection.Lineage.Executions.FirstOrDefault(
                        e => e.ExecutionId.Value == request.ArtifactReference.ExecutionId);
                    if (referencedExecution is null || !referencedExecution.OutputFiles.Contains(request.ArtifactReference.FileName))
                    {
                        return Results.BadRequest("ArtifactReference does not name a known output file for that execution.");
                    }

                    var outputDir = ArtifactManager.ResolveOutputDirectory(
                        Path.Combine(request.DirectoryPath, ArtifactManager.ArtifactsDirectoryName),
                        referencedExecution.ExecutionId);
                    var candidatePath = Path.Combine(outputDir, request.ArtifactReference.FileName);
                    if (File.Exists(candidatePath))
                    {
                        revisionFilePath = candidatePath;
                    }
                }

                // #1227's refusal — see the reasoning above the artifact-reference block. Last point
                // at which the person can be told anything, since everything below this line is
                // fire-and-forget.
                // The wording promises nothing that was not traced, which took three attempts and a
                // second reader to get right. "Open the room on the desktop first" was wrong:
                // /api/rooms/open fills this slot from the config store's last-used bindings, so it
                // teaches the daemon nothing about *this* room and may teach it nothing at all. "Start
                // or resume it from the desktop" was also wrong, and worse — a paused room is
                // NeedsYou, and DeriveRoomStoppedReason only offers Run/Resume for Stopped, Finished
                // and Cancelled, so those buttons do not exist in the state this message is shown in.
                //
                // So it names no remedy. There is not reliably one today: a room whose bindings this
                // daemon never received cannot be decided here, and the honest thing is to say what is
                // wrong and why rather than send someone hunting for a button that is not there.
                // Giving it a real remedy means giving a room its own worker setup — #1230.
                // #1230 / decision 0056: the room's OWN bindings, never the global slot. What the slot
                // cost before this, and why the precedence runs this way round, is recorded there.
                var roomBindingsPath = AerPaths.RoomBindingsFile(request.DirectoryPath);
                if (!File.Exists(roomBindingsPath) && !string.IsNullOrEmpty(request.BindingsFilePath))
                {
                    // The unstick path 0056 allows, and the only case a caller's file is consulted at
                    // all: an older room gets its copy the first time a client that knows the file
                    // decides in it.
                    if (!File.Exists(request.BindingsFilePath))
                    {
                        return Results.BadRequest("The BindingsFilePath given does not name a file that exists.");
                    }

                    try
                    {
                        MaterializeRoomBindings(request.DirectoryPath, request.BindingsFilePath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return Results.BadRequest(
                            $"Baton could not give this room its own copy of the worker setup: {ex.Message}");
                    }
                }

                if (!File.Exists(roomBindingsPath))
                {
                    // Now names a remedy, because #1230 built one. The two earlier wordings named
                    // actions that did not exist — "open the room on the desktop" (open refills a
                    // last-used slot and teaches the daemon nothing about this room) and "start or
                    // resume it" (a paused room is NeedsYou; those buttons are not in that state).
                    return Results.BadRequest(
                        "Baton doesn't know which workers this room runs, so it can't carry out the decision. "
                        + "That happens for a room made before rooms kept their own worker setup. Run the room "
                        + "once from the desktop, choosing its workers, and Baton will remember them with the "
                        + "room from then on.");
                }

                // #590: see the matching lock in /api/rooms/run above -- same persisted-SessionId
                // exposure, same fix.
                _ = Task.Run(async () =>
                {
                    var turnLock = SessionTurnLockFor(request.DirectoryPath);
                    await turnLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // #1230: point the resolver at THIS room's bindings, inside the lock. The
                        // holder is one user-global slot, so setting it before the lock would leave a
                        // window where a concurrent run or send replaces it between the assignment and
                        // the dispatch below — the same wrong-workers outcome by a narrower route.
                        // /api/sessions/send does the per-room assignment too (see its own line), but
                        // outside its lock; this is the shape that one should converge on.
                        pathHolder.BindingsFilePath = roomBindingsPath;

                        // #828: same gap as /api/rooms/run above -- RoomClient.DecideAsync's
                        // in-process fallback also catches AerFlowException/FileNotFoundException
                        // itself and returns a MutationOutcome rather than throwing, so its
                        // ErrorMessage is the primary place a failure here (e.g. this decision losing
                        // a race to ExternalDecisionValidator) is observable.
                        var outcome = await session.DecideAsync(
                            request.DirectoryPath,
                            new StepId(request.StepId),
                            new ExecutionId(request.ExecutionId),
                            request.DecisionType,
                            request.TargetStepId != null ? new StepId(request.TargetStepId) : null,
                            revisionFilePath,
                            request.SupplementaryWorker,
                            request.SupplementaryOutputName);
                        if (outcome.ErrorMessage is { } errorMessage)
                        {
                            await AppendTurnErrorAsync(request.DirectoryPath, "/api/rooms/decide", errorMessage).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing room decide in background: {ex}");
                        await AppendTurnErrorAsync(request.DirectoryPath, "/api/rooms/decide", ex).ConfigureAwait(false);
                    }
                    finally
                    {
                        turnLock.Release();
                    }
                });

                return Results.Ok();
            });

            // #1238: the way back, and what it costs a person not to have one is stated on
            // RuntimePermissionGrantAmender.RevokeAsync, which this reaches.
            //
            // It is a POST rather than a DELETE deliberately: the thing being withdrawn has no URL of
            // its own, and the kinds are two different withdrawals (a room-wide shell permission, and
            // one named command family) rather than one resource removed twice.
            app.MapPost("/api/rooms/permissions/revoke", async ([FromBody] RevokePermissionRequest request, RoomClient session) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");
                if (string.IsNullOrEmpty(request.RevokeKind)) return Results.BadRequest("RevokeKind is required.");

                if (request.RevokeKind is not (PermissionRevokeKind.RoomShell or PermissionRevokeKind.CommandInRoom))
                {
                    // Named rather than echoed back as a shrug: an unknown kind here is a caller bug,
                    // and the two that exist are a short list worth saying out loud.
                    return Results.BadRequest(
                        $"Unknown RevokeKind '{request.RevokeKind}'. Expected " +
                        $"'{PermissionRevokeKind.RoomShell}' or '{PermissionRevokeKind.CommandInRoom}'.");
                }

                if (request.RevokeKind == PermissionRevokeKind.CommandInRoom
                    && string.IsNullOrEmpty(request.ShellCommandPattern))
                {
                    return Results.BadRequest(
                        "ShellCommandPattern is required when revoking one command's standing permission.");
                }

                var revokeWorkerName = string.IsNullOrEmpty(request.WorkerName)
                    ? InteractiveSessionMaterializer.DefaultWorkerName
                    : request.WorkerName;

                PermissionRevokeOutcome revokeOutcome;
                try
                {
                    // The same room-events guard the answer path's amend takes, for the same reason:
                    // this is a read-modify-write of bindings.json, and a turn's own per-turn write can
                    // race it. AcquireWithin, not Acquire — that holder releases in milliseconds.
                    using var revokeGuard = ConcurrencyGuard.AcquireRoomEventsWithin(
                        request.DirectoryPath, TimeSpan.FromSeconds(2), "permission revoke");
                    revokeOutcome = await RuntimePermissionGrantAmender.RevokeAsync(
                        request.DirectoryPath,
                        revokeWorkerName,
                        request.RevokeKind,
                        request.ShellCommandPattern).ConfigureAwait(false);
                }
                catch (WorkflowLockedException ex)
                {
                    // Refused, not reported as done. The asymmetry with the answer path is deliberate:
                    // there, losing the lock narrows an answer that was already recorded, and the honest
                    // thing is to say so and carry on. Here, nothing has been recorded at all, and a 200
                    // would tell the person a permission is gone while it is still in force.
                    return Results.Problem(
                        $"Could not take back that permission: the room was busy ({ex.Message}). Try again.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (revokeOutcome == PermissionRevokeOutcome.CouldNotPersist)
                {
                    // The outcome covers two situations and they need different sentences: a room with
                    // no worker setup at all is not the same problem as a room whose setup lacks THIS
                    // worker, and naming the wrong one sends the person looking in the wrong place.
                    return Results.BadRequest(
                        File.Exists(AerPaths.RoomBindingsFile(request.DirectoryPath))
                            ? $"This room doesn't have worker '{revokeWorkerName}' to take a permission back from."
                            : "This room has no worker setup, so it holds no permissions to take back.");
                }

                // The projection does not carry standing permissions, so nothing to broadcast — the
                // change lands on the next turn's grant build. Saying which of the two happened is the
                // whole caller-facing value: "there was nothing to take back" and "it is taken back"
                // are different sentences for a person who just asked for one.
                return Results.Ok(new { Outcome = revokeOutcome.ToString() });
            });

            app.MapPost("/api/rooms/permissions/answer", async ([FromBody] AnswerPermissionRequest request, RoomClient session) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");
                if (string.IsNullOrEmpty(request.PermissionRequestId)) return Results.BadRequest("PermissionRequestId is required.");
                if (string.IsNullOrEmpty(request.DecisionKind)) return Results.BadRequest("DecisionKind is required.");

                // Read once up front, regardless of which branch resolves outputDir below: #390 needs
                // the asked ToolName/ToolInputJson to persist a scoped grant, and PendingGateRegistry
                // (the live-doorbell path) never carried them.
                RoomEvent.RuntimePermissionAsked? askedEvent = null;
                bool isResolvedInJournal = false;
                var permissionsAnswerRoomLogPath = Path.Combine(request.DirectoryPath, "room.jsonl");
                if (File.Exists(permissionsAnswerRoomLogPath))
                {
                    var askedReader = new RoomEventLogReader(permissionsAnswerRoomLogPath);
                    var askedEvents = await askedReader.ReadAllRoomEventsAsync().ConfigureAwait(false);
                    askedEvent = askedEvents.OfType<RoomEvent.RuntimePermissionAsked>()
                        .FirstOrDefault(a => a.PermissionRequestId == request.PermissionRequestId);

                    isResolvedInJournal = askedEvents.Any(e => e switch
                    {
                        RoomEvent.RuntimePermissionAnswered ans => ans.PermissionRequestId == request.PermissionRequestId,
                        RoomEvent.RuntimePermissionRevoked rev => rev.PermissionRequestId == request.PermissionRequestId,
                        _ => false
                    });
                }

                string? outputDir = null;
                if (PendingGateRegistry.TryGet(request.PermissionRequestId, out var entry))
                {
                    outputDir = entry.OutputDir;
                }
                else if (askedEvent != null)
                {
                    var artifactsDir = Path.Combine(request.DirectoryPath, ArtifactManager.ArtifactsDirectoryName);
                    outputDir = ArtifactManager.ResolveOutputDirectory(artifactsDir, askedEvent.ExecutionId);
                }

                if (string.IsNullOrEmpty(outputDir))
                {
                    return Results.NotFound($"Permission request '{request.PermissionRequestId}' was not found.");
                }

                var revokedFilePath = Path.Combine(outputDir, $"revoked-{request.PermissionRequestId}.json");
                if (isResolvedInJournal || File.Exists(revokedFilePath))
                {
                    return Results.Conflict(new
                    {
                        error = $"Permission request '{request.PermissionRequestId}' is revoked or already answered.",
                        permissionRequestId = request.PermissionRequestId,
                        status = "Revoked"
                    });
                }

                Directory.CreateDirectory(outputDir);

                string? updatedInputJson = null;
                if (request.UpdatedInput.HasValue && request.UpdatedInput.Value.ValueKind != JsonValueKind.Undefined && request.UpdatedInput.Value.ValueKind != JsonValueKind.Null)
                {
                    updatedInputJson = request.UpdatedInput.Value.ValueKind == JsonValueKind.String
                        ? request.UpdatedInput.Value.GetString()
                        : request.UpdatedInput.Value.GetRawText();
                }

                var answerPayload = new
                {
                    decisionKind = request.DecisionKind,
                    updatedInputJson = updatedInputJson,
                    reason = request.Reason
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };

                // Journal FIRST, then write the answer file. The answer file is what releases the
                // waiting worker, so this order means a failed journal write leaves the worker
                // waiting and the operator retrying — never a released worker whose answer is
                // missing from room.jsonl forever (second-reader finding on #1098: the room guard
                // is fail-fast and RoomWakeBridge contends it, so a lost race here was permanent).
                var roomLogFile = Path.Combine(request.DirectoryPath, "room.jsonl");
                await RetryOnRoomLockAsync(async () =>
                {
                    var roomReader = new RoomEventLogReader(roomLogFile);
                    await using var roomWriter = new RoomEventLogWriter(roomLogFile);
                    await RoomMutationInterface.AnswerPermissionAsync(
                        request.DirectoryPath,
                        roomReader,
                        roomWriter,
                        request.PermissionRequestId,
                        request.DecisionKind,
                        updatedInputJson,
                        request.Reason,
                        "human").ConfigureAwait(false);
                }).ConfigureAwait(false);

                var answerFileName = $"answer-{request.PermissionRequestId}.json";
                var answerFilePath = Path.Combine(outputDir, answerFileName);
                var tempFilePath = Path.Combine(outputDir, $"{answerFileName}.{Guid.NewGuid():N}.tmp");

                var answerJson = JsonSerializer.Serialize(answerPayload, jsonOptions);
                await File.WriteAllTextAsync(tempFilePath, answerJson).ConfigureAwait(false);
                File.Move(tempFilePath, answerFilePath, overwrite: true);

                // #390: for a persisting rung, amend the room's chat-worker PermissionGrant. See
                // RuntimePermissionGrantAmender for how the next interactive turn then enforces it.
                if (askedEvent != null)
                {
                    // The answer file written above already released the worker, so a fast next turn's
                    // per-turn bindings.json write can race this read-modify-write. Take the same room
                    // guard the mutation path uses; AcquireWithin (not Acquire) because that holder
                    // releases in milliseconds -- #857's shape -- and a routine overlap should wait, not
                    // 500 an already-recorded answer. If it stays held, fail narrow: the answer applies
                    // once only (never wider) and we say so.
                    try
                    {
                        using var amendGuard = ConcurrencyGuard.AcquireRoomEventsWithin(
                            request.DirectoryPath, TimeSpan.FromSeconds(2), "permission-answer grant amend");
                        var amendOutcome = await RuntimePermissionGrantAmender.AmendAsync(
                            request.DirectoryPath,
                            InteractiveSessionMaterializer.DefaultWorkerName,
                            request.DecisionKind,
                            askedEvent.ToolName,
                            askedEvent.ToolInputJson).ConfigureAwait(false);

                        if (amendOutcome == PermissionAmendOutcome.CouldNotPersist)
                        {
                            // The operator picked a standing rung but no grant could be written (no
                            // binding, or the asked command was unparseable and derivation failed
                            // closed). It applies once only -- surface the narrowing, don't swallow it.
                            Console.Error.WriteLine(
                                $"Permission answer '{request.DecisionKind}' for '{askedEvent.ToolName}' could not " +
                                "persist a standing permission; it applies once only.");
                        }
                    }
                    catch (WorkflowLockedException ex)
                    {
                        Console.Error.WriteLine(
                            $"Permission answer '{request.DecisionKind}' recorded, but persisting the standing " +
                            $"grant lost the room lock ({ex.Message}); it applies once only.");
                    }
                }

                PendingGateRegistry.TryRemove(request.PermissionRequestId, out _);

                var outcome = await session.LoadAsync(request.DirectoryPath).ConfigureAwait(false);
                if (outcome.Projection is { } proj)
                {
                    await broadcast.BroadcastStateAsync(proj, request.DirectoryPath).ConfigureAwait(false);
                }

                return Results.Ok();
            });

            app.MapPost("/api/rooms/cancel", async ([FromBody] CancelRoomRequest request, RoomClient session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");

                // #1230's second reader: cancel had the same defect decide did, on a path 0056 did not
                // touch. CancelExecutionAsync short-circuits while THIS daemon is hosting the room's
                // pump, but otherwise falls through to CancelCommand, which loads whatever the global
                // slot happens to name — the last room anything ran, opened or sent to. So a cancel for
                // a room this daemon is not currently driving (it restarted mid-run, the pump already
                // deregistered, another process started the execution) resolved a different room's
                // workers, silently. Point it at this room's own bindings, per decision 0056.
                var cancelBindingsPath = AerPaths.RoomBindingsFile(request.DirectoryPath);
                if (File.Exists(cancelBindingsPath))
                {
                    pathHolder.BindingsFilePath = cancelBindingsPath;
                }

                if (!string.IsNullOrEmpty(request.ExecutionId))
                {
                    await session.CancelExecutionAsync(request.DirectoryPath, new ExecutionId(request.ExecutionId));
                }
                else
                {
                    // Targeted at the directory the caller named (#335). The parameterless overload
                    // stops every hosted pump, which was indistinguishable from "stop this one" only
                    // while the daemon could host a single session -- with two running it stopped
                    // whichever started last, so a client asking to stop A stopped B.
                    session.RequestHostStop(request.DirectoryPath);
                }

                await RevokePendingGatesForRoomAsync(
                    request.DirectoryPath,
                    request.ExecutionId,
                    "cancelled",
                    async (proj, dir) => await broadcast.BroadcastStateAsync(proj, dir).ConfigureAwait(false),
                    async () => (await session.LoadAsync(request.DirectoryPath).ConfigureAwait(false)).Projection).ConfigureAwait(false);

                return Results.Ok();
            });

            // M24 Phase 5 (#278): the fleet list — every known room directory's lightweight status,
            // scanning ~/.aer/rooms, the one root /api/templates/run and /api/sessions materialize
            // into (#443 unified the former ~/.aer/tasks and ~/.aer/sessions). Archived items are
            // filtered out by default (the everyday view); includeArchived=true surfaces them for
            // the management screen. A directory that fails to load (corrupt snapshot/log) is skipped
            // rather than failing the whole list, since one bad item shouldn't hide every other room.
            app.MapGet("/api/rooms", async (bool? includeArchived) =>
            {
                // #333: one root, one kind of record -- the two-root concatenation this replaced is
                // what "one list of one kind of thing" was blocked on.
                var baseRoomsDir = AerPaths.Rooms;

                var directories = new List<string>();
                if (Directory.Exists(baseRoomsDir))
                {
                    directories.AddRange(Directory.GetDirectories(baseRoomsDir));
                }

                var items = new List<RoomFleetItem>();
                foreach (var directory in directories)
                {
                    try
                    {
                        var item = await RoomProjectionLoader.LoadFleetStatusAsync(directory).ConfigureAwait(true);
                        if (item.IsArchived && includeArchived != true)
                        {
                            continue;
                        }
                        items.Add(item);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error loading fleet status for '{directory}': {ex}");
                    }
                }

                // #640: most-recent-activity first (derived from journal events), ties broken by name so
                // ordering is stable across refreshes.
                return Results.Ok(items.OrderByDescending(i => i.LastActivityAt ?? i.Updated).ThenBy(i => i.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList());
            });

            app.MapPost("/api/rooms/archive", async ([FromBody] RoomDirectoryRequest request) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }
                if (!TryResolveManagedRoomDirectory(request.DirectoryPath, out var resolvedPath))
                {
                    return Results.BadRequest("DirectoryPath must be inside ~/.aer/rooms.");
                }

                await RoomLifecycle.ArchiveAsync(resolvedPath).ConfigureAwait(true);
                return Results.Ok();
            });

            app.MapPost("/api/rooms/unarchive", async ([FromBody] RoomDirectoryRequest request) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }
                if (!TryResolveManagedRoomDirectory(request.DirectoryPath, out var resolvedPath))
                {
                    return Results.BadRequest("DirectoryPath must be inside ~/.aer/rooms.");
                }

                await RoomLifecycle.UnarchiveAsync(resolvedPath).ConfigureAwait(true);
                return Results.Ok();
            });

            // A real delete frees the directory's name for reuse (RoomDirectoryAlreadyExistsException's
            // collision guard checks File.Exists on workflow.json, which archiving alone never
            // clears — see RoomLifecycle's remarks) and also strips the stale recent so a later
            // /api/rooms/recent-driven open doesn't 404 on a directory that no longer exists.
            app.MapPost("/api/rooms/delete", async ([FromBody] RoomDirectoryRequest request, LocalUiConfigurationStore configStore) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }
                if (!TryResolveManagedRoomDirectory(request.DirectoryPath, out var resolvedPath))
                {
                    return Results.BadRequest("DirectoryPath must be inside ~/.aer/rooms.");
                }

                if (!Directory.Exists(resolvedPath))
                {
                    return Results.NotFound();
                }

                Directory.Delete(resolvedPath, recursive: true);
                await configStore.RemoveRecentRoomDirectoryAsync(resolvedPath).ConfigureAwait(true);
                return Results.Ok();
            });

            // M21 Phase 2 (#232): a client with no access to the daemon host's filesystem
            // (Aer.Mobile) otherwise has no way to see what it's approving — RoomProjection only
            // ever carries file *paths*, never bytes (HomeViewModel's desktop-side inbox preview
            // reads artifact content straight off local disk). fileName is validated against the
            // execution's own recorded OutputFiles rather than trusted as a raw path component,
            // the same containment guarantee that desktop-side preview already relies on. Text
            // content only — capped well above the Home inbox snippet's 400 chars, since a phone
            // has no "open the real file" fallback, but still bounded so one huge artifact can't
            // stall a slow LAN/cellular transfer.
            app.MapGet("/api/rooms/artifact", async (string directoryPath, string executionId, string fileName, RoomClient session) =>
            {
                if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(fileName))
                {
                    return Results.BadRequest("directoryPath, executionId, and fileName are required.");
                }

                var outcome = await session.LoadAsync(directoryPath);
                if (outcome.Projection is not { } projection)
                {
                    return Results.BadRequest(outcome.ErrorMessage);
                }

                var execution = projection.Lineage.Executions.FirstOrDefault(e => e.ExecutionId.Value == executionId);
                if (execution is null || !execution.OutputFiles.Contains(fileName))
                {
                    return Results.NotFound();
                }

                var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                    Path.Combine(directoryPath, ArtifactManager.ArtifactsDirectoryName), execution.ExecutionId);

                try
                {
                    var content = await File.ReadAllTextAsync(Path.Combine(outputDirectory, fileName));
                    var truncated = content.Length > ArtifactPreviewMaxLength;
                    return Results.Ok(new
                    {
                        Content = truncated ? content[..ArtifactPreviewMaxLength] + "…" : content,
                        Truncated = truncated,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Results.NotFound();
                }
            });

            // M24 Phase 1 (#262): Interactive Sessions (Chat) endpoints
            app.MapPost("/api/sessions/start", async ([FromBody] StartSessionRequest request, RoomClient session, BindingsPathHolder pathHolder, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var adapter = string.IsNullOrWhiteSpace(request.Adapter) ? "claude" : request.Adapter.Trim().ToLowerInvariant();
                var sessionId = Guid.NewGuid().ToString("N")[..12];
                var roomDirectoryPath = InteractiveSessionMaterializer.ResolveRoomDirectoryPath(sessionId, request.RoomName, request.DirectoryPath);

                if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
                {
                    await KnownProjectsStore.AddOrUpdateProjectAsync(request.WorkingDirectory).ConfigureAwait(true);
                }

                // A null PermissionGrant is resolved to the working-directory-aware default inside
                // Materialize (fail closed when there's no directory -- #321 / decision 0004), so the
                // policy lives in one place instead of being re-decided at each call site.
                SessionMetadata metadata;
                try
                {
                    metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                        sessionId,
                        roomDirectoryPath,
                        adapter,
                        request.Model,
                        request.WorkingDirectory,
                        request.InitialMessage,
                        request.SafetyCeiling ?? InteractiveSessionMaterializer.DefaultSafetyCeiling,
                        request.PermissionGrant).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(ex.Message);
                }

                var bindingsFilePath = AerPaths.RoomBindingsFile(roomDirectoryPath);

                pathHolder.BindingsFilePath = bindingsFilePath;
                session.SetCurrentRoomDirectory(roomDirectoryPath);
                await session.RecordOpenedAsync(roomDirectoryPath).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(request.InitialMessage))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteSessionTurnAsync(session, roomDirectoryPath, metadata, request.InitialMessage, adapter, request.Model, isInitial: true, broadcast.BroadcastStateAsync, adapters, broadcast.BroadcastSessionProgressAsync).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error running initial session turn: {ex}");
                        }
                    });
                }
                else
                {
                    var outcome = await session.LoadAsync(roomDirectoryPath).ConfigureAwait(true);
                    if (outcome.Projection != null)
                    {
                        await broadcast.BroadcastStateAsync(outcome.Projection, roomDirectoryPath).ConfigureAwait(true);
                    }
                }

                return Results.Ok(metadata);
            });

            app.MapPost("/api/sessions/send", async ([FromBody] SendSessionMessageRequest request, RoomClient session, BindingsPathHolder pathHolder, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return Results.BadRequest("Message is required.");
                }

                string? directoryPath = request.DirectoryPath;
                if (string.IsNullOrEmpty(directoryPath) && !string.IsNullOrEmpty(request.SessionId))
                {
                    var resolvedBySessionId = await ResolveSessionAsync(request.SessionId);
                    if (resolvedBySessionId != null)
                    {
                        directoryPath = resolvedBySessionId.Value.DirectoryPath;
                    }
                }

                if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
                {
                    return Results.BadRequest("DirectoryPath or valid SessionId is required.");
                }

                var metadataPath = Path.Combine(directoryPath, ".aer", AerPaths.RoomMetadataFileName);
                var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath).ConfigureAwait(true);
                if (metadata == null)
                {
                    return Results.BadRequest("Not a valid interactive session directory.");
                }

                // #1179: a freshly materialized room (POST /api/sessions/start) is never dormant --
                // its room.jsonl has no dormancy transition yet -- so that endpoint needs no change;
                // this check only ever matters on a send into an already-existing room. The check and
                // the dispatch below are deliberately not atomic: dormancy entered between them lets
                // exactly one already-accepted human turn complete, and every later send sees the
                // dormant state -- bounded and self-correcting, so no lock spans the vendor dispatch.
                if (await IsRoomDormantAsync(directoryPath).ConfigureAwait(true))
                {
                    // 03-interaction-depth.md: "Dormancy answers, it never resumes" -- a message to a
                    // dormant room is answered by the product with the dormancy state, not dispatched
                    // to a worker. No ExecuteSessionTurnAsync call, no Task.Run: the human message is
                    // recorded as an answered turn (no vendor process ran, hence AssistantResponse
                    // stays null) and the ONLY way a real turn runs again is the existing Wake path
                    // (/api/rooms/turn-host/clear-dormancy).
                    //
                    // #393/#285 (the #1179 review's blocking find): this is a metadata read-modify-write
                    // like every real turn's, and SaveMetadataAsync is last-writer-wins by design -- so
                    // it takes the SAME per-directory turn lock and re-reads metadata inside it, exactly
                    // as ExecuteSessionTurnAsync does. Without this, a dormancy answer built from the
                    // endpoint's pre-lock snapshot could land after an in-flight real turn's save and
                    // silently erase that turn (reverting VendorSessionEstablished -- the #285 wedge).
                    var dormancyTurnLock = SessionTurnLockFor(directoryPath);
                    await dormancyTurnLock.WaitAsync().ConfigureAwait(true);
                    try
                    {
                        var currentMetadata =
                            await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath).ConfigureAwait(true)
                            ?? metadata;

                        var dormancyTurn = new SessionTurn(
                            TurnIndex: currentMetadata.TurnCount + 1,
                            Vendor: "System",
                            HumanMessage: request.Message,
                            AssistantResponse: null,
                            ExecutedAt: DateTimeOffset.UtcNow,
                            NativeSessionResumed: false,
                            VendorHandoffSynthesized: false,
                            IsDormancyAnswer: true);

                        var updatedMetadata = currentMetadata with
                        {
                            TurnCount = currentMetadata.TurnCount + 1,
                            UpdatedAt = DateTimeOffset.UtcNow,
                            Turns = new List<SessionTurn>(currentMetadata.Turns) { dormancyTurn },
                        };

                        await InteractiveSessionMaterializer.SaveMetadataAsync(updatedMetadata, metadataPath).ConfigureAwait(true);
                    }
                    finally
                    {
                        dormancyTurnLock.Release();
                    }

                    // Same broadcast a completed turn ends with (ExecuteSessionTurnAsync's finally
                    // block), so both surfaces update without waiting on their own poll.
                    var refreshedProjection = (await session.LoadAsync(directoryPath).ConfigureAwait(true)).Projection;
                    if (refreshedProjection != null)
                    {
                        await broadcast.BroadcastStateAsync(refreshedProjection, directoryPath).ConfigureAwait(true);
                    }

                    return Results.Ok(new { SessionId = metadata.SessionId, RoomDirectoryPath = directoryPath });
                }

                pathHolder.BindingsFilePath = AerPaths.RoomBindingsFile(directoryPath);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteSessionTurnAsync(session, directoryPath, metadata, request.Message, request.Adapter, request.Model, isInitial: false, broadcast.BroadcastStateAsync, adapters, broadcast.BroadcastSessionProgressAsync).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // #341: this turn runs fire-and-forget behind an already-returned 200, so a
                        // throw here reached Console.Error and nowhere else -- the client saw success
                        // and then silence forever, which is exactly how a stalled chat presents.
                        // Persist it next to the session so the failure survives the daemon and can
                        // be read after the fact; the console line alone is gone the moment CI's
                        // process exits, which is why this took a day to characterize.
                        Console.Error.WriteLine($"Error executing session message turn: {ex}");
                        await AppendTurnErrorAsync(directoryPath, request.Message, ex).ConfigureAwait(false);
                    }
                });

                return Results.Ok(new { SessionId = metadata.SessionId, RoomDirectoryPath = directoryPath });
            });

            app.MapGet("/api/sessions/{sessionId}", async (string sessionId) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }
                return Results.Ok(resolved.Value.Metadata);
            });

            app.MapGet("/api/sessions", async () =>
            {
                var baseRoomsDir = AerPaths.Rooms;
                if (!Directory.Exists(baseRoomsDir))
                {
                    return Results.Ok(Array.Empty<SessionMetadata>());
                }

                var list = new List<SessionMetadata>();
                foreach (var dir in Directory.GetDirectories(baseRoomsDir))
                {
                    var metadataPath = Path.Combine(dir, ".aer", AerPaths.RoomMetadataFileName);
                    if (File.Exists(metadataPath))
                    {
                        try
                        {
                            var meta = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath);
                            if (meta != null) list.Add(meta);
                        }
                        catch (OperationCanceledException)
                        {
                            // See the by-id scan's note on this carve-out.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // #1229, same rule as the by-id scan above and /api/rooms: one unreadable
                            // room is skipped rather than emptying the whole list.
                            Console.Error.WriteLine($"Error reading session metadata for '{dir}': {ex}");
                        }
                    }
                }

                return Results.Ok(list.OrderByDescending(s => s.UpdatedAt));
            });

            // M24 Phase 2 (#263): Capabilities discovery & Session compact endpoints
            app.MapGet("/api/sessions/{sessionId}/commands", async (string sessionId, IReadOnlyDictionary<string, IWorkerAdapter> adapters, LocalUiConfigurationStore configStore) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var metadata = resolved.Value.Metadata;
                if (!adapters.TryGetValue(metadata.CurrentAdapter, out var adapter))
                {
                    adapter = adapters["claude"];
                }

                var capabilities = await adapter.DiscoverCapabilitiesAsync(metadata.WorkingDirectory);
                var recentlyUsed = await configStore.LoadRecentCommandsAsync(metadata.CurrentAdapter);

                // RecentlyUsed is an additive sibling property, same idiom as the WS payload's
                // SessionId/DirectoryPath (PR #276) -- existing callers deserializing straight into
                // WorkerCapabilities are unaffected (unmapped JSON members are ignored by default).
                return Results.Ok(new
                {
                    capabilities.Vendor,
                    capabilities.Items,
                    capabilities.Models,
                    RecentlyUsed = recentlyUsed,
                });
            });

            app.MapPost("/api/sessions/{sessionId}/commands/record", async (string sessionId, [FromBody] RecordCommandUsedRequest request, LocalUiConfigurationStore configStore) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name is required.");
                }

                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                await configStore.RecordCommandUsedAsync(resolved.Value.Metadata.CurrentAdapter, request.Name.Trim());
                return Results.Ok();
            });

            // Session-level mode (M24 Phase 2 follow-up): PermissionGrant already persists across
            // turns via bindings.json (ExecuteSessionTurnAsync reads the existing entry's grant each
            // turn), but nothing let a user change it mid-session -- it was fixed at whatever
            // /api/sessions/start set. This updates bindings.json directly so the *next* turn (any
            // vendor) picks up the new grant, translated per-vendor by that adapter's own existing
            // PermissionGrant translation.
            app.MapPost("/api/sessions/{sessionId}/mode", async (string sessionId, [FromBody] SetSessionModeRequest request) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var directoryPath = resolved.Value.DirectoryPath;
                // #645: the mapping and its mode set live on InteractiveSessions so a test can assert
                // a property across every mode. Inline here, nothing could enumerate them.
                var grant = InteractiveSessionMaterializer.GrantForMode(request.Mode);
                if (grant == null)
                {
                    return Results.BadRequest(
                        $"Mode must be one of: {string.Join(", ", InteractiveSessionMaterializer.KnownModes)}.");
                }

                try
                {
                    using var guard = ConcurrencyGuard.AcquireRoomEventsWithin(directoryPath, TimeSpan.FromSeconds(2), "session mode update");
                    var bindingsFilePath = AerPaths.RoomBindingsFile(directoryPath);
                    var existingBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath).ConfigureAwait(true);
                    if (!existingBindings.TryGetValue(InteractiveSessionMaterializer.DefaultWorkerName, out var existingEntry))
                    {
                        return Results.NotFound();
                    }

                    var updatedBindings = new Dictionary<string, WorkerBindingConfigEntry>(existingBindings)
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = existingEntry with { PermissionGrant = grant }
                    };
                    await WorkerBindingConfigWriter.SaveToFileAsync(updatedBindings, bindingsFilePath).ConfigureAwait(true);

                    return Results.Ok();
                }
                catch (WorkflowLockedException ex)
                {
                    return Results.Json(
                        new { Error = $"Could not acquire room lock to update session mode: {ex.Message}. Retry the operation." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

            // #286: the POST above changes the mode but nothing let a client learn what's
            // currently active -- mode itself lives only in bindings.json's PermissionGrant (there's
            // no separate "CurrentMode" field), so this reverse-maps the persisted grant back to one
            // of the three canonical mode names the POST above can produce, or "custom" for a grant
            // that doesn't match any of them (e.g. one set directly via /api/sessions/start's own
            // PermissionGrant parameter, bypassing this mode vocabulary entirely).
            app.MapGet("/api/sessions/{sessionId}/mode", async (string sessionId) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var bindingsFilePath = AerPaths.RoomBindingsFile(resolved.Value.DirectoryPath);
                var existingBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath).ConfigureAwait(true);
                if (!existingBindings.TryGetValue(InteractiveSessionMaterializer.DefaultWorkerName, out var existingEntry))
                {
                    return Results.NotFound();
                }

                // #645: asked of the same mapping POST uses, rather than restating the three grants
                // here as this endpoint used to. What that second copy cost is recorded on
                // InteractiveSessionMaterializer.ModeForGrant.
                return Results.Ok(new
                {
                    Mode = InteractiveSessionMaterializer.ModeForGrant(existingEntry.PermissionGrant),
                });
            });

            // #286: "clear" (unlike compact) never talks to the vendor -- it's a purely local reset
            // so the *next* turn starts a genuinely fresh native session, mirroring exactly what
            // /api/sessions/start's own materialization does for a brand new session (same
            // fresh-GUID-per-adapter minting, VendorSessionEstablished reset to false so
            // ExecuteSessionTurnAsync's #285 resume-gating correctly picks `--session-id` over
            // `--resume` on that next turn instead of trying to resume an id the vendor never
            // established). Turns are cleared immediately so the UI reflects "cleared" without
            // waiting on any background work.
            app.MapPost("/api/sessions/{sessionId}/clear", async (string sessionId) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var (directoryPath, metadata) = resolved.Value;
                var freshVendorSessionId = string.Equals(metadata.CurrentAdapter, "claude", StringComparison.OrdinalIgnoreCase)
                    ? Guid.NewGuid().ToString()
                    : null;

                var cleared = metadata with
                {
                    Turns = [],
                    TurnCount = 0,
                    CurrentVendorSessionId = freshVendorSessionId,
                    VendorSessionEstablished = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                var metadataPath = Path.Combine(directoryPath, ".aer", AerPaths.RoomMetadataFileName);
                await InteractiveSessionMaterializer.SaveMetadataAsync(cleared, metadataPath).ConfigureAwait(true);

                return Results.Ok(cleared);
            });

            app.MapGet("/api/adapters/capabilities", async (string? adapter, string? workingDirectory, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var name = string.IsNullOrWhiteSpace(adapter) ? "claude" : adapter.Trim().ToLowerInvariant();
                if (!adapters.TryGetValue(name, out var workerAdapter))
                {
                    workerAdapter = adapters["claude"];
                }

                var capabilities = await workerAdapter.DiscoverCapabilitiesAsync(workingDirectory);
                return Results.Ok(capabilities);
            });

            app.MapPost("/api/sessions/{sessionId}/compact", async (string sessionId, RoomClient session, BindingsPathHolder pathHolder, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var (directoryPath, metadata) = resolved.Value;
                pathHolder.BindingsFilePath = AerPaths.RoomBindingsFile(directoryPath);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var compactMsg = "/compact Please provide a concise summary of our conversation so far, including all key requirements, code changes, decisions, and current progress.";
                        await ExecuteSessionTurnAsync(session, directoryPath, metadata, compactMsg, metadata.CurrentAdapter, metadata.Model, isInitial: false, broadcast.BroadcastStateAsync, adapters, broadcast.BroadcastSessionProgressAsync, forceHandoff: true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing session compact turn: {ex}");
                    }
                });

                return Results.Ok(new { SessionId = metadata.SessionId, Message = "Compacting session context in background." });
            });

            // M24 Phase 3 (#264): Known Projects Registry endpoints
            app.MapGet("/api/projects", async () =>
            {
                var projects = await KnownProjectsStore.LoadProjectsAsync();
                return Results.Ok(projects);
            });

            app.MapPost("/api/projects", async ([FromBody] RegisterProjectRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.BadRequest("Path is required.");
                }

                await KnownProjectsStore.AddOrUpdateProjectAsync(request.Path, request.FriendlyName);
                var projects = await KnownProjectsStore.LoadProjectsAsync();
                return Results.Ok(projects);
            });

            // Write active port to discovery file on startup
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var server = app.Services.GetRequiredService<IServer>();
                var addressesFeature = server.Features.Get<IServerAddressesFeature>();
                if (addressesFeature != null)
                {
                    var firstUrl = addressesFeature.Addresses.FirstOrDefault();
                    if (firstUrl != null)
                    {
                        var uri = new Uri(firstUrl);
                        var portFile = Path.Combine(aerDir, "daemon.port");
                        File.WriteAllText(portFile, uri.Port.ToString());

                        if (isRemote)
                        {
                            TryStartSidecar(uri.Port);
                        }
                    }
                }
            });

            // Windows doesn't reap child processes when the parent exits -- an orphaned sidecar
            // would keep holding its tsnet node and tailnet port. Covers both real shutdown and the
            // shutdown-then-respawn toggle (RemoteViewModel.ToggleRemoteAsync), since both go
            // through this same graceful-shutdown path.
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                try { sidecarProcess?.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            });

            await app.RunAsync();
            mutex?.Dispose();
        }

        // Session folders are only named "session-{sessionId}" when StartSessionRequest.RoomName is
        // omitted (the fallback name at MapPost "/api/sessions/start" above) -- a caller-supplied
        // RoomName (e.g. a human-readable title) produces a differently-named folder, so any lookup
        // by sessionId alone must not assume the fallback convention holds. Mirrors the scan
        // MapGet "/api/sessions" (list) already does per-directory, keyed by the persisted
        // SessionMetadata.SessionId instead of the folder name.
        // Review follow-up (issue #250's containment fix, applied here too): DirectoryPath is a
        // caller-supplied path reaching real filesystem mutation (archive/unarchive marker writes,
        // and delete's recursive Directory.Delete) via remote-reachable endpoints (mobile's
        // DaemonClient.deleteTask() included) -- an unchecked path here is a strictly worse version
        // of #250's RunTemplate RoomName traversal, since delete needs no traversal trick at all,
        // just any absolute path. Every fleet item this API surfaces is itself a direct child of
        // the record root (Directory.GetDirectories in the /api/rooms handler above), so requiring
        // the resolved path be contained within it costs nothing legitimate.
        // #333: this deliberately no longer accepts the legacy `tasks` root. Migration copies rather
        // than moves, so those directories still exist on disk -- but nothing enumerates them any
        // more, and a stale client path pointing at one would mutate an abandoned copy that no read
        // path will ever surface again, silently diverging from the live record. Narrowing a
        // containment check fails closed: the request is rejected instead of quietly doing the wrong
        // thing to the wrong directory.
        private static bool TryResolveManagedRoomDirectory(string directoryPath, out string resolvedPath)
        {
            resolvedPath = Path.GetFullPath(directoryPath);

            var baseRoomsDir = Path.GetFullPath(AerPaths.Rooms);

            return resolvedPath.StartsWith(baseRoomsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        /// <summary>#992: the turn-host endpoints serve only the hosted room, and "same room" has
        /// to survive separator/casing/trailing-slash differences between how the operator typed
        /// the path and how the host recorded it. OrdinalIgnoreCase matches the Windows-first
        /// reality of the desktop host; a false negative here only yields a 409 the caller can
        /// correct, never a mutation of the wrong room.</summary>
        private static bool PathsReferToSameDirectory(string a, string b)
        {
            var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gives <paramref name="roomDirectoryPath"/> its own copy of <paramref name="sourceBindingsFilePath"/>
        /// — decision 0056's register — replacing any previous copy, since re-binding is a per-run choice.
        /// </summary>
        /// <remarks>
        /// Written to a sibling temp file and moved into place rather than copied over the live one.
        /// #1230's second reader named the exposure this closes: once the room's copy IS the register, a
        /// process killed mid-copy leaves a truncated `bindings.json` where a previously good one was —
        /// strictly worse than the old behaviour, which could only orphan a disposable global slot.
        /// `File.Move(overwrite: true)` replaces atomically on both NTFS and POSIX, so a reader sees the
        /// old file or the new one and never a half-written one.
        /// <para>
        /// The same-file check is by full path, case-insensitively: a caller may legitimately pass the
        /// room's own copy back (it is the Run dialog's pre-fill after the first run), and copying a file
        /// onto itself is at best wasted work. It does not resolve symlinks — a source reaching this same
        /// file through a link would be copied rather than skipped, which is harmless here because the
        /// temp-and-move keeps that self-copy atomic too.
        /// </para>
        /// </remarks>
        private static void MaterializeRoomBindings(string roomDirectoryPath, string sourceBindingsFilePath)
        {
            var roomBindings = AerPaths.RoomBindingsFile(roomDirectoryPath);
            if (string.Equals(
                    Path.GetFullPath(roomBindings), Path.GetFullPath(sourceBindingsFilePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(roomDirectoryPath);

            // Unique per call, so two runs racing the same room cannot write each other's temp file.
            var staging = roomBindings + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(sourceBindingsFilePath, staging, overwrite: true);
                File.Move(staging, roomBindings, overwrite: true);
            }
            finally
            {
                if (File.Exists(staging))
                {
                    try { File.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static async Task<(string DirectoryPath, SessionMetadata Metadata)?> ResolveSessionAsync(string sessionId)
        {
            var baseRoomsDir = AerPaths.Rooms;
            if (!Directory.Exists(baseRoomsDir))
            {
                return null;
            }

            foreach (var dir in Directory.GetDirectories(baseRoomsDir))
            {
                var metadataPath = Path.Combine(dir, ".aer", AerPaths.RoomMetadataFileName);
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                SessionMetadata? metadata;
                try
                {
                    metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Inert today — this call passes no token — but stated rather than left to a future
                    // change that plumbs a request-abort token through and silently turns a cancellation
                    // into "skip this room and keep scanning". Same carve-out RoomRetentionSweep makes.
                    throw;
                }
                catch (Exception ex)
                {
                    // #1229: this scan reads EVERY room to find ONE session, so without this a single
                    // unreadable room — corrupt json, or a directory deleted between the File.Exists
                    // above and the open — threw out of the whole lookup and answered 500 for a
                    // session that was perfectly healthy. LoadMetadataAsync already retries the
                    // transient shapes (see its RetryOnSharingViolationAsync); what reaches here has
                    // outlasted that, so it is that room's problem and not this session's.
                    // Same rule /api/rooms already applies to the fleet: one bad item must not hide
                    // every other room.
                    Console.Error.WriteLine($"Error reading session metadata for '{dir}': {ex}");
                    continue;
                }

                if (metadata != null && metadata.SessionId == sessionId)
                {
                    return (dir, metadata);
                }
            }

            return null;
        }

        /// <summary>
        /// #341: appends a background turn failure to <c>.aer/turn-errors.log</c> in the session
        /// directory. <c>POST /api/sessions/send</c> answers before the turn runs, so nothing it
        /// returns can carry a later failure, and <c>Console.Error</c> dies with the process --
        /// leaving a stalled chat with no recoverable evidence anywhere. Best-effort by
        /// construction: this runs inside a catch, so it must never throw over the top of the
        /// original error.
        ///
        /// #828: the same gap exists for both room dispatch endpoints -- they answer 200 before
        /// their fire-and-forget dispatch body runs, and until now a failure there
        /// (e.g. this directory's <c>WorkflowLockedException</c>, swallowed by
        /// <see cref="RoomClient.RunAsync"/>/<see cref="RoomClient.DecideAsync"/>'s own
        /// <c>catch (AerFlowException)</c>) reached <c>Console.Error</c> and nowhere else. A single
        /// log convention rather than splitting by call site -- <paramref name="context"/> generalizes
        /// the chat call site's "message" label to whatever identifies the failed operation.
        /// <paramref name="error"/> is <c>object</c>, not <see cref="Exception"/>, so a
        /// <see cref="RoomClient.MutationOutcome"/>'s <c>ErrorMessage</c> string -- the only place
        /// most room-endpoint dispatch failures actually surface, since
        /// <see cref="RoomClient.RunAsync"/>/<see cref="RoomClient.DecideAsync"/>'s in-process
        /// fallback catches every <c>AerFlowException</c> itself and returns normally -- can be
        /// recorded without fabricating an exception instance to wrap it in.
        /// </summary>
        private static async Task AppendTurnErrorAsync(string directoryPath, string context, object error)
        {
            try
            {
                var aerDir = Path.Combine(directoryPath, ".aer");
                Directory.CreateDirectory(aerDir);
                var line = $"{DateTimeOffset.UtcNow:O}\tmessage={context.ReplaceLineEndings(" ")}\t{error}";
                await File.AppendAllTextAsync(Path.Combine(aerDir, "turn-errors.log"), line + Environment.NewLine).ConfigureAwait(false);
            }
            catch (Exception recordError)
            {
                Console.Error.WriteLine($"Could not persist session turn error: {recordError}");
            }
        }

        /// <summary>
        /// #1179: whether <paramref name="directoryPath"/>'s room is currently dormant, per its
        /// <c>room.jsonl</c> -- same read as the turn-throttles endpoint's dormancy check above
        /// (<c>RoomEventLogReader</c> -&gt; <c>RoomProjector.Project</c> -&gt; <c>RoomState.IsDormant</c>).
        /// An absent log (no dormancy transition has ever been recorded, e.g. a brand-new room) reads
        /// as not dormant, following <see cref="RoomProjectionLoader.LoadJournalStateAsync"/>'s own
        /// absence handling -- never a throw.
        /// </summary>
        private static async Task<bool> IsRoomDormantAsync(string directoryPath)
        {
            var roomLogPath = Path.Combine(directoryPath, "room.jsonl");
            if (!File.Exists(roomLogPath))
            {
                return false;
            }

            var reader = new RoomEventLogReader(roomLogPath);
            var events = await reader.ReadAllRoomEventsAsync().ConfigureAwait(false);
            return RoomProjector.Project(events).IsDormant;
        }

        /// <summary>
        /// #393: one turn at a time per session directory. Every turn does
        /// <see cref="RoomClient.LoadAsync"/>, branches on the projection, and -- in the
        /// re-materialize branch -- deletes <c>snapshot.json</c>/<c>flow.jsonl</c>/<c>artifacts</c>
        /// *before* <c>RunAsync</c> takes Flow's per-room-directory lock, so that read-then-delete
        /// window sits outside every existing lock. Turns also run fire-and-forget behind an
        /// already-returned 200, so two overlapping <c>POST /api/sessions/send</c> calls for the same
        /// session genuinely interleave there. <see cref="IsSessionSafeToReMaterialize"/> (#354) makes
        /// the delete refuse in the unsafe states; this closes the ordering hole itself.
        ///
        /// #590: also the daemon's single-writer-per-vendor-session lock. A chat turn persists its
        /// vendor <c>SessionId</c> into <c>bindings.json</c> (<see cref="ExecuteSessionTurnCoreAsync"/>),
        /// and both room dispatch endpoints dispatch whatever that file says with no lock of their own
        /// (see <see cref="SessionDirectoryDispatchSerializationTests"/> for guard details),
        /// so this in-process lock is the only thing that serializes concurrent dispatches. Keyed by
        /// directory rather than the vendor session id itself because the id is re-minted on handoff
        /// while the directory is stable, and because this lock also serialises the
        /// <c>room.json</c> read-modify-write an id-keyed lock would miss.
        ///
        /// Keyed by the session's room directory: that is what the deletes target, it matches Flow's
        /// own lock granularity, and it is the one identifier all three call sites (create, send,
        /// compact) share. Keys are normalised and compared case-insensitively so two spellings of the
        /// same directory cannot end up with two different semaphores -- on a case-sensitive
        /// filesystem that can only ever over-serialise two genuinely distinct directories, which is
        /// safe; under-locking is the bug worth avoiding.
        ///
        /// Never removed: one <see cref="SemaphoreSlim"/> per session seen per daemon lifetime is
        /// bounded and tiny, and safe removal would need refcounting to avoid disposing a semaphore a
        /// waiter still holds. #335 landed its keyed host state without absorbing this, and
        /// deliberately so -- the two have different lifetimes. A hosted run exists only while its
        /// pump is in flight and is removed by that run; a turn lock must outlive every turn, because
        /// its whole job is to be found again by the next one.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionTurnLocks =
            new(AerPaths.RecordKeyComparer);

        /// <summary>
        /// Canonical <see cref="SessionTurnLocks"/> key for a session's directory. Delegates to
        /// <see cref="AerPaths.RecordKey"/> rather than normalising here: #335 keys host state on the
        /// same directories, and two normalisers that disagree about whether two spellings are one
        /// record would make one of the two primitives silently miss.
        /// </summary>
        internal static string SessionTurnLockKey(string directoryPath) => AerPaths.RecordKey(directoryPath);

        /// <summary>
        /// The one semaphore guarding turns for <paramref name="directoryPath"/>. Two spellings of the
        /// same directory must return the same instance -- if they ever return two, the lock silently
        /// becomes a no-op and every guarantee above is void, which is why this is reachable from tests.
        /// </summary>
        internal static SemaphoreSlim SessionTurnLockFor(string directoryPath) =>
            SessionTurnLocks.GetOrAdd(SessionTurnLockKey(directoryPath), _ => new SemaphoreSlim(1, 1));

        private static async Task ExecuteSessionTurnAsync(
            RoomClient session,
            string directoryPath,
            SessionMetadata metadata,
            string userMessage,
            string? requestAdapter,
            string? requestModel,
            bool isInitial,
            Func<RoomProjection, string?, Task> broadcastStateAsync,
            IReadOnlyDictionary<string, IWorkerAdapter> adapters,
            Func<string, string, WorkerProgressEvent, Task> broadcastSessionProgressAsync,
            bool forceHandoff = false)
        {
            var turnLock = SessionTurnLockFor(directoryPath);
            await turnLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // #393: the caller's `metadata` was read before the lock -- by the send endpoint well
                // before this turn was queued, or (create) built in-memory by materialization. Holding
                // it across the wait would serialise execution while still acting on a pre-wait
                // snapshot: a turn queued behind another would see the *previous* turn's
                // VendorSessionEstablished/CurrentVendorSessionId and mint a fresh vendor id instead of
                // resuming -- reopening #285's wedge, now concurrency-triggered -- and would append to
                // a stale Turns transcript, dropping the turn the other one wrote. Re-read inside the
                // lock so the turn acts on state the previous turn actually committed. Materialization
                // persists room.json before returning, so the on-disk copy is authoritative for the
                // create path too; the parameter remains the fallback for an unreadable file.
                var metadataPath = Path.Combine(directoryPath, ".aer", AerPaths.RoomMetadataFileName);
                var current = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath).ConfigureAwait(false);

                await ExecuteSessionTurnCoreAsync(
                    session,
                    directoryPath,
                    current ?? metadata,
                    userMessage,
                    requestAdapter,
                    requestModel,
                    isInitial,
                    broadcastStateAsync,
                    adapters,
                    broadcastSessionProgressAsync,
                    forceHandoff).ConfigureAwait(false);
            }
            finally
            {
                // 0022 §5: a pending permission dies with its turn. Revoked BEFORE the lock is
                // released and room-scoped on purpose — while this turn's lock is held no next
                // turn can raise a fresh ask, so every registry entry for this room is a leftover
                // of the turn that just ended, and the room filter cannot catch a successor's ask.
                // Failure here must never wedge the release below (same reason as its comment).
                try
                {
                    await RevokePendingGatesForRoomAsync(
                        directoryPath,
                        executionIdFilter: null,
                        "turn_ended",
                        async (proj, dir) => await broadcastStateAsync(proj, dir).ConfigureAwait(false),
                        async () => (await session.LoadAsync(directoryPath).ConfigureAwait(false)).Projection).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"turn-end gate revocation failed for '{directoryPath}': {ex.GetType().Name}: {ex.Message}");
                }

                // Turns throw in several places and the fire-and-forget catch sits outside this method,
                // so an un-released semaphore would wedge the session permanently.
                turnLock.Release();
            }
        }

        private static async Task ExecuteSessionTurnCoreAsync(
            RoomClient session,
            string directoryPath,
            SessionMetadata metadata,
            string userMessage,
            string? requestAdapter,
            string? requestModel,
            bool isInitial,
            Func<RoomProjection, string?, Task> broadcastStateAsync,
            IReadOnlyDictionary<string, IWorkerAdapter> adapters,
            Func<string, string, WorkerProgressEvent, Task> broadcastSessionProgressAsync,
            bool forceHandoff = false)
        {
            var targetAdapter = string.IsNullOrWhiteSpace(requestAdapter) ? metadata.CurrentAdapter : requestAdapter.Trim().ToLowerInvariant();
            bool isVendorChange = !string.Equals(targetAdapter, metadata.CurrentAdapter, StringComparison.OrdinalIgnoreCase);
            bool isCeilingReached = metadata.TurnCount >= metadata.SafetyCeiling;
            // Compact (POST /api/sessions/{id}/compact) forces this branch even for a same-vendor,
            // under-ceiling turn -- it must actually synthesize a summary and start a fresh native
            // session, not just forward "/compact" as an ordinary resumed message to the vendor's own
            // (unverified, vendor-owned) slash-command handling. See issue #263's original rationale.
            bool handoff = isVendorChange || isCeilingReached || forceHandoff;

            string promptTemplate;
            bool resumeSession;
            string? vendorSessionId;

            if (handoff)
            {
                promptTemplate = InteractiveSessionMaterializer.BuildTurnPrompt(
                    InteractiveSessionMaterializer.SynthesizeContextSummary(metadata.Turns, userMessage));
                resumeSession = false;
                vendorSessionId = string.Equals(targetAdapter, "claude", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString() : null;
            }
            else
            {
                // #650: BuildTurnPrompt, not the bare message. This value overwrites the materialized
                // PromptTemplate on every turn, so an ask appended only at materialization reaches no
                // vendor — measured. The chat contract no longer requires response.md, which makes the
                // prompt the only thing that asks for it, and on a non-streaming vendor the file is the
                // only channel an answer can arrive on.
                promptTemplate = InteractiveSessionMaterializer.BuildTurnPrompt(userMessage);
                // #285: CurrentVendorSessionId is minted client-side at materialization time, before
                // the vendor CLI has ever heard of it -- it's non-null from turn zero, so "isInitial"
                // was standing in for "has the vendor actually established this id" and is wrong
                // whenever a session starts with no InitialMessage (the normal chat-page flow): the
                // very first /api/sessions/send call had isInitial=false and went straight to
                // `--resume <unestablished-guid>`, which claude rejects outright ("No conversation
                // found"), permanently wedging every later turn on the same dead id. Gate on whether a
                // turn has actually succeeded against this id instead.
                resumeSession = !isInitial && metadata.VendorSessionEstablished;
                vendorSessionId = metadata.CurrentVendorSessionId ?? (string.Equals(targetAdapter, "claude", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString() : null);
            }

            var logFilePath = string.Equals(targetAdapter, "agy", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directoryPath, ".aer", "agy-log.txt")
                : null;

            var bindingsFilePath = AerPaths.RoomBindingsFile(directoryPath);

            WorkerBindingConfigEntry updatedEntry;
            {
                using var bindingsGuard = ConcurrencyGuard.AcquireRoomEventsWithin(directoryPath, TimeSpan.FromSeconds(2), "per-turn bindings rewrite");
                var existingBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath).ConfigureAwait(false);

                WorkerBindingConfigEntry? existingEntry = existingBindings.TryGetValue(InteractiveSessionMaterializer.DefaultWorkerName, out var e) ? e : null;

                // Always the canonical shape, never the persisted one (#650). AER owns this contract and
                // an operator never authors it, so there is nothing in a session's own copy worth
                // preserving — while reading it back would keep every session materialized before this
                // change requiring response.md, and so keep classifying every one of its turns Failed.
                var contract = InteractiveSessionMaterializer.ChatWorkerContract;

                var grant = existingEntry?.PermissionGrant ?? InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory(metadata.WorkingDirectory);

                updatedEntry = new WorkerBindingConfigEntry(
                    Adapter: targetAdapter,
                    Contract: contract,
                    PromptTemplate: promptTemplate,
                    Timeout: TimeSpan.FromMinutes(10),
                    Model: requestModel ?? metadata.Model,
                    PermissionGrant: grant,
                    // #407: a directory-less session runs in its own dir under ~/.aer/rooms/, not the
                    // inherited daemon/app cwd. The grant above is still derived from metadata.WorkingDirectory
                    // (null -> fail-closed), so this run-dir fallback hardens where it starts without widening
                    // what it may do.
                    WorkingDirectory: InteractiveSessionMaterializer.ResolveRunDirectory(metadata.WorkingDirectory, metadata.RoomDirectoryPath),
                    SessionId: vendorSessionId,
                    ResumeSession: resumeSession,
                    // #1088: agy joins claude here. Both stream structured events on stdout under their own
                    // grammar (claude `--output-format stream-json --verbose`; agy `--output-format stream-json`,
                    // no `--verbose`), and each adapter's TryParseProgressEvent parses its own envelope. Turning
                    // this on fills rawStdoutCapture and drives the progress pump for agy; the answer still comes
                    // from the output file and the conversation-id from the log scrape, both unchanged.
                    StreamJson: string.Equals(targetAdapter, "claude", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(targetAdapter, "agy", StringComparison.OrdinalIgnoreCase),
                    LogFilePath: logFilePath,
                    // #445: the ONE binding that opts into the runtime conversational gate. An interactive
                    // turn is the only dispatch shape with a human on the other end to answer an ask -- the
                    // anchor step's own binding (written at materialization) deliberately does not set it,
                    // so an ungranted capability there still fails closed exactly as it does in a one-shot
                    // run. See WorkerInvocation.EnablePermissionGate.
                    EnablePermissionGate: true);

                // #285: must start from the existing bindings, not a fresh dictionary containing only
                // "chat-worker" -- a full replacement here silently dropped the anchor step's own
                // binding entry (written once at materialization, never touched by any per-turn
                // rewrite) after the very first turn, leaving "turn-anchor-worker" unresolvable and the
                // anchor step's dispatch throwing UnresolvedWorkerException deep inside the pump. That
                // exception was itself silently swallowed (RoomClient.RunAsync's in-process fallback
                // catches AerFlowException into an unchecked MutationOutcome, and neither call site
                // below checked it) -- chat would still succeed before the pump ever reached the
                // now-unresolvable anchor, so the turn looked like it worked right up until the anchor
                // never dispatched and never paused, wedging every later turn's Supersede target.
                var newBindings = new Dictionary<string, WorkerBindingConfigEntry>(existingBindings)
                {
                    [InteractiveSessionMaterializer.DefaultWorkerName] = updatedEntry
                };

                await WorkerBindingConfigWriter.SaveToFileAsync(newBindings, bindingsFilePath).ConfigureAwait(false);
            }

            var workflowFilePath = Path.Combine(directoryPath, "workflow.json");

            // M24 Phase 1's live in-turn streaming: only worth the stdout-capture cost (Aer.Flow's
            // CoreDispatcher only captures stdout at all when a target's OnStdoutLine is non-null)
            // when this turn actually requested a structured streaming format. The raw-line callback
            // below runs synchronously on aer-core's native callback thread, under CoreDispatcher's
            // own lock (see CoreDispatcher.cs's StdoutChunk handling) -- it must never block or do
            // real work, so it only enqueues onto a bounded channel and returns immediately. A
            // separate pump task drains that channel, does the (adapter-owned, possibly
            // vendor-specific) parse via TryParseProgressEvent, and awaits the WebSocket broadcast in
            // order, entirely off that native thread.
            Action<string, string>? onWorkerStdoutLine = null;
            Channel<string>? progressLines = null;
            Task? progressPumpTask = null;
            // #285: a failed execution's stderr never reaches this process (aer-core's P/Invoke
            // boundary doesn't surface it), but a failed `claude --output-format stream-json` call
            // still prints its error as the final stdout line (`{"type":"result",...,"errors":[...]}`)
            // before exiting non-zero -- captured here so a failure stops looking like silence.
            var rawStdoutCapture = new StringBuilder();

            // #545 fix, corrected twice: a wall-clock last-write-time comparison (the first attempt)
            // does not discriminate in a fast in-process test, where turn 1 and turn 2 can complete
            // within the same millisecond -- any tolerance wide enough to survive real clock/filesystem
            // resolution is also wide enough for a stale file from turn 1 to look "fresh" relative to
            // turn 2. Deleting any pre-existing log file before THIS turn's own dispatch removes the
            // ambiguity entirely: afterward, the file can only exist if THIS turn's own agy process
            // wrote it. See the log-scrape block below for how this is used.
            if (string.Equals(targetAdapter, "agy", StringComparison.OrdinalIgnoreCase) && logFilePath != null)
            {
                try
                {
                    File.Delete(logFilePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            if (updatedEntry.StreamJson && adapters.TryGetValue(targetAdapter, out var streamingAdapter))
            {
                var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
                progressLines = channel;
                onWorkerStdoutLine = (_, line) =>
                {
                    channel.Writer.TryWrite(line);
                    rawStdoutCapture.AppendLine(line);
                };

                progressPumpTask = Task.Run(async () =>
                {
                    await foreach (var line in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        if (streamingAdapter.TryParseProgressEvent(line, out var progressEvent) && progressEvent is not null)
                        {
                            await broadcastSessionProgressAsync(directoryPath, InteractiveSessionMaterializer.DefaultStepId, progressEvent).ConfigureAwait(false);
                        }
                    }
                });
            }

            await using var doorbell = new DoorbellMonitor(
                directoryPath, targetAdapter, vendorSessionId,
                async dir => (await session.LoadAsync(dir).ConfigureAwait(false)).Projection,
                broadcastStateAsync);

            try
            {
                if (isInitial)
                {
                    var runOutcome = await session.RunAsync(directoryPath, workflowFilePath, bindingsFilePath, onWorkerStdoutLine: onWorkerStdoutLine, settleOnVendorExhaustion: true).ConfigureAwait(false);
                    if (runOutcome.ErrorMessage is { } runError)
                    {
                        // #285: RunAsync's in-process fallback catches AerFlowException into an
                        // unchecked MutationOutcome -- an unresolvable binding or similar dispatch
                        // failure would otherwise vanish silently here exactly as it did before this
                        // check existed, leaving whatever the pump had already dispatched (e.g. a
                        // successful "chat" execution reached before the pump hit the failure) looking
                        // like a complete, healthy turn.
                        throw new InvalidOperationException($"Chat turn run failed: {runError}");
                    }
                }
                else
                {
                    // #285: "chat" superseding itself is spec-illegal (§17.1 -- a Supersede target
                    // must be a distinct transitive ancestor; a single self-referencing step has
                    // none), which is why every turn after the first silently no-opped -- the
                    // validator's rejection was swallowed by DecideAsync's in-process fallback, and
                    // ExecuteSessionTurnAsync fell through to re-read the *previous* turn's stale
                    // response.md as if it were a fresh answer. InteractiveSessionMaterializer now
                    // builds a two-step DAG: "chat" itself declares no PausePoint (so a successful
                    // turn flows straight through, uninterrupted), and a downstream "turn-anchor" step
                    // (DependsOn: [chat]) declares the PausePoint with SupersedeTargets: [chat] --
                    // exactly the shape spec §17.5's own Architect/Critic example uses. Anchor sitting
                    // Paused is what makes a Supersede against "chat" legal; its own successful rerun
                    // (triggered automatically by §11.3 condition 2 once chat's new execution
                    // succeeds) lands it paused again, ready for the next turn.
                    var currentOutcome = await session.LoadAsync(directoryPath).ConfigureAwait(false);
                    var anchorState = currentOutcome.Projection?.State.Steps
                        .SingleOrDefault(s => s.StepId.Value == InteractiveSessionMaterializer.AnchorStepId);

                    if (anchorState is { Status: StepStatus.Paused, LatestExecutionId: { } anchorExecutionId })
                    {
                        // Ordinary continuation (including handoff turns, which just carry a
                        // different promptTemplate/vendorSessionId computed above): supply this
                        // turn's message as the mandatory supplementary human-tier artifact (§17.3)
                        // and Supersede "chat" via anchor's currently-paused execution.
                        var messageFilePath = Path.Combine(directoryPath, ".aer", "pending-turn-message.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(messageFilePath)!);
                        await File.WriteAllTextAsync(messageFilePath, userMessage).ConfigureAwait(false);

                        var decideOutcome = await session.DecideAsync(
                            directoryPath,
                            new StepId(InteractiveSessionMaterializer.AnchorStepId),
                            anchorExecutionId,
                            DecisionType.Supersede,
                            targetStepId: new StepId(InteractiveSessionMaterializer.DefaultStepId),
                            revisionFilePath: messageFilePath,
                            supplementaryWorker: "human",
                            supplementaryOutputName: "message.txt",
                            onWorkerStdoutLine: onWorkerStdoutLine,
                            settleOnVendorExhaustion: true).ConfigureAwait(false);

                        if (decideOutcome.ErrorMessage is { } decideError)
                        {
                            throw new InvalidOperationException($"Chat turn decision (Supersede) was rejected: {decideError}");
                        }
                    }
                    else
                    {
                        // #354: reaching here means the anchor is not observably Paused -- but the old
                        // code treated that as an unconditional "nothing of value exists, delete and
                        // re-run", a two-way test over a multi-state DAG. Re-materializing wipes this
                        // room's snapshot.json / flow.jsonl / artifacts, so it is only safe when the
                        // flow genuinely has no live state to lose or corrupt (see
                        // IsSessionSafeToReMaterialize): nothing Running (the anchor's own rerun is
                        // auto-triggered by §11.3 condition 2 once "chat" succeeds, so there is a
                        // Running window), nothing Paused (a continuation a stale projection can
                        // momentarily hide), and no already-succeeded "chat" still awaiting its anchor.
                        // When that holds it is the very first turn of a no-InitialMessage session, a
                        // first turn that failed outright, or the documented mid-conversation-failure
                        // recovery -- all cases where only Flow's internal snapshot/log/artifacts are
                        // replaced while SessionMetadata's own Turns transcript and
                        // VendorSessionEstablished (which carry real continuity) stay untouched.
                        // Otherwise a live session's entire event log would be destroyed: refuse and
                        // surface it rather than betting the wrong way.
                        if (!IsSessionSafeToReMaterialize(currentOutcome.Projection?.State.Steps, metadata.Turns.Count))
                        {
                            throw new InvalidOperationException(
                                "Chat turn found the session anchor not resolved to a paused state, but " +
                                "the flow still holds live state (a step is Running or Paused, or a " +
                                "succeeded \"chat\" step is awaiting its anchor). Refusing to " +
                                "re-materialize, which would delete this session's live flow log, " +
                                "snapshot and artifacts (#354). Retry once the current turn has settled.");
                        }

                        var snapshotPath = Path.Combine(directoryPath, "snapshot.json");
                        var flowLogPath = Path.Combine(directoryPath, "flow.jsonl");
                        var artifactsPath = Path.Combine(directoryPath, ArtifactManager.ArtifactsDirectoryName);
                        if (File.Exists(snapshotPath))
                        {
                            File.Delete(snapshotPath);
                        }

                        if (File.Exists(flowLogPath))
                        {
                            File.Delete(flowLogPath);
                        }

                        if (Directory.Exists(artifactsPath))
                        {
                            Directory.Delete(artifactsPath, recursive: true);
                        }

                        var runOutcome = await session.RunAsync(directoryPath, workflowFilePath, bindingsFilePath, onWorkerStdoutLine: onWorkerStdoutLine, settleOnVendorExhaustion: true).ConfigureAwait(false);
                        if (runOutcome.ErrorMessage is { } runError)
                        {
                            // #285: RunAsync's in-process fallback catches AerFlowException into an
                            // unchecked MutationOutcome -- an unresolvable binding or similar dispatch
                            // failure would otherwise vanish silently here exactly as it did before
                            // this check existed, leaving whatever the pump had already dispatched
                            // (e.g. a successful "chat" execution reached before the pump hit the
                            // failure) looking like a complete, healthy turn.
                            throw new InvalidOperationException($"Chat turn run failed: {runError}");
                        }
                    }
                }
            }
            finally
            {
                if (progressLines is not null)
                {
                    progressLines.Writer.Complete();
                    await progressPumpTask!.ConfigureAwait(false);
                }
            }

            // Capture agy conversation ID if turn 1 for agy
            if (string.Equals(targetAdapter, "agy", StringComparison.OrdinalIgnoreCase) && vendorSessionId == null && logFilePath != null && File.Exists(logFilePath))
            {
                try
                {
                    var logText = await File.ReadAllTextAsync(logFilePath).ConfigureAwait(false);
                    // #837: agy's log line trails the id with a comma (`conversation=<uuid>, ...`);
                    // a non-whitespace class captured it into the stored id.
                    var match = System.Text.RegularExpressions.Regex.Match(logText, @"conversation=([\w-]+)");
                    if (match.Success)
                    {
                        vendorSessionId = match.Groups[1].Value;
                    }
                }
                catch { }
            }

            // #545, corrected after a second review pass (both agy's own independent review and a
            // reconciled empirical repro confirmed this, not just static reading): THIS turn's own
            // establishment fact for agy, computed independently of vendorSessionId's value. That
            // variable is deliberately left untouched by the turn-1-only scrape above on every later
            // turn (a real vendor-assigned id, once minted, is never silently replaced) -- which
            // means on turn 2+ it is already non-null before this turn even runs (carried over via
            // metadata.CurrentVendorSessionId). Keying establishment to "vendorSessionId != null"
            // therefore measured whether ANY turn ever succeeded, not whether THIS one did: a turn
            // that produced nothing at all was still reported established, with its real error
            // silently discarded. Deleting any pre-existing log file before this turn's own dispatch
            // (above) is what makes a plain existence check here mean "THIS turn produced one" --
            // an earlier version of this fix compared the file's last-write time against a
            // pre-dispatch timestamp instead, which does not discriminate when turn 1 and turn 2
            // complete within the same wall-clock second (routine in an in-process test).
            var agyLogFreshThisTurn = false;
            if (string.Equals(targetAdapter, "agy", StringComparison.OrdinalIgnoreCase) && logFilePath != null && File.Exists(logFilePath))
            {
                try
                {
                    var freshLogText = await File.ReadAllTextAsync(logFilePath).ConfigureAwait(false);
                    // #837: same trailing-comma shape as the scrape above.
                    agyLogFreshThisTurn = System.Text.RegularExpressions.Regex.IsMatch(freshLogText, @"conversation=([\w-]+)");
                }
                catch { }
            }

            // Read assistant response
            string? assistantResponse = null;
            var finalOutcome = await session.LoadAsync(directoryPath).ConfigureAwait(false);
            if (finalOutcome.Projection is { } finalProj)
            {
                var latestExecution = finalProj.Lineage.Executions.LastOrDefault(ex => ex.StepId?.Value == InteractiveSessionMaterializer.DefaultStepId);
                if (latestExecution != null)
                {
                    var outputDir = ArtifactManager.ResolveOutputDirectory(Path.Combine(directoryPath, ArtifactManager.ArtifactsDirectoryName), latestExecution.ExecutionId);
                    var responseFile = Path.Combine(outputDir, InteractiveSessionMaterializer.DefaultOutputFileName);
                    if (File.Exists(responseFile))
                    {
                        assistantResponse = await File.ReadAllTextAsync(responseFile).ConfigureAwait(false);
                    }
                }
                await broadcastStateAsync(finalProj, directoryPath).ConfigureAwait(false);
            }

            // #534: recover the answer from the vendor's own structured result when no output file
            // was produced. A session with no working directory gets an all-deny grant
            // (InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory, fail-closed per #321),
            // which becomes `--disallowedTools Edit,Write,NotebookEdit,Bash` -- so the worker CANNOT
            // write response.md, while its contract declares exactly that output. Both halves are
            // deliberate; together they discarded every answer. Measured identically on
            // claude-opus-5 and claude-haiku-4-5, so it is not a model declining a tool it had.
            var fileResponse = assistantResponse;
            assistantResponse ??= TryExtractAssistantAnswer(rawStdoutCapture.ToString());

            // #537 / #545: keyed to whether the TURN SUCCEEDED, not to whether an output file was written.
            //
            // This feeds VendorSessionEstablished below, which decides whether the next turn passes
            // `--resume` (or `--conversation` for agy). It used to key off the output file, which is a
            // permission outcome rather than a session one -- so a directory-less chat, which can never
            // write the file (all-deny grant, fail-closed per #321), was never marked established, never
            // resumed, and carried no memory between turns. Measured before changing: see
            // SessionContinuityWithoutOutputFileTests.
            //
            // Establishment signal per vendor:
            // - `claude`: `assistantResponse` is non-null when the vendor produced an answer via the output
            //   file or via the structured result on stdout (#534).
            // - `gemini` (`agy`): `assistantResponse != null` (output file written) OR `agyLogFreshThisTurn`
            //   (a valid `conversation=` id appeared in agy's log file, written since THIS turn's own
            //   dispatch started -- not merely present at all, which `vendorSessionId != null` would also
            //   be true for on turn 2+ since that variable is never cleared between turns; see
            //   `agyLogFreshThisTurn`'s own comment above for why the two are not interchangeable).
            //
            // A turn that genuinely failed leaves these signals null/false and stays unestablished, which
            // is what #285's resume-gating regression tests pin.
            //
            // One edge is deliberately left unestablished: a vendor that succeeds while producing no
            // answer at all, so the next turn re-sends `--session-id` rather than `--resume`.
            // Whether that is the safe direction is an OPEN QUESTION, not a fact -- the register
            // (see vendor-doc-audit.md) measured
            // sequential reuse of an existing id as REFUSED, which would make the re-send fail
            // rather than retry harmlessly. It is unreconciled because the pre-fix symptom was
            // silent memory loss rather than a hard turn-2 failure, and #537 never verified turn 2
            // end to end. Tracked as #546; do not restate either side as settled until it measures.
            var establishedThisTurn = assistantResponse != null || agyLogFreshThisTurn;
            var errorMessage = establishedThisTurn ? null : TryExtractVendorErrorMessage(rawStdoutCapture.ToString());

            // 0026 §4: a failed turn is consulted against the resolved adapter's failure classifier
            // BEFORE it is left as a generic error -- exhausted plan/quota is a STATE with a reset
            // time, never a failure (#1180). Reuses the same classifiers the dispatch path already
            // has (claude's typed `errorCode: "credits_required"` match, #1115; agy's result-envelope
            // "Resets in" prose, #1128) rather than re-implementing either. The stderr tail is always
            // null here -- unlike the dispatch path's CoreDispatchResult, this session seam never
            // captures the failed process's stderr (aer-core's P/Invoke boundary doesn't surface it,
            // see rawStdoutCapture's comment above) -- so only the stdout tail is offered, which is
            // the only tail this seam has. Claim scope (0026 Rests-on): agy's refusal prose is
            // measured on stdout (dispatch path, #1128); claude's typed code is measured on the
            // dispatch path (#1115) but its INTERACTIVE refusal channel is still unmeasured -- this
            // wires the same classifier honestly without claiming a live observation on this seam.
            var isExhausted = false;
            DateTimeOffset? exhaustedUntil = null;
            if (!establishedThisTurn
                && adapters.TryGetValue(targetAdapter, out var resolvedAdapter)
                && resolvedAdapter.TryClassifyFailure(
                    null, rawStdoutCapture.ToString(), TimeProvider.System, out var failureClassification, out var retryNotBefore)
                && failureClassification == FailureClassification.ExhaustedUntil)
            {
                isExhausted = true;
                exhaustedUntil = retryNotBefore;
            }

            var newTurnIndex = metadata.TurnCount + 1;
            var turn = new SessionTurn(
                TurnIndex: newTurnIndex,
                Vendor: targetAdapter,
                HumanMessage: userMessage,
                AssistantResponse: assistantResponse,
                ExecutedAt: DateTimeOffset.UtcNow,
                NativeSessionResumed: resumeSession,
                VendorHandoffSynthesized: handoff,
                ErrorMessage: errorMessage,
                IsExhausted: isExhausted,
                ExhaustedUntil: exhaustedUntil);

            var updatedTurns = new List<SessionTurn>(metadata.Turns) { turn };
            var updatedTurnCount = isCeilingReached ? 1 : newTurnIndex;

            var updatedMetadata = metadata with
            {
                CurrentAdapter = targetAdapter,
                CurrentVendorSessionId = vendorSessionId,
                Model = requestModel ?? metadata.Model,
                TurnCount = updatedTurnCount,
                UpdatedAt = DateTimeOffset.UtcNow,
                Turns = updatedTurns,
                // #285: a handoff mints a brand-new vendorSessionId, so prior establishment doesn't
                // carry over -- only this turn's own outcome counts for it. Otherwise, once
                // established stays established even if a later turn fails for an unrelated reason
                // (rate limit, transient network blip) -- the id itself is still real and resumable.
                VendorSessionEstablished = handoff ? establishedThisTurn : (metadata.VendorSessionEstablished || establishedThisTurn)
            };

            await InteractiveSessionMaterializer.SaveMetadataAsync(updatedMetadata, Path.Combine(directoryPath, ".aer", AerPaths.RoomMetadataFileName)).ConfigureAwait(false);
        }

        /// <summary>
        /// #354: decides whether a chat turn that could not resolve its <c>turn-anchor</c> to a Paused
        /// state may safely re-materialize the session -- delete <c>flow.jsonl</c>/<c>snapshot.json</c>/
        /// <c>artifacts</c> and re-run from scratch. Only true when the flow has no live state to lose
        /// or corrupt:
        /// <list type="bullet">
        /// <item>no step is <see cref="StepStatus.Running"/> -- the anchor's own rerun (auto-triggered
        /// by spec §11.3 condition 2 once <c>chat</c> succeeds) may be in flight, and deleting races a
        /// live write;</item>
        /// <item>no step is <see cref="StepStatus.Paused"/> -- a paused step is a continuation that
        /// should be Superseded, not wiped, and a stale projection can momentarily hide a real paused
        /// anchor;</item>
        /// <item>the <c>chat</c> step is not <see cref="StepStatus.Succeeded"/> -- a succeeded chat
        /// whose anchor rerun simply hasn't fired yet is a healthy turn, not a stuck one.</item>
        /// </list>
        /// A null/empty projection is "nothing of value" only for a brand-new session (no recorded
        /// turns); for an established one it means a lagging or failed read, where deleting is data
        /// loss. This is a decision over a single state snapshot: it makes the delete refuse in the
        /// unsafe states, but it cannot by itself close the underlying read-then-delete race -- that
        /// is now closed by <see cref="SessionTurnLocks"/> (#393), which serialises turns per session
        /// directory so the read, the branch and the delete can no longer interleave with another
        /// turn. The guarantee this check gives is still by construction, not by a test that could
        /// deterministically reproduce a live Running anchor against a synchronous stub.
        /// </summary>
        internal static bool IsSessionSafeToReMaterialize(IReadOnlyList<StepState>? steps, int recordedTurnCount)
        {
            if (steps is null || steps.Count == 0)
            {
                return recordedTurnCount == 0;
            }

            if (steps.Any(s => s.Status is StepStatus.Running or StepStatus.Paused))
            {
                return false;
            }

            var chatStep = steps.SingleOrDefault(s => s.StepId.Value == InteractiveSessionMaterializer.DefaultStepId);
            return chatStep?.Status is not StepStatus.Succeeded;
        }

        /// <summary>
        /// Best-effort extraction of a human-readable failure reason from a failed
        /// <c>claude --output-format stream-json</c> turn's captured stdout (#285). A failed turn's
        /// final line is a <c>{"type":"result","is_error":true,"errors":[...]}</c> object (confirmed
        /// live: <c>--resume</c> of an unestablished session id prints exactly
        /// <c>"No conversation found with session ID: &lt;guid&gt;"</c> this way, on stdout, not
        /// stderr) -- scanned from the end since it's always the last line the CLI writes before
        /// exiting. Falls back to the raw last non-empty line, then a generic message, so a caller
        /// never has to null-check this to render *something*.
        /// </summary>
        /// <summary>
        /// The answer a SUCCESSFUL turn put in its structured result, for when no output file was
        /// written (#534). Returns <c>null</c> when there is no such answer to recover.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mirror of <see cref="TryExtractVendorErrorMessage"/>, over the same
        /// <c>{"type":"result",...}</c> line and scanned from the end for the same reason. The two
        /// are separated by exactly one condition — <c>is_error</c> — and conflating them would
        /// render a vendor error as though the assistant had said it, which is why
        /// <c>SessionAnswerWithoutOutputFileTests</c> pins that polarity explicitly.
        /// </para>
        /// <para>
        /// <b>Scope, stated rather than implied:</b> this reads the STRUCTURED result line only, so
        /// it covers `claude` (dispatched with <c>--output-format stream-json</c>, see
        /// <c>StreamJson</c> above) and does NOT cover a text-mode vendor such as `agy`, whose
        /// stdout is prose with no result envelope. Recovering an answer there would mean treating
        /// arbitrary stdout as the reply, which can put diagnostics and warnings into a chat bubble.
        /// A text-mode recovery needs a per-vendor rule in <c>Aer.Adapters</c> (Architecture Rule 2),
        /// not a heuristic here.
        /// </para>
        /// <para>
        /// Reading a declared field of a structured vendor response is not Architecture Rule 1's
        /// "parse conversation content to make routing decisions" — nothing is routed on it. It is
        /// the same act as reading the response file, from a different transport.
        /// </para>
        /// </remarks>
        internal static string? TryExtractAssistantAnswer(string rawStdout)
        {
            var lines = rawStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                try
                {
                    var node = JsonNode.Parse(line);

                    // claude: {"type":"result","result":"<text>","is_error":bool}
                    if (node?["type"]?.GetValue<string>() == "result")
                    {
                        // A failed turn's text is an ERROR, and belongs in ErrorMessage. Never here.
                        if (node["is_error"]?.GetValue<bool>() == true)
                        {
                            return null;
                        }

                        if (node["result"] is { } answer)
                        {
                            var text = answer.ToString();
                            return string.IsNullOrWhiteSpace(text) ? null : text;
                        }

                        continue;
                    }

                    // agy (#1088): {"event":"result","result":{"status":"SUCCESS","response":"<text>",…}}.
                    // agy streams no incremental assistant text, so this terminal event is the ONLY stdout
                    // carrier of the answer -- the #534 recovery for the all-deny / no-output-file case,
                    // now reachable because agy runs under `--output-format stream-json`. Claude's `result`
                    // is a string, agy's is an object, so `is JsonObject` keeps the two envelopes apart.
                    if (node?["event"]?.GetValue<string>() == "result" && node["result"] is JsonObject agyResult)
                    {
                        // Only a SUCCESS result is an answer; any other status is a failure whose text
                        // belongs in ErrorMessage. agy's NON-success result shape is unmeasured (its quota
                        // error is on stderr, not a stdout result), so this returns null rather than guess a
                        // field -- and returns here regardless, since the terminal result ends the scan.
                        if (agyResult["status"]?.GetValue<string>() == "SUCCESS"
                            && agyResult["response"]?.GetValue<string>() is { } response
                            && !string.IsNullOrWhiteSpace(response))
                        {
                            return response;
                        }

                        return null;
                    }
                }
                catch (JsonException)
                {
                    // Not a JSON result line -- keep scanning backward.
                }
            }

            return null;
        }

        internal static string TryExtractVendorErrorMessage(string rawStdout)
        {
            var lines = rawStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                try
                {
                    var node = JsonNode.Parse(line);
                    if (node?["type"]?.GetValue<string>() != "result")
                    {
                        continue;
                    }

                    if (node["errors"] is JsonArray errors && errors.Count > 0)
                    {
                        return string.Join("; ", errors.Select(e => e?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));
                    }

                    if (node["is_error"]?.GetValue<bool>() == true && node["result"] is { } resultText)
                    {
                        return resultText.ToString();
                    }
                }
                catch (JsonException)
                {
                    // Not a JSON result line -- keep scanning backward.
                }
            }

            var lastLine = lines.Length > 0 ? lines[^1] : null;
            return string.IsNullOrWhiteSpace(lastLine)
                ? "The vendor process exited without producing a response."
                : lastLine;
        }

        internal static async Task ReconcilePendingPermissionsAsync(
            string? roomsDirOverride = null,
            Func<RoomProjection, string, Task>? broadcastStateAsync = null,
            CancellationToken cancellationToken = default)
        {
            var baseRoomsDir = roomsDirOverride ?? AerPaths.Rooms;
            if (!Directory.Exists(baseRoomsDir)) return;

            string[] roomDirs;
            try
            {
                roomDirs = Directory.GetDirectories(baseRoomsDir);
            }
            catch
            {
                return;
            }

            foreach (var roomDir in roomDirs)
            {
                try
                {
                    await ReconcileRoomPermissionsAsync(roomDir, broadcastStateAsync, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown unwinds the whole sweep rather than being logged as one room's problem
                    // — RoomRetentionSweep.ExecuteSingleSweepAsync makes the same distinction.
                    throw;
                }
                catch (Exception ex)
                {
                    // #1241: RoomEventLogReader is deliberately loud on a malformed line, which is right
                    // for a replay and wrong for one item of a sweep. Without this, one corrupt
                    // room.jsonl threw out of the whole loop, and every room the enumeration had not
                    // reached yet silently lost its post-restart reconciliation — a stuck permission ask
                    // never re-presented, reported only as one unnamed line at startup. Which rooms lost
                    // it depended on directory order. Same rule as the session scans and /api/rooms.
                    Console.Error.WriteLine($"Permission reconciliation failed for '{roomDir}': {ex}");
                }
            }
        }

        /// <summary>
        /// Retries a room-journal write that lost the fail-fast <see cref="ConcurrencyGuard"/> race.
        /// RoomWakeBridge's periodic sweep holds the guard in millisecond bursts (#857's shape), so
        /// a loser waits briefly and retries rather than stranding an on-disk answer/sentinel file
        /// with no journal entry; the final attempt propagates so callers stay loud on real failure.
        /// </summary>
        internal static async Task RetryOnRoomLockAsync(Func<Task> journalWrite)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await journalWrite().ConfigureAwait(false);
                    return;
                }
                catch (WorkflowLockedException) when (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt)).ConfigureAwait(false);
                }
            }
        }

        internal static async Task RevokePendingGatesForRoomAsync(
            string roomDir,
            string? executionIdFilter,
            string reason,
            Func<RoomProjection, string, Task>? broadcastStateAsync = null,
            Func<Task<RoomProjection?>>? loadProjectionAsync = null)
        {
            var entries = PendingGateRegistry.GetEntries();
            foreach (var kvp in entries)
            {
                var reqId = kvp.Key;
                var entry = kvp.Value;

                if (!string.Equals(entry.RoomDir, roomDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(executionIdFilter) &&
                    !string.Equals(entry.ExecutionId, executionIdFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    // Journal first (with the room-lock retry), sentinel second, registry removal
                    // last. On a journal failure the entry stays registered, so the next turn-end
                    // or cancel sweep retries it instead of the revoke silently never reaching
                    // room.jsonl (second-reader finding on #1098).
                    var roomLogPath = Path.Combine(roomDir, "room.jsonl");
                    await RetryOnRoomLockAsync(async () =>
                    {
                        var reader = new RoomEventLogReader(roomLogPath);
                        await using var writer = new RoomEventLogWriter(roomLogPath);
                        await RoomMutationInterface.RevokePermissionAsync(
                            roomDir,
                            reader,
                            writer,
                            reqId,
                            reason).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                    var revokedFilePath = Path.Combine(entry.OutputDir, $"revoked-{reqId}.json");
                    var tempFilePath = Path.Combine(entry.OutputDir, $"revoked-{reqId}.json.{Guid.NewGuid():N}.tmp");
                    var payload = new { permissionRequestId = reqId, reason };
                    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
                    var json = JsonSerializer.Serialize(payload, jsonOptions);

                    Directory.CreateDirectory(entry.OutputDir);
                    await File.WriteAllTextAsync(tempFilePath, json).ConfigureAwait(false);
                    File.Move(tempFilePath, revokedFilePath, overwrite: true);

                    PendingGateRegistry.TryRemove(reqId, out _);

                    if (broadcastStateAsync != null && loadProjectionAsync != null)
                    {
                        if (await loadProjectionAsync().ConfigureAwait(false) is { } proj)
                        {
                            await broadcastStateAsync(proj, roomDir).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to revoke pending gate '{reqId}' with reason '{reason}': {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        internal static Task ReconcileRoomPermissionsAsync(string roomDir, CancellationToken cancellationToken = default)
            => ReconcileRoomPermissionsAsync(roomDir, broadcastStateAsync: null, cancellationToken);

        internal static async Task ReconcileRoomPermissionsAsync(
            string roomDir,
            Func<RoomProjection, string, Task>? broadcastStateAsync,
            CancellationToken cancellationToken = default)
        {
            var artifactsDir = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
            if (!Directory.Exists(artifactsDir)) return;

            string[] askFiles;
            try
            {
                askFiles = Directory.GetFiles(artifactsDir, "ask-*.json", SearchOption.AllDirectories);
            }
            catch
            {
                return;
            }

            if (askFiles.Length == 0) return;

            // Do NOT bail when room.jsonl is absent: a worker that asked permission on its first turn and
            // crashed before any journal write leaves an orphan ask in a journal-less room. Requiring the
            // journal to pre-exist would make that pause silently unrecoverable -- the exact "absence must
            // be observed, never assumed" failure 0018 forbids. RaisePermissionAsync's writer creates the
            // journal, so an empty prior-events list is the correct starting point.
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            IReadOnlyList<RoomEvent> events = File.Exists(roomLogPath)
                ? await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false)
                : [];

            var answeredById = events.OfType<RoomEvent.RuntimePermissionAnswered>()
                .GroupBy(a => a.PermissionRequestId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // #1171: reconcile journals without pushing, so a WS-connected client never learned a
            // gate was re-presented or expired at startup. Track whether this room's journal
            // changed; one broadcast at the end covers every mutation kind.
            var journalMutated = false;
            var resolvedIds = answeredById.Keys
                .Concat(events.OfType<RoomEvent.RuntimePermissionRevoked>().Select(r => r.PermissionRequestId))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var askFile in askFiles)
            {
                var fileName = Path.GetFileName(askFile);
                if (!fileName.StartsWith("ask-", StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var permissionRequestId = fileName.Substring(4, fileName.Length - 9);
                if (string.IsNullOrWhiteSpace(permissionRequestId)) continue;

                var outputDir = Path.GetDirectoryName(askFile)!;
                var answerFile = Path.Combine(outputDir, $"answer-{permissionRequestId}.json");
                var revokedFile = Path.Combine(outputDir, $"revoked-{permissionRequestId}.json");

                // A journaled answer with no answer file on disk is the crash window between the
                // journal-first write and the file write in the answer endpoint: the human's
                // decision is recorded but the worker was never released. Re-materialize the file
                // from the event so a still-polling worker resolves per the recorded answer
                // (second-reader Finding 1 on the #1098 reorder).
                if (answeredById.TryGetValue(permissionRequestId, out var answeredEvent)
                    && !File.Exists(Path.Combine(outputDir, $"answer-{permissionRequestId}.json")))
                {
                    try
                    {
                        var healPayload = new
                        {
                            decisionKind = answeredEvent.DecisionKind,
                            updatedInputJson = answeredEvent.UpdatedInputJson,
                            reason = answeredEvent.Reason
                        };
                        var healOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
                        var healJson = JsonSerializer.Serialize(healPayload, healOptions);
                        var healPath = Path.Combine(outputDir, $"answer-{permissionRequestId}.json");
                        var healTemp = Path.Combine(outputDir, $"answer-{permissionRequestId}.json.{Guid.NewGuid():N}.tmp");
                        await File.WriteAllTextAsync(healTemp, healJson, cancellationToken).ConfigureAwait(false);
                        File.Move(healTemp, healPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"reconcile: failed to re-materialize answer file '{permissionRequestId}': {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (resolvedIds.Contains(permissionRequestId)) continue;

                // A revoked sentinel with no matching journal event is a divergence to HEAL, not
                // skip: the tool's own timeout can fire while the daemon is down, and a lost room-
                // lock race can strand a sentinel (second-reader finding on #1098). Journal it now
                // so the outcome exists in room.jsonl, then move on.
                if (File.Exists(revokedFile))
                {
                    try
                    {
                        string revokedReason = "unknown";
                        using (var revokedDoc = JsonDocument.Parse(await File.ReadAllTextAsync(revokedFile, cancellationToken).ConfigureAwait(false)))
                        {
                            if (revokedDoc.RootElement.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String)
                            {
                                revokedReason = rr.GetString()!;
                            }
                        }

                        await RetryOnRoomLockAsync(async () =>
                        {
                            var healReader = new RoomEventLogReader(roomLogPath);
                            await using var healWriter = new RoomEventLogWriter(roomLogPath);
                            await RoomMutationInterface.RevokePermissionAsync(
                                roomDir,
                                healReader,
                                healWriter,
                                permissionRequestId,
                                revokedReason,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                        journalMutated = true;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"reconcile: failed to journal orphaned revoke '{permissionRequestId}': {ex.GetType().Name}: {ex.Message}");
                    }

                    continue;
                }

                if (File.Exists(answerFile)) continue;

                try
                {
                    var jsonText = await File.ReadAllTextAsync(askFile, cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;

                    var dirName = Path.GetFileName(outputDir);
                    var executionIdStr = dirName.StartsWith("execution_", StringComparison.OrdinalIgnoreCase)
                        ? dirName.Substring("execution_".Length)
                        : dirName;
                    var executionId = new ExecutionId(executionIdStr);

                    var toolName = root.TryGetProperty("toolName", out var tnElem) && tnElem.ValueKind == JsonValueKind.String
                        ? tnElem.GetString()!
                        : "unknown";

                    string inputJson = "{}";
                    if (root.TryGetProperty("inputJson", out var inElem))
                    {
                        inputJson = inElem.ValueKind == JsonValueKind.String ? inElem.GetString()! : inElem.GetRawText();
                    }

                    DateTimeOffset askedAt = ((root.TryGetProperty("askedAt", out var askedAtElem) || root.TryGetProperty("AskedAt", out askedAtElem)) && askedAtElem.TryGetDateTimeOffset(out var dto))
                        ? dto
                        : DateTimeOffset.UtcNow;

                    // The ask records its own deadline (PermissionGateTool writes timeoutSeconds
                    // beside askedAt); 180 is only the fallback for ask files predating that field.
                    var askTimeoutSeconds = root.TryGetProperty("timeoutSeconds", out var timeoutElem)
                        && timeoutElem.TryGetInt32(out var timeoutVal) && timeoutVal > 0
                        ? timeoutVal
                        : 180;

                    if (DateTimeOffset.UtcNow >= askedAt.AddSeconds(askTimeoutSeconds))
                    {
                        var revokePayload = new { permissionRequestId, reason = "expired_during_shutdown" };
                        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
                        var revokeJson = JsonSerializer.Serialize(revokePayload, jsonOptions);
                        var tempRevokedFilePath = Path.Combine(outputDir, $"revoked-{permissionRequestId}.json.{Guid.NewGuid():N}.tmp");
                        await File.WriteAllTextAsync(tempRevokedFilePath, revokeJson, cancellationToken).ConfigureAwait(false);
                        File.Move(tempRevokedFilePath, revokedFile, overwrite: true);

                        await using var expireWriter = new RoomEventLogWriter(roomLogPath);
                        await RoomMutationInterface.RevokePermissionAsync(
                            roomDir,
                            reader,
                            expireWriter,
                            permissionRequestId,
                            "expired_during_shutdown",
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        journalMutated = true;
                        continue;
                    }

                    var entry = new PendingGateEntry(roomDir, outputDir, executionIdStr, askFile);
                    PendingGateRegistry.Register(permissionRequestId, entry);

                    await using var writer = new RoomEventLogWriter(roomLogPath);
                    await RoomMutationInterface.RaisePermissionAsync(
                        roomDir,
                        reader,
                        writer,
                        permissionRequestId,
                        executionId,
                        new StepId(InteractiveSessionMaterializer.DefaultStepId),
                        InteractiveSessionMaterializer.DefaultWorkerName,
                        "unknown",
                        "",
                        toolName,
                        inputJson,
                        toolName,
                        askedAt,
                        cancellationToken).ConfigureAwait(false);
                    journalMutated = true;

                    // #1113: a re-raised young ask usually has no live PermissionGateTool left to
                    // enforce its own timeout — the worker that would write the timeout sentinel was
                    // a child of a turn this daemon no longer hosts, and this reconcile pass runs
                    // exactly once. Schedule the expiry the dead worker can no longer perform, at the
                    // ask's own recorded deadline. Harmless when the worker IS still alive (a CLI
                    // pump independent of the daemon) or a human answers first: the registry entry is
                    // gone once anything resolves the ask, so the sweep below finds nothing, and
                    // RevokePermissionAsync refuses an already-resolved id either way.
                    var remaining = askedAt.AddSeconds(askTimeoutSeconds) - DateTimeOffset.UtcNow;
                    var expiryExecutionId = executionIdStr;
                    var expiryRequestId = permissionRequestId;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (remaining > TimeSpan.Zero)
                            {
                                await Task.Delay(remaining).ConfigureAwait(false);
                            }

                            // #1171: the expiry fires minutes after startup, into clients that may
                            // be rendering the gate — push the refreshed projection so the card
                            // retires instead of lingering until an unrelated event broadcasts.
                            await RevokePendingGatesForRoomAsync(
                                roomDir, expiryExecutionId, "timeout",
                                broadcastStateAsync,
                                broadcastStateAsync is null ? null : () => TryLoadProjectionAsync(roomDir)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                $"reconcile: delayed expiry for re-raised ask '{expiryRequestId}' failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    // Log and continue: a single unreadable/corrupt ask file must not abort reconciling
                    // the rest, but it must NOT vanish silently -- a swallowed exception here is exactly
                    // the "silence must be earned" failure (0018) in the mechanism that exists to prevent
                    // silent loss. This surfaced a real defect (an empty vendor correlation id threw and
                    // was hidden, killing all reconciliation).
                    Console.Error.WriteLine(
                        $"reconcile: failed to re-raise permission ask '{askFile}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            // The push is wrapped like RevokePendingGatesForRoomAsync wraps its own (#1171 review):
            // today's DaemonBroadcast never throws, but a future delegate that did would otherwise
            // silently truncate reconciliation for every room enumerated after this one.
            if (journalMutated && broadcastStateAsync is not null
                && await TryLoadProjectionAsync(roomDir, cancellationToken).ConfigureAwait(false) is { } refreshed)
            {
                try
                {
                    await broadcastStateAsync(refreshed, roomDir).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"reconcile: broadcast after healing '{roomDir}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Best-effort projection load for reconcile broadcasts (#1171): a room whose snapshot is
        /// absent or malformed must not kill reconciliation of the rest — the broadcast is skipped
        /// loudly, the journal heal already happened.
        /// </summary>
        private static async Task<RoomProjection?> TryLoadProjectionAsync(string roomDir, CancellationToken cancellationToken = default)
        {
            try
            {
                return await RoomProjectionLoader.LoadAsync(roomDir, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"reconcile: projection load for broadcast failed for '{roomDir}': {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    public class PairRequest
    {
        public string Code { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
    }

    public record RegisterProjectRequest(string Path, string? FriendlyName = null);

    /// <summary>M24 Phase 2 follow-up: records a picked skill/command/agent as this vendor's most-recently-used, via <see cref="LocalUiConfigurationStore.RecordCommandUsedAsync"/>.</summary>
    public record RecordCommandUsedRequest(string Name);

    /// <summary>
    /// Session-level mode (M24 Phase 2 follow-up, per discussion with the owner): a vendor-neutral
    /// permission mode settable mid-session, applying to whichever vendor is currently active --
    /// distinct from <see cref="StartSessionRequest.PermissionGrant"/>, which only ever applies at
    /// session creation. <paramref name="Mode"/> is one of "auto" (maximally permissive -- Claude's
    /// full <c>Read,Edit,Write,Bash,WebFetch,WebSearch</c> grant, Gemini's <c>accept-edits</c>),
    /// "default" (this session's original grant), or "plan" (read-only -- <see cref="PermissionGrant.WriteFiles"/>/<see cref="PermissionGrant.RunShellCommands"/> both off).
    /// </summary>
    public record SetSessionModeRequest(string Mode);

    /// <summary>#799: points <see cref="RoomWakeBridge"/> at the room directory to watch.</summary>
    public record WatchRoomRequest(string RoomDirectoryPath);

    /// <summary>#672: <paramref name="Outcome"/> is "approve" or "reject" (case-insensitive); <paramref name="Ref"/> is the <see cref="HeldWorkRef"/>'s own string value.</summary>
    public record ResolveHeldWorkRequest(string RoomDirectoryPath, string Ref, string Outcome);

    /// <summary>#992: clears dormancy on a room.</summary>
    public record ClearDormancyRequest(string RoomDirectoryPath);

    /// <summary>#1216: switches a room's workflow on or off — see <see cref="RoomMutationInterface.SetWorkflowSwitchAsync"/>.</summary>
    public record SetWorkflowSwitchRequest(string RoomDirectoryPath, bool IsOn);

    public record AnswerPermissionRequest(
        string DirectoryPath,
        string PermissionRequestId,
        string DecisionKind,
        JsonElement? UpdatedInput = null,
        string? Reason = null);

    /// <summary>
    /// #1238's request shape. <c>WorkerName</c> is optional and defaults to the chat worker, matching
    /// the answer path's own assumption — the ladder is only ever answered for that worker today, and
    /// requiring callers to name it would invite them to guess.
    /// </summary>
    public record RevokePermissionRequest(
        string DirectoryPath,
        string RevokeKind,
        string? ShellCommandPattern = null,
        string? WorkerName = null);
}

