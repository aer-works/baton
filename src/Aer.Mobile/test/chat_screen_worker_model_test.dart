import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';

/// #1311 (0054 §1's mobile remainder, #1305): the session-room header chip parses
/// `Participant.model` (`daemon/models.dart`) but never rendered it -- desktop's
/// `ChatViewModel.WorkerModelText`/`HasWorkerModel` already show name + model
/// (`ChatViewModelTests.cs`), and this pins the phone side of the same split. Same
/// subclass-and-override fake as `chat_screen_gate_test.dart`, since `getSession` is a plain
/// instance method rather than an HTTP round trip.
class _FakeDaemonClient extends DaemonClient {
  _FakeDaemonClient() : super(host: 'localhost:5000', token: 'fake-token');

  final _projectionController = StreamController<RoomProjection>.broadcast();

  /// Set before pumping so `getSession` serves a metadata carrying (or not carrying) a participant.
  List<Participant>? participants;

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
        participants: participants,
      );

  @override
  Future<String> getSessionMode(String sessionId) async => 'default';
}

void main() {
  Future<void> pumpChatScreen(WidgetTester tester, _FakeDaemonClient client) async {
    await tester.pumpWidget(MaterialApp(
      home: ChatScreen(client: client, sessionId: 'sess-1', directoryPath: '/tasks/foo'),
    ));
    await tester.pumpAndSettle();
  }

  group('_ChatScreenState session-room header chip (#1311, 0054 §1)', () {
    testWidgets('a participant with a model renders name and model as separate texts', (tester) async {
      final client = _FakeDaemonClient()
        ..participants = [
          Participant(id: 'claude', name: 'claude', vendor: 'claude', model: 'claude-sonnet-4.5', isOrchestrator: true),
        ];

      await pumpChatScreen(tester, client);

      expect(find.text('claude'), findsOneWidget);
      expect(find.text('claude-sonnet-4.5'), findsOneWidget);
      // Not concatenated into one string -- the name and model stay two distinct Text widgets.
      expect(find.text('claude claude-sonnet-4.5'), findsNothing);
      expect(find.text('claude — claude-sonnet-4.5'), findsNothing);
    });

    testWidgets('a participant with no model renders the name only', (tester) async {
      final client = _FakeDaemonClient()
        ..participants = [
          Participant(id: 'claude', name: 'claude', vendor: 'claude', model: null, isOrchestrator: true),
        ];

      await pumpChatScreen(tester, client);

      expect(find.text('claude'), findsOneWidget);
      expect(find.textContaining('claude-sonnet'), findsNothing);
    });
  });
}
