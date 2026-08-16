import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';

class _ThinkingTimeFakeDaemonClient extends DaemonClient {
  _ThinkingTimeFakeDaemonClient() : super(host: 'localhost:5000', token: 'fake-token');

  final _projectionController = StreamController<RoomProjection>.broadcast();

  int turnCount = 0;
  final List<String> sentMessages = [];
  Object? sendError;

  void push(RoomProjection projection) => _projectionController.add(projection);

  @override
  Stream<RoomProjection> watch() => _projectionController.stream;

  @override
  Stream<SessionProgressEvent> watchProgress() => const Stream.empty();

  @override
  Future<SessionMetadata> getSession(String sessionId) async => SessionMetadata(
        sessionId: sessionId,
        roomDirectoryPath: '/tasks/foo',
        currentAdapter: 'claude',
        turnCount: turnCount,
        turns: const [],
      );

  @override
  Future<String> getSessionMode(String sessionId) async => 'default';

  @override
  Future<void> sendSessionMessage({
    required String sessionId,
    required String message,
    String? adapter,
    String? model,
    String? targetParticipantId,
  }) async {
    sentMessages.add(message);
    final error = sendError;
    if (error != null) throw error;
  }
}

void main() {
  RoomProjection projection() => RoomProjection(
        directoryPath: '/tasks/foo',
        sessionId: 'sess-1',
        workflowTemplateId: 'chat',
        status: 'Running',
        stepDefinitions: const [],
        steps: const [],
        executions: const [],
        workerAdapters: const {},
      );

  Future<_ThinkingTimeFakeDaemonClient> pumpChatScreen(WidgetTester tester) async {
    final client = _ThinkingTimeFakeDaemonClient();
    await tester.pumpWidget(MaterialApp(
      home: ChatScreen(client: client, sessionId: 'sess-1', directoryPath: '/tasks/foo'),
    ));
    await tester.pumpAndSettle();
    return client;
  }

  // #483, parity with desktop ChatViewModel.FormatThinkingTime: reported once, after the turn
  // completes, never as a live counter -- and never at all below the 10-second threshold.
  group('Thinking time caption (#483)', () {
    tearDown(() => debugThinkingTimeClock = DateTime.now);

    testWidgets('a turn completing 34s later shows the formatted caption, not a live counter',
        (tester) async {
      final start = DateTime(2026, 1, 1, 12, 0, 0);
      debugThinkingTimeClock = () => start;
      final client = await pumpChatScreen(tester);

      await tester.enterText(find.byType(TextField), 'Do something');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      // Never a live counter -- see ChatViewModelTests' matching desktop assertion.
      expect(find.textContaining('Thought for'), findsNothing);

      debugThinkingTimeClock = () => start.add(const Duration(seconds: 34));
      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();

      expect(find.text('Thought for 34s'), findsOneWidget);
    });

    testWidgets('a turn completing 2m 5s later uses the minute-formatting branch',
        (tester) async {
      final start = DateTime(2026, 1, 1, 12, 0, 0);
      debugThinkingTimeClock = () => start;
      final client = await pumpChatScreen(tester);

      await tester.enterText(find.byType(TextField), 'Do something');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      debugThinkingTimeClock = () => start.add(const Duration(seconds: 125));
      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();

      expect(find.text('Thought for 2m 5s'), findsOneWidget);
    });

    testWidgets('a turn completing well under the threshold shows no thinking-time caption',
        (tester) async {
      final client = await pumpChatScreen(tester);

      await tester.enterText(find.byType(TextField), 'Do something');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();

      expect(find.textContaining('Thought for'), findsNothing);
    });

    testWidgets('a failed dispatch shows no thinking-time caption', (tester) async {
      final client = await pumpChatScreen(tester);
      client.sendError = Exception('network error');

      await tester.enterText(find.byType(TextField), 'Do something');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      expect(find.textContaining('Thought for'), findsNothing);
    });
  });
}
