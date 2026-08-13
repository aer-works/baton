import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';

/// Widget-level coverage for `_ChatScreenState`'s permission-gate wiring (0022, #390's mobile
/// phase) — a second-reader review flagged that `_surfacePendingPermission`'s three-way branch and
/// `_answerPermission`'s in-flight flag lifecycle had no test (`PermissionGateCard` itself is
/// covered directly by `permission_gate_card_test.dart`, but never through `ChatScreen`'s own
/// wiring). `watch()` is a plain instance method (not an HTTP round trip like `rooms_screen_test`'s
/// `MockClient` fakes), so this subclasses [DaemonClient] and overrides every method
/// `initState`/`_answerPermission` call, mirroring the reviewer's suggested approach.
class _FakeDaemonClient extends DaemonClient {
  _FakeDaemonClient() : super(host: 'localhost:5000', token: 'fake-token');

  final _projectionController = StreamController<RoomProjection>.broadcast();

  /// Set to make [answerPermission] throw once its future resolves. A [DaemonException] exercises
  /// the pre-existing narrow catch; any other error (a plain [Exception], standing in for a
  /// [SocketException]-style transport failure) exercises the Finding-1 regression guard.
  Object? answerError;

  /// When set, [answerPermission] awaits this before resolving/throwing — lets a test hold an
  /// answer "in flight" long enough to push a second projection and assert on gate state while
  /// `_isAnsweringPermission` is still true.
  Completer<void>? answerGate;

  int answerCallCount = 0;

  void push(RoomProjection projection) => _projectionController.add(projection);

  /// Drops an error onto the projection stream — stands in for a WebSocket transport failure so a
  /// test can assert the screen surfaces it (the found-while-fixing `onError` gap) rather than
  /// swallowing it. The broadcast controller stays open afterwards, so a later [push] still lands.
  void pushError(Object error) => _projectionController.addError(error);

  @override
  Stream<RoomProjection> watch() => _projectionController.stream;

  @override
  Stream<SessionProgressEvent> watchProgress() => const Stream.empty();

  @override
  Future<SessionMetadata> getSession(String sessionId) async => SessionMetadata(
        sessionId: sessionId,
        roomDirectoryPath: '/tasks/foo',
        currentAdapter: 'claude',
        turnCount: 0,
        turns: const [],
      );

  @override
  Future<String> getSessionMode(String sessionId) async => 'default';

  @override
  Future<void> answerPermission({
    required String directoryPath,
    required String permissionRequestId,
    required String decisionKind,
    String? reason,
  }) async {
    answerCallCount++;
    final gate = answerGate;
    if (gate != null) await gate.future;
    final error = answerError;
    if (error != null) throw error;
  }
}

void main() {
  PendingPermission pending(String id) => PendingPermission(
        permissionRequestId: id,
        workerId: 'worker-1',
        vendorTag: 'claude',
        toolName: 'Bash',
        toolInputJson: '{"command":"rm -rf build/"}',
        category: 'Shell',
        askedAt: DateTime.utc(2026, 8, 9, 12),
      );

  RoomProjection projection({PendingPermission? withPending, List<PermissionAnswer>? withAnswers}) => RoomProjection(
        directoryPath: '/tasks/foo',
        sessionId: 'sess-1',
        workflowTemplateId: 'chat',
        status: 'Running',
        stepDefinitions: const [],
        steps: const [],
        executions: const [],
        workerAdapters: const {},
        pendingPermission: withPending,
        permissionAnswers: withAnswers ?? const [],
      );

  Future<_FakeDaemonClient> pumpChatScreen(WidgetTester tester) async {
    final client = _FakeDaemonClient();
    await tester.pumpWidget(MaterialApp(
      home: ChatScreen(client: client, sessionId: 'sess-1', directoryPath: '/tasks/foo'),
    ));
    await tester.pumpAndSettle();
    return client;
  }

  group('_ChatScreenState permission gate wiring (0022, #390 mobile, second-reader finding)', () {
    testWidgets('a push with permissionAnswers renders system transcript lines in order', (tester) async {
      final client = await pumpChatScreen(tester);

      final answers = [
        PermissionAnswer(
          permissionRequestId: 'req-1',
          toolName: 'Bash',
          category: 'Shell',
          decisionKind: 'AllowOnce',
          reason: null,
          deciderIdentity: 'op',
          answeredAt: DateTime.utc(2026, 8, 12, 10, 2),
          wasRevoked: false,
        ),
        PermissionAnswer(
          permissionRequestId: 'req-2',
          toolName: 'Edit',
          category: 'Files',
          decisionKind: 'Deny',
          reason: 'user declined',
          deciderIdentity: 'op',
          answeredAt: DateTime.utc(2026, 8, 12, 10, 5),
          wasRevoked: false,
        ),
        PermissionAnswer(
          permissionRequestId: 'req-3',
          toolName: 'Bash',
          category: 'Shell',
          decisionKind: '',
          reason: 'turn_ended',
          deciderIdentity: '',
          answeredAt: DateTime.utc(2026, 8, 12, 10, 12),
          wasRevoked: true,
        ),
      ];

      client.push(projection(withAnswers: answers));
      await tester.pumpAndSettle();

      expect(find.text('Allowed once — Bash'), findsOneWidget);
      expect(find.text('Denied — Edit: user declined'), findsOneWidget);
      expect(find.text('Expired unanswered — turn ended'), findsOneWidget);
    });

    testWidgets('a push with a pendingPermission renders the gate', (tester) async {
      final client = await pumpChatScreen(tester);

      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();

      expect(find.text('Allow once'), findsOneWidget);
      expect(find.text('Deny once'), findsOneWidget);
    });

    testWidgets(
        'a second push with the SAME permissionRequestId while an answer is in flight does not reset the in-flight flag',
        (tester) async {
      final client = await pumpChatScreen(tester);
      client.answerGate = Completer<void>();

      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Allow once'));
      await tester.pump();
      expect(client.answerCallCount, 1);

      // A redundant push carrying the SAME pendingPermission (e.g. an unrelated projection field
      // changed) must not touch _isAnsweringPermission -- the no-op-push case the desktop bug was
      // about.
      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();

      // Still in flight: tapping again must be a no-op (the guard in _answerPermission), so the
      // call count must not have grown.
      await tester.tap(find.text('Allow once'));
      await tester.pump();
      expect(client.answerCallCount, 1);

      client.answerGate!.complete();
      await tester.pumpAndSettle();
    });

    testWidgets('a push with pendingPermission == null clears the gate', (tester) async {
      final client = await pumpChatScreen(tester);

      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();
      expect(find.text('Allow once'), findsOneWidget);

      client.push(projection());
      await tester.pumpAndSettle();

      expect(find.text('Allow once'), findsNothing);
    });

    testWidgets(
        'a NON-DaemonException from answerPermission resets the in-flight flag and shows a SnackBar (Finding-1 regression guard)',
        (tester) async {
      final client = await pumpChatScreen(tester);
      client.answerError = Exception('socket closed');

      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Allow once'));
      await tester.pumpAndSettle();

      expect(find.byType(SnackBar), findsOneWidget);
      expect(find.textContaining('socket closed'), findsOneWidget);
      expect(client.answerCallCount, 1);

      // The flag must have been reset -- tapping again must reach the fake a second time, not be
      // swallowed by a flag stuck true.
      client.answerError = null;
      await tester.tap(find.text('Allow once'));
      await tester.pump();
      expect(client.answerCallCount, 2);
    });

    testWidgets(
        'a watch() stream error surfaces a recoverable Reconnect banner instead of a silent swallow (found-while-fixing #390)',
        (tester) async {
      final client = await pumpChatScreen(tester);

      // Gate open, then the socket drops. Before the fix, `watch().listen` had no `onError`, so this
      // error escaped to the test zone as an unhandled async error (a failing test) and no banner
      // appeared -- the silent swallow that stranded the gate with no user signal.
      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();
      expect(find.text('Allow once'), findsOneWidget);

      client.pushError(Exception('socket closed'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Disconnected'), findsOneWidget);
      expect(find.text('Reconnect'), findsOneWidget);

      // Reconnect re-subscribes and clears the banner (the button's whole point).
      await tester.tap(find.text('Reconnect'));
      await tester.pumpAndSettle();
      expect(find.textContaining('Disconnected'), findsNothing);
    });

    testWidgets('a projection push after a stream error self-clears the banner with no tap', (tester) async {
      final client = await pumpChatScreen(tester);

      client.pushError(Exception('socket closed'));
      await tester.pumpAndSettle();
      expect(find.textContaining('Disconnected'), findsOneWidget);

      // Transport recovers on its own: the next push must clear the banner without the user tapping
      // Reconnect (the self-heal the onData handler's `_connectionError = null` promises).
      client.push(projection(withPending: pending('perm-1')));
      await tester.pumpAndSettle();
      expect(find.textContaining('Disconnected'), findsNothing);
      expect(find.text('Allow once'), findsOneWidget);
    });
  });
}
