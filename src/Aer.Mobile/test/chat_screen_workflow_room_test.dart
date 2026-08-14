import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';
import 'package:aer_mobile/paused_step_card.dart';

/// #1226 (#1196 slice 6a): a workflow room renders in the phone's room screen — the one rendering a
/// room has — and its paused step is answered there rather than on a decision surface of its own.
///
/// The discriminating fact throughout is `sessionId: null`. Before this slice that was not a state
/// [ChatScreen] could be constructed in at all; a workflow room had to open `InboxScreen`, which
/// this slice deleted.
class _FakeDaemonClient extends DaemonClient {
  _FakeDaemonClient() : super(host: 'localhost:5000', token: 'fake-token');

  final _projectionController = StreamController<RoomProjection>.broadcast();

  /// Every decision this screen sent, in order — the point of the test being that a decision reaches
  /// the daemon from inside the transcript.
  final List<Map<String, String>> decisions = [];
  int cancelRunCallCount = 0;

  /// Set to make [decide] hang, so a test can assert the card's rungs are disabled mid-flight.
  Completer<void>? decideGate;

  /// Fails the test loudly rather than silently returning empty: a workflow room must never ask for
  /// a session, and a fake that quietly answered would hide exactly the defect this pins.
  @override
  Future<SessionMetadata> getSession(String sessionId) async =>
      throw StateError('getSession must not be called for a room with no session');

  @override
  Future<String> getSessionMode(String sessionId) async =>
      throw StateError('getSessionMode must not be called for a room with no session');

  void push(RoomProjection projection) => _projectionController.add(projection);

  @override
  Stream<RoomProjection> watch() => _projectionController.stream;

  @override
  Stream<SessionProgressEvent> watchProgress() => const Stream.empty();

  @override
  Future<String?> fetchArtifact({
    required String directoryPath,
    required String executionId,
    required String fileName,
  }) async =>
      'the draft';

  @override
  Future<void> decide({
    required String directoryPath,
    required String stepId,
    required String executionId,
    required String decisionType,
    String? targetStepId,
    String? revisionFilePath,
    Map<String, String>? artifactReference,
  }) async {
    decisions.add({
      'stepId': stepId,
      'decisionType': decisionType,
      'targetStepId': ?targetStepId,
    });
    final gate = decideGate;
    if (gate != null) await gate.future;
  }

  @override
  Future<void> cancelRun({required String directoryPath, String? executionId}) async {
    cancelRunCallCount++;
  }
}

void main() {
  RoomProjection workflowProjection({String stepStatus = 'Paused'}) => RoomProjection(
        directoryPath: '/tasks/foo',
        // No session: this is what makes it a workflow room rather than a chat.
        sessionId: null,
        workflowTemplateId: 'draft-review',
        status: 'Paused',
        stepDefinitions: [
          StepDefinition(stepId: 'review', worker: 'critic', supersedeTargets: const ['draft']),
        ],
        steps: [
          WorkflowStepState(stepId: 'review', status: stepStatus, latestExecutionId: 'exec-1'),
        ],
        executions: [
          ExecutionArtifacts(executionId: 'exec-1', worker: 'critic', outputFiles: const ['review.md']),
        ],
        workerAdapters: const {'critic': 'claude'},
      );

  Future<_FakeDaemonClient> pumpWorkflowRoom(WidgetTester tester) async {
    final client = _FakeDaemonClient();
    await tester.pumpWidget(MaterialApp(
      home: ChatScreen(client: client, sessionId: null, directoryPath: '/tasks/foo'),
    ));
    await tester.pumpAndSettle();
    return client;
  }

  group('A workflow room in the phone\'s room screen (#1226)', () {
    testWidgets('opens without asking for a session, and shows its paused step as a card', (tester) async {
      final client = await pumpWorkflowRoom(tester);

      // Before any push there is no card — and, critically, no spinner either: a room with nothing
      // to fetch must not sit in a permanent loading state, which is what the old getSession path
      // would have produced here.
      expect(find.byType(PausedStepCard), findsNothing);
      expect(find.byType(CircularProgressIndicator), findsNothing);

      client.push(workflowProjection());
      await tester.pumpAndSettle();

      expect(find.byType(PausedStepCard), findsOneWidget);
      expect(find.text('critic (claude)'), findsOneWidget);
      expect(find.text('Approve'), findsOneWidget);
    });

    testWidgets('answering the card sends the decision for that step', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Approve'));
      await tester.pumpAndSettle();

      expect(client.decisions, [
        {'stepId': 'review', 'decisionType': 'Resume'}
      ]);
    });

    testWidgets('send back names the step the shape sends it back to', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Send back to draft'));
      await tester.pumpAndSettle();

      expect(client.decisions, [
        {'stepId': 'review', 'decisionType': 'Supersede', 'targetStepId': 'draft'}
      ]);
    });

    testWidgets('a decision in flight disables the rungs so it cannot be double-submitted', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.decideGate = Completer<void>();
      client.push(workflowProjection());
      await tester.pumpAndSettle();

      await tester.tap(find.text('Approve'));
      await tester.pump();

      final approve = tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Approve'));
      expect(approve.onPressed, isNull);
      expect(tester.widget<TextButton>(find.widgetWithText(TextButton, 'Reject')).onPressed, isNull);

      client.decideGate!.complete();
      await tester.pumpAndSettle();
      expect(client.decisions.length, 1);
    });

    testWidgets('the composer is present but disabled, and says why', (tester) async {
      await pumpWorkflowRoom(tester);

      // Present, not absent — 02-screens.md:57-63. A composer that vanishes reads as a capability
      // taken away; a disabled one reads as one that has not arrived.
      final field = tester.widget<TextField>(find.byType(TextField));
      expect(field.enabled, isFalse);
      expect(find.textContaining("aren't conversational yet"), findsOneWidget);
    });

    testWidgets('the room can still be stopped, with a confirm', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection(stepStatus: 'Running'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Stop this room'));
      await tester.pumpAndSettle();

      // The confirm is load-bearing: the button stops the whole room, not the step it sits beside.
      expect(find.text('This stops the whole room, not just one step.'), findsOneWidget);
      expect(client.cancelRunCallCount, 0);

      await tester.tap(find.text('Cancel run'));
      await tester.pumpAndSettle();
      expect(client.cancelRunCallCount, 1);
    });
  });
}
