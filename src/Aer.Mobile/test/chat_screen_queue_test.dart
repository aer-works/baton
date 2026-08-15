import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';

class _QueueFakeDaemonClient extends DaemonClient {
  _QueueFakeDaemonClient() : super(host: 'localhost:5000', token: 'fake-token');

  final _projectionController = StreamController<RoomProjection>.broadcast();

  int turnCount = 0;
  final List<String> sentMessages = [];
  Object? sendError;
  Completer<void>? sendGate;

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
  }) async {
    sentMessages.add(message);
    final gate = sendGate;
    if (gate != null) await gate.future;
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

  Future<_QueueFakeDaemonClient> pumpChatScreen(WidgetTester tester) async {
    final client = _QueueFakeDaemonClient();
    await tester.pumpWidget(MaterialApp(
      home: ChatScreen(client: client, sessionId: 'sess-1', directoryPath: '/tasks/foo'),
    ));
    await tester.pumpAndSettle();
    return client;
  }

  group('ChatScreen mid-turn message queue (#1131, parity with #1074)', () {
    testWidgets(
        '1. Sending while a turn is in flight adds to the strip (caption + message visible), composer clears, sendSessionMessage NOT called a second time yet',
        (tester) async {
      final client = await pumpChatScreen(tester);

      // Start initial turn
      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      expect(client.sentMessages, ['First turn message']);

      // Send second message while first turn is in flight
      await tester.enterText(find.byType(TextField), 'Queued message 1');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      // Strip shows caption and queued message, composer cleared, no second dispatch
      expect(find.text('Queued — sends when the reply finishes.'), findsOneWidget);
      expect(find.text('Queued message 1'), findsOneWidget);
      final textField = tester.widget<TextField>(find.byType(TextField));
      expect(textField.controller?.text, '');
      expect(client.sentMessages, ['First turn message']);
    });

    testWidgets(
        '2. The remove button removes exactly that entry (enqueue two, remove the first, strip shows the second)',
        (tester) async {
      await pumpChatScreen(tester);

      // Turn in flight
      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      // Enqueue two messages
      await tester.enterText(find.byType(TextField), 'Queued message 1');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), 'Queued message 2');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      expect(find.text('Queued message 1'), findsOneWidget);
      expect(find.text('Queued message 2'), findsOneWidget);

      // Remove the first entry
      await tester.tap(find.byTooltip('Remove').first);
      await tester.pumpAndSettle();

      expect(find.text('Queued message 1'), findsNothing);
      expect(find.text('Queued message 2'), findsOneWidget);
    });

    testWidgets(
        '3. Turn completion (push a projection + bump turnCount) drains the head: sendSessionMessage called with it, entry gone from strip',
        (tester) async {
      final client = await pumpChatScreen(tester);

      // Start initial turn
      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      // Enqueue message
      await tester.enterText(find.byType(TextField), 'Queued message 1');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      expect(client.sentMessages, ['First turn message']);
      expect(find.text('Queued message 1'), findsOneWidget);

      // Simulate turn completion: bump turnCount and push projection
      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();

      // Drains head: sendSessionMessage called for queued message, entry removed from strip (strip caption gone)
      expect(client.sentMessages, ['First turn message', 'Queued message 1']);
      expect(find.text('Queued — sends when the reply finishes.'), findsNothing);
    });

    testWidgets(
        '4. A drained send that throws DaemonException keeps the message in the strip and does not retry on the next refresh (paused), and a fresh manual enqueue resumes draining',
        (tester) async {
      final client = await pumpChatScreen(tester);

      // Start initial turn
      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      // Enqueue message
      await tester.enterText(find.byType(TextField), 'Queued message 1');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      // Next send (drain attempt) will throw DaemonException
      client.sendError = DaemonException('connection failed');
      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();

      // Failed drained send stays queued in strip, error shown, sentMessages attempted 'Queued message 1'
      expect(find.text('Queued message 1'), findsOneWidget);
      expect(find.text('connection failed'), findsOneWidget);
      expect(client.sentMessages, ['First turn message', 'Queued message 1']);

      // Next refresh push while paused does NOT retry draining
      client.push(projection());
      await tester.pumpAndSettle();
      expect(client.sentMessages, ['First turn message', 'Queued message 1']);

      // Fresh manual enqueue resumes draining (clear error first so retry succeeds)
      client.sendError = null;
      await tester.enterText(find.byType(TextField), 'Queued message 2');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      // Drain resumed: sent head ('Queued message 1')
      expect(client.sentMessages.contains('Queued message 1'), isTrue);
    });

    testWidgets(
        '5. Two same-text entries stay two entries: removing the in-flight head mid-drain never collapses them (identity, not value)',
        (tester) async {
      final client = await pumpChatScreen(tester);

      // Turn in flight, then two identical messages queued.
      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();
      await tester.enterText(find.byType(TextField), 'dup');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), 'dup');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();
      expect(find.text('dup'), findsNWidgets(2));

      // Hold the drain's dispatch open so the head is in flight but still visible in the strip.
      client.sendGate = Completer<void>();
      client.turnCount = 1;
      client.push(projection());
      await tester.pump();
      await tester.pump();

      // Remove the in-flight head via the strip while its dispatch is held open.
      await tester.tap(find.byTooltip('Remove').first);
      await tester.pump();

      // Release the dispatch; its success must consume the (already-removed) head and NOTHING
      // else — with value-based removal the second same-text entry would vanish here instead.
      // Count strip rows via their Remove buttons: the drained head's text also appears in the
      // transcript as the pending message, so find.text can't scope this.
      client.sendGate!.complete();
      client.sendGate = null;
      await tester.pumpAndSettle();

      expect(find.byTooltip('Remove'), findsOneWidget);
    });

    testWidgets(
        '6. A raw transport exception (not DaemonException) on a drained send does not strand _isSending: the entry stays queued and the composer still works (#1131 review)',
        (tester) async {
      final client = await pumpChatScreen(tester);

      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();
      await tester.enterText(find.byType(TextField), 'Queued message 1');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      // The drain's dispatch dies with a RAW exception — the kind _post does not wrap.
      client.sendError = StateError('socket died');
      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();

      // Still queued, and the composer is not jammed: a fresh manual send resumes the drain.
      expect(find.text('Queued message 1'), findsOneWidget);
      client.sendError = null;
      await tester.enterText(find.byType(TextField), 'After recovery');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();
      expect(client.sentMessages.contains('Queued message 1'), isTrue);
    });

    // A contract pin, not a red-provable discriminator — the hold is structural (why: the send
    // timeout Timer's comment in chat_screen.dart). The pin keeps a future refactor from adding a
    // post-timeout auto-drain without noticing this test.
    testWidgets(
        '7. After the 5-minute timeout the backlog holds: a late completion push does not auto-dispatch it, the next manual send does (#1131 review)',
        (tester) async {
      final client = await pumpChatScreen(tester);

      await tester.enterText(find.byType(TextField), 'First turn message');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();
      await tester.enterText(find.byType(TextField), 'Queued message 1');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();

      // The client-side timeout gives up on the turn; the daemon may still be running it. The
      // error must disclose that the backlog is held, or the strip reads as a hang.
      await tester.pump(const Duration(minutes: 5));
      expect(client.sentMessages, ['First turn message']);
      expect(find.textContaining('Queued messages wait for your next send'), findsOneWidget);

      // A late completion push must NOT auto-drain into a possibly-live turn.
      client.turnCount = 1;
      client.push(projection());
      await tester.pumpAndSettle();
      expect(client.sentMessages, ['First turn message']);
      expect(find.text('Queued message 1'), findsOneWidget);

      // The operator's next manual send resumes: enqueue clears the pause, then drains the head.
      await tester.enterText(find.byType(TextField), 'Resume');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pumpAndSettle();
      expect(client.sentMessages.contains('Queued message 1'), isTrue);
    });
  });

  /// #1236: the header of a session room — the arm that regressed. What it used to read and why that
  /// was wrong is recorded on `ChatScreen`'s AppBar title.
  group('The phone room header, for a session room (#1236)', () {
    testWidgets('names the room rather than the engine\'s turn count', (tester) async {
      final client = await pumpChatScreen(tester);
      client.turnCount = 4;
      client.push(projection());
      await tester.pumpAndSettle();

      expect(find.descendant(of: find.byType(AppBar), matching: find.text('foo')), findsOneWidget);
      expect(find.textContaining('turn 4'), findsNothing);
      // The adapter did not disappear — it moved out of the room's name and beside it.
      expect(find.descendant(of: find.byType(AppBar), matching: find.text('claude')), findsOneWidget);
    });
  });
}
