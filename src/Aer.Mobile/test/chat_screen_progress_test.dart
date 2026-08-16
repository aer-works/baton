import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';

class _ProgressFakeDaemonClient extends DaemonClient {
  _ProgressFakeDaemonClient() : super(host: 'localhost:5000', token: 'fake-token');

  final _projectionController = StreamController<RoomProjection>.broadcast();
  final _progressController = StreamController<SessionProgressEvent>.broadcast();

  int turnCount = 0;
  final List<String> sentMessages = [];

  void push(RoomProjection projection) => _projectionController.add(projection);

  void pushProgress(String kind, String text, {bool isPartial = false}) =>
      _progressController.add(SessionProgressEvent(
        directoryPath: '/tasks/foo',
        stepId: null,
        kind: kind,
        text: text,
        isPartial: isPartial,
      ));

  @override
  Stream<RoomProjection> watch() => _projectionController.stream;

  @override
  Stream<SessionProgressEvent> watchProgress() => _progressController.stream;

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
  }
}

void main() {
  Future<_ProgressFakeDaemonClient> pumpChatScreen(WidgetTester tester) async {
    final client = _ProgressFakeDaemonClient();
    await tester.pumpWidget(MaterialApp(
      home: ChatScreen(client: client, sessionId: 'sess-1', directoryPath: '/tasks/foo'),
    ));
    await tester.pumpAndSettle();
    return client;
  }

  // #323/#1290: discrete progress events ran together with no separator, reading as one nonsense
  // word ("Session startedrequestingPowerShellrequesting"). A continuing streaming text delta must
  // still concatenate raw — it is one sentence arriving token by token, not a series of events.
  group('Live progress strip separators (#323, parity with desktop ChatViewModel.AppendProgress)',
      () {
    testWidgets('discrete status/tool events get a separator; a partial text run does not',
        (tester) async {
      final client = await pumpChatScreen(tester);

      await tester.enterText(find.byType(TextField), 'Do something');
      await tester.tap(find.byIcon(Icons.send));
      await tester.pump();

      client.pushProgress('status', 'Session started');
      client.pushProgress('status', 'requesting');
      client.pushProgress('tool', 'PowerShell');
      client.pushProgress('text', 'Thinking', isPartial: true);
      client.pushProgress('text', ' some more...', isPartial: true);
      client.pushProgress('status', 'requesting');
      await tester.pump();

      expect(
        find.text('Session started · requesting · PowerShell · Thinking some more... · requesting'),
        findsOneWidget,
      );
    });
  });
}
