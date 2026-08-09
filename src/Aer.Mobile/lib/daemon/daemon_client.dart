import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:tailscale/tailscale.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

import 'models.dart';
import 'tailnet_gateway.dart';
import 'ws_client.dart';

class DaemonException implements Exception {
  final String message;
  DaemonException(this.message);

  @override
  String toString() => message;
}

typedef TsnetDialFn = Future<TailscaleConnection> Function(String host, int port);

/// Brings the embedded tsnet node up if it isn't already, before this client trusts it enough to
/// dial. Defaults to [TailnetGateway.ensureUp]; overridable for tests.
typedef TsnetEnsureUpFn = Future<TailscaleStatus> Function();

/// REST + WebSocket client for Aer.Daemon's remote API (src/Aer.Daemon/Program.cs). `host` is an
/// authority string like `192.168.1.23:5050` — no scheme, matching how the pairing screen collects
/// it (the user types/scans host+port, not a full URL).
class DaemonClient {
  final String host;
  final String token;
  final bool tsnetRouted;
  final http.Client? httpClient;
  final TsnetDialFn? tsnetDialFn;
  final TsnetEnsureUpFn? tsnetEnsureUpFn;

  DaemonClient({
    required this.host,
    required this.token,
    this.tsnetRouted = false,
    this.httpClient,
    this.tsnetDialFn,
    this.tsnetEnsureUpFn,
  });

  /// The only unauthenticated endpoint besides GET /api/version. Returns the raw paired-client
  /// token — shown/stored exactly once, same as the desktop UI's own pairing flow.
  ///
  /// [httpClient] routes the request over the embedded tailnet node (M21 Phase 7, #246) instead of
  /// the phone's regular network stack — required when [host] is a tailnet-only address, since
  /// `tsnet` is a userspace network stack with no OS-level route for it. Omit for a plain LAN host.
  static Future<String> pair({
    required String host,
    required String code,
    required String clientName,
    http.Client? httpClient,
  }) async {
    final response = httpClient != null
        ? await httpClient.post(
            Uri.http(host, '/api/pairing/pair'),
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({'code': code, 'clientName': clientName}),
          )
        : await http.post(
            Uri.http(host, '/api/pairing/pair'),
            headers: {'Content-Type': 'application/json'},
            body: jsonEncode({'code': code, 'clientName': clientName}),
          );
    final body = caseInsensitive(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
    if (response.statusCode != 200) {
      throw DaemonException(
        body['error']?.toString() ??
            'Pairing failed (HTTP ${response.statusCode}).',
      );
    }
    return body['token'].toString();
  }

  /// The other unauthenticated endpoint. Used as the tsnet connectivity proof (M21 Phase 7,
  /// #246) — a successful call here means the phone joined the tailnet and actually routed to the
  /// sidecar, before trusting a pairing attempt on top of that same [httpClient].
  static Future<void> version({
    required String host,
    http.Client? httpClient,
  }) async {
    final response = httpClient != null
        ? await httpClient.get(Uri.http(host, '/api/version'))
        : await http.get(Uri.http(host, '/api/version'));
    if (response.statusCode != 200) {
      throw DaemonException(
        'Could not reach $host (HTTP ${response.statusCode}).',
      );
    }
  }

  Map<String, String> get _authHeader => {'Authorization': 'Bearer $token'};

  Future<http.Response> _get(Uri url) {
    if (httpClient != null) return httpClient!.get(url, headers: _authHeader);
    if (tsnetRouted) return TailnetGateway().client.get(url, headers: _authHeader);
    return http.get(url, headers: _authHeader);
  }

  Future<http.Response> _post(Uri url, {Object? body, Map<String, String>? headers}) {
    final mergedHeaders = {..._authHeader, ...?headers};
    if (httpClient != null) return httpClient!.post(url, headers: mergedHeaders, body: body);
    if (tsnetRouted) return TailnetGateway().client.post(url, headers: mergedHeaders, body: body);
    return http.post(url, headers: mergedHeaders, body: body);
  }

  /// A full RoomProjection snapshot on connect (if a task is currently open server-side) and again
  /// on every state change thereafter — never a diff. See RoomProjection's doc comment for why the
  /// snapshot alone doesn't tell this client which directory it's for.
  Stream<RoomProjection> watch() {
    if (tsnetRouted) {
      return _watchOverTsnet();
    }
    return _watchOverPlain();
  }

  // #1045: close the socket when the subscription is cancelled, matching _watchOverTsnet's finally.
  // Returning channel.stream.map directly leaked the WebSocket on every cancel.
  Stream<RoomProjection> _watchOverPlain() async* {
    final channel = WebSocketChannel.connect(
      Uri.parse('ws://$host/api/ws?token=$token'),
    );
    try {
      yield* channel.stream.map(
        (raw) => RoomProjection.fromJson(
          jsonDecode(raw as String) as Map<String, dynamic>,
        ),
      );
    } finally {
      await channel.sink.close();
    }
  }

  Stream<RoomProjection> _watchOverTsnet() async* {
    await _ensureTsnetUp();
    final parts = host.split(':');
    final targetHost = parts[0];
    final targetPort = parts.length > 1 ? int.parse(parts[1]) : 5050;

    final dial = tsnetDialFn ?? ((h, p) => Tailscale.instance.tcp.dial(h, p));
    final connection = await dial(targetHost, targetPort);
    final wsChannel = await TsnetWsChannel.connect(
      socket: TailscaleWsSocket(connection),
      host: host,
      path: '/api/ws?token=$token',
    );

    try {
      yield* wsChannel.stream.map(
        (raw) => RoomProjection.fromJson(
          jsonDecode(raw) as Map<String, dynamic>,
        ),
      );
    } finally {
      await wsChannel.close();
    }
  }

  /// A session's live in-turn streaming (M24 Phase 1, issue #262) — a dedicated socket/frame shape
  /// from `watch()`, broadcast to every connected progress socket regardless of directory. Callers
  /// (ChatScreen) must filter on `SessionProgressEvent.directoryPath` themselves, same as desktop's
  /// MainWindow.axaml.cs does for its own subscription.
  Stream<SessionProgressEvent> watchProgress() {
    if (tsnetRouted) {
      return _watchProgressOverTsnet();
    }
    return _watchProgressOverPlain();
  }

  // #1045: same socket-close-on-cancel as _watchProgressOverTsnet; the plain path leaked otherwise.
  Stream<SessionProgressEvent> _watchProgressOverPlain() async* {
    final channel = WebSocketChannel.connect(
      Uri.parse('ws://$host/api/ws/progress?token=$token'),
    );
    try {
      yield* channel.stream.map(
        (raw) => SessionProgressEvent.fromJson(
          jsonDecode(raw as String) as Map<String, dynamic>,
        ),
      );
    } finally {
      await channel.sink.close();
    }
  }

  Stream<SessionProgressEvent> _watchProgressOverTsnet() async* {
    await _ensureTsnetUp();
    final parts = host.split(':');
    final targetHost = parts[0];
    final targetPort = parts.length > 1 ? int.parse(parts[1]) : 5050;

    final dial = tsnetDialFn ?? ((h, p) => Tailscale.instance.tcp.dial(h, p));
    final connection = await dial(targetHost, targetPort);
    final wsChannel = await TsnetWsChannel.connect(
      socket: TailscaleWsSocket(connection),
      host: host,
      path: '/api/ws/progress?token=$token',
    );

    try {
      yield* wsChannel.stream.map(
        (raw) => SessionProgressEvent.fromJson(
          jsonDecode(raw) as Map<String, dynamic>,
        ),
      );
    } finally {
      await wsChannel.close();
    }
  }

  /// Guards every tsnet dial against the engine not actually being up in *this* process yet
  /// (issue #287). `Tailscale.instance` is a process-wide singleton that only starts the embedded
  /// node when [TailnetGateway.ensureUp]/`up()` is called — historically that only ever happened
  /// once, from the one-time pairing flow (`pairing_screen.dart`). That's fine as long as the same
  /// process stays alive for the life of the paired credentials, but Android does not guarantee
  /// that: a screen-timeout-driven background can lead to the OS reclaiming the process, and on
  /// relaunch this client resumes straight into `watch()` with a brand-new, never-started
  /// `Tailscale.instance` — `tcp.dial()` then fails immediately with `TailscaleTcpException(tcp):
  /// tcpDialFd called before Start` (confirmed against the vendored `tailscale` package's Go
  /// source: `srv` is nil until `Start()` runs, and `NodeState.stopped` — "engine not running but
  /// persisted credentials exist" — is exactly what a fresh process reports). `ensureUp()` with no
  /// authKey is a no-op if the node is already running and otherwise restarts it from the
  /// credentials `Tailscale.init`'s stateDir already has on disk, so calling it here is safe and
  /// cheap on the common (already-running) path and self-healing on the broken one — including from
  /// the Reconnect button, which works simply by re-running this same `watch()`/`watchProgress()`
  /// path rather than retrying a dial against a client it already knows is dead.
  Future<void> _ensureTsnetUp() async {
    final ensureUp = tsnetEnsureUpFn ?? TailnetGateway().ensureUp;
    final status = await ensureUp();
    if (status.state != NodeState.running) {
      throw DaemonException(
        'Tailscale is not connected (${status.state.name}). Re-pair this device if the problem persists.',
      );
    }
  }

  Future<List<String>> recentRooms() async {
    final response = await _get(Uri.http(host, '/api/rooms/recent'));
    _throwIfFailed(response);
    return (jsonDecode(response.body) as List<dynamic>)
        .map((d) => d.toString())
        .toList();
  }

  /// Reassigns Aer.Daemon's own notion of "current" task, which still broadcasts to every
  /// connected client — see RoomProjection's doc comment. Callers must also update their own
  /// local `_openDirectoryPath` (or equivalent) after this succeeds, or their own filter will
  /// discard the resulting push. Only call this from an explicit user action (the recent-tasks
  /// picker), never automatically.
  Future<void> openRoom(String directoryPath) async {
    final response = await _post(
      Uri.http(host, '/api/rooms/open'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath}),
    );
    _throwIfFailed(response);
  }

  /// Text content of one execution's output file, or null if the daemon has no such file (already
  /// deleted, or the execution/fileName pair doesn't match its recorded OutputFiles). See
  /// src/Aer.Daemon/Program.cs's /api/rooms/artifact handler (M21 Phase 2, #232).
  Future<String?> fetchArtifact({
    required String directoryPath,
    required String executionId,
    required String fileName,
  }) async {
    final uri = Uri.http(host, '/api/rooms/artifact', {
      'directoryPath': directoryPath,
      'executionId': executionId,
      'fileName': fileName,
    });
    final response = await _get(uri);
    if (response.statusCode == 404) return null;
    _throwIfFailed(response);
    final body = caseInsensitive(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
    return body['content'].toString();
  }

  /// Lists available built-in workflow templates and vendor CLI presence.
  Future<Map<String, dynamic>> listTemplates() async {
    final response = await _get(Uri.http(host, '/api/templates'));
    _throwIfFailed(response);
    return jsonDecode(response.body) as Map<String, dynamic>;
  }

  /// Runs a built-in template on the daemon and returns the materialized room directory path.
  Future<String> runTemplate({
    required String templateId,
    String? primaryAdapter,
    String? secondaryAdapter,
    String? roomName,
    String? customPrompt,
    String? secondaryCustomPrompt,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/templates/run'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'templateId': templateId,
        'primaryAdapter': primaryAdapter,
        'secondaryAdapter': secondaryAdapter,
        'roomName': roomName,
        'customPrompt': customPrompt,
        'secondaryCustomPrompt': secondaryCustomPrompt,
      }),
    );
    final body = caseInsensitive(jsonDecode(response.body) as Map<String, dynamic>);
    return body['roomdirectorypath']?.toString() ?? '';
  }

  /// Starts an interactive session on the daemon (M24).
  Future<Map<String, dynamic>> startSession({
    String? adapter,
    String? model,
    String? workingDirectory,
    String? initialMessage,
    String? roomName,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/sessions/start'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'adapter': adapter, // vocabulary-ok: payload field key
        'model': model,
        'workingDirectory': workingDirectory,
        'initialMessage': initialMessage,
        'roomName': roomName,
      }),
    );
    _throwIfFailed(response);
    return jsonDecode(response.body) as Map<String, dynamic>;
  }

  /// Sends a typed message to an active interactive session (M24).
  Future<void> sendSessionMessage({
    required String sessionId,
    required String message,
    String? adapter,
    String? model,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/sessions/send'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'sessionId': sessionId,
        'message': message,
        'adapter': adapter, // vocabulary-ok: payload field key
        'model': model,
      }),
    );
    _throwIfFailed(response);
  }

  /// Fetches an interactive session's full state, including turn history — the mobile chat
  /// screen's counterpart of desktop's ChatViewModel.LoadFromMetadata, which reads
  /// `.aer/session.json` straight off disk. This app has no filesystem access to the daemon host,
  /// so it re-fetches this after every filtered WS push for the session's directory instead of
  /// polling on a timer.
  Future<SessionMetadata> getSession(String sessionId) async {
    final response = await _get(Uri.http(host, '/api/sessions/$sessionId'));
    _throwIfFailed(response);
    return SessionMetadata.fromJson(jsonDecode(response.body) as Map<String, dynamic>);
  }

  /// Compacts an interactive session history (M24 Phase 2).
  Future<void> compactSession(String sessionId) async {
    final response = await _post(
      Uri.http(host, '/api/sessions/$sessionId/compact'),
    );
    _throwIfFailed(response);
  }

  /// Clears a session's visible transcript and forces the next turn to start a genuinely fresh
  /// native session (#286) — unlike [compactSession] this never talks to the vendor and completes
  /// synchronously, returning the updated (empty-turns) metadata directly.
  Future<SessionMetadata> clearSession(String sessionId) async {
    final response = await _post(
      Uri.http(host, '/api/sessions/$sessionId/clear'),
    );
    _throwIfFailed(response);
    return SessionMetadata.fromJson(jsonDecode(response.body) as Map<String, dynamic>);
  }

  /// Discovered skills/commands/agents/models for a session's current vendor, plus recently-used
  /// ordering (M24 Phase 2 follow-up chat capability picker).
  Future<SessionCommandsResult> getSessionCommands(String sessionId) async {
    final response = await _get(Uri.http(host, '/api/sessions/$sessionId/commands'));
    _throwIfFailed(response);
    return SessionCommandsResult.fromJson(jsonDecode(response.body) as Map<String, dynamic>);
  }

  /// Records a picked command as this vendor's most-recently-used.
  Future<void> recordCommandUsed(String sessionId, String name) async {
    final response = await _post(
      Uri.http(host, '/api/sessions/$sessionId/commands/record'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'name': name}),
    );
    _throwIfFailed(response);
  }

  /// Session-level mode (M24 Phase 2 follow-up): one of "auto", "default", "plan" — applies to
  /// whichever vendor is currently active, taking effect on the next turn.
  Future<void> setSessionMode(String sessionId, String mode) async {
    final response = await _post(
      Uri.http(host, '/api/sessions/$sessionId/mode'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'mode': mode}),
    );
    _throwIfFailed(response);
  }

  /// The currently active session mode (#286), reverse-mapped server-side from the persisted
  /// PermissionGrant — "auto", "default", "plan", or "custom".
  Future<String> getSessionMode(String sessionId) async {
    final response = await _get(Uri.http(host, '/api/sessions/$sessionId/mode'));
    _throwIfFailed(response);
    final body = jsonDecode(response.body) as Map<String, dynamic>;
    return body['mode'] as String;
  }

  /// Fetches known projects registry from daemon (M24 Phase 3).
  Future<List<Map<String, dynamic>>> listKnownProjects() async {
    final response = await _get(Uri.http(host, '/api/projects'));
    _throwIfFailed(response);
    final list = jsonDecode(response.body) as List<dynamic>;
    return list.map((item) => item as Map<String, dynamic>).toList();
  }

  /// decisionType is one of "Resume" | "Reject" | "Supersede" | "RetryWithRevision".
  /// Supports optional [artifactReference] ({'executionId': ..., 'fileName': ...}) for server-side
  /// resolution when the client has no local filesystem access.
  Future<void> decide({
    required String directoryPath,
    required String stepId,
    required String executionId,
    required String decisionType,
    String? targetStepId,
    String? revisionFilePath,
    Map<String, String>? artifactReference,
  }) async {
    final payload = <String, dynamic>{
      'directoryPath': directoryPath,
      'stepId': stepId,
      'executionId': executionId,
      'decisionType': decisionType,
      // ignore: use_null_aware_elements
      if (targetStepId != null) 'targetStepId': targetStepId,
      // ignore: use_null_aware_elements
      if (revisionFilePath != null) 'revisionFilePath': revisionFilePath,
      // ignore: use_null_aware_elements
      if (artifactReference != null) 'artifactReference': artifactReference,
    };
    final response = await _post(
      Uri.http(host, '/api/rooms/decide'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode(payload),
    );
    _throwIfFailed(response);
  }

  /// Every known task/session directory's lightweight status (M24 Phase 5, #278) — archived items
  /// are filtered out by default, matching desktop's own RoomsViewModel.
  Future<List<RoomFleetItem>> listRooms({bool includeArchived = false}) async {
    final uri = Uri.http(host, '/api/rooms', {'includeArchived': includeArchived.toString()});
    final response = await _get(uri);
    _throwIfFailed(response);
    final list = jsonDecode(response.body) as List<dynamic>;
    return list.map((item) => RoomFleetItem.fromJson(item as Map<String, dynamic>)).toList();
  }

  /// Hides a room directory from the default fleet list — the name stays reserved until a
  /// real [deleteRoom].
  Future<void> archiveRoom(String directoryPath) async {
    final response = await _post(
      Uri.http(host, '/api/rooms/archive'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath}),
    );
    _throwIfFailed(response);
  }

  /// Reinstates a task/session directory into the default fleet list.
  Future<void> unarchiveRoom(String directoryPath) async {
    final response = await _post(
      Uri.http(host, '/api/rooms/unarchive'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath}),
    );
    _throwIfFailed(response);
  }

  /// Really deletes a task/session directory — the only action that frees its name for reuse.
  Future<void> deleteRoom(String directoryPath) async {
    final response = await _post(
      Uri.http(host, '/api/rooms/delete'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'directoryPath': directoryPath}),
    );
    _throwIfFailed(response);
  }

  /// Answers a runtime permission (0022's ladder, #390's mobile phase) by posting to
  /// /api/rooms/permissions/answer — the daemon writes the rendezvous answer file that releases the
  /// held worker, records the answer as a turn, amends the room's chat-worker grant for a
  /// persisting rung, and broadcasts a fresh projection whose `pendingPermission` is now clear.
  /// [decisionKind] must be one of PermissionDecisionKind's values (permission_decision_kind.dart) —
  /// the daemon fails closed on anything else. Body keys are camelCase, matching every other call in
  /// this file; ASP.NET's body binding is case-insensitive (same as desktop's own POST — see
  /// RoomClient.Permissions.cs's remarks).
  Future<void> answerPermission({
    required String directoryPath,
    required String permissionRequestId,
    required String decisionKind,
    String? reason,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/rooms/permissions/answer'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'directoryPath': directoryPath,
        'permissionRequestId': permissionRequestId,
        'decisionKind': decisionKind,
        'reason': reason,
      }),
    );
    _throwIfFailed(response);
  }

  /// executionId null cancels the whole run; non-null cancels just that execution.
  Future<void> cancelRun({
    required String directoryPath,
    String? executionId,
  }) async {
    final response = await _post(
      Uri.http(host, '/api/rooms/cancel'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({
        'directoryPath': directoryPath,
        'executionId': executionId,
      }),
    );
    _throwIfFailed(response);
  }

  void _throwIfFailed(http.Response response) {
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw DaemonException(
        'Request failed (HTTP ${response.statusCode}): ${response.body}',
      );
    }
  }
}
