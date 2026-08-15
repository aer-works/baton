import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';
import 'package:aer_mobile/daemon/recorded_decision_wording.dart';
import 'package:aer_mobile/paused_step_card.dart';
import 'package:aer_mobile/room_stopped_card.dart';

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

  /// Directories this screen asked the daemon to open — the doorbell that makes the current
  /// projection arrive over the socket.
  final List<String> openedRooms = [];

  @override
  Future<void> openRoom(String directoryPath) async => openedRooms.add(directoryPath);

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

    testWidgets('asks the daemon to push this room, so a paused room is not opened empty', (tester) async {
      // Found by driving the built app on a device: without this the screen subscribes to a socket
      // and then waits for a push that will never come, because the change that would produce one is
      // the decision the person opened the room to make. Every other test here hands the projection
      // in directly, so none of them could see it — this is the one that asks where a projection
      // comes from in the first place.
      final client = await pumpWorkflowRoom(tester);

      expect(client.openedRooms, ['/tasks/foo']);
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

      // And the rungs come back. The second reader caught that stopping at the line above would
      // pass just as happily against a card that never re-enables — drop the `finally`'s
      // `_pendingStepIds.remove`, or remove the wrong id, and a person would be left looking at a
      // dead card with no way to answer it.
      expect(
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Approve')).onPressed,
        isNotNull,
      );
      expect(
        tester.widget<TextButton>(find.widgetWithText(TextButton, 'Reject')).onPressed,
        isNotNull,
      );
    });

    testWidgets('the composer is present but disabled, and says why', (tester) async {
      await pumpWorkflowRoom(tester);

      // Present, not absent — the fork and why it went this way are on the composer itself in
      // chat_screen.dart.
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

  /// #1236: `02-screens.md:370` draws the phone room header as `‹ aer-flow    claude + agy` — the
  /// room's name and its workers, and neither a Shape control nor a Workflow switch.
  group('The phone room header (#1236)', () {
    testWidgets('names the room, and its workers beside it', (tester) async {
      final client = await pumpWorkflowRoom(tester);

      // The name is there before any projection arrives: it comes from the room you tapped, not from
      // something that has to be fetched first.
      expect(find.text('foo'), findsOneWidget);

      client.push(workflowProjection());
      await tester.pumpAndSettle();

      expect(find.text('foo'), findsOneWidget);
      expect(find.descendant(of: find.byType(AppBar), matching: find.text('claude')), findsOneWidget);
    });

    testWidgets('two workers read as the corpus writes them', (tester) async {
      final client = await pumpWorkflowRoom(tester);

      client.push(RoomProjection(
        directoryPath: '/tasks/foo',
        sessionId: null,
        workflowTemplateId: 'draft-review',
        status: 'Paused',
        stepDefinitions: [StepDefinition(stepId: 'review', worker: 'critic', supersedeTargets: const ['draft'])],
        steps: [WorkflowStepState(stepId: 'review', status: 'Paused', latestExecutionId: 'exec-1')],
        executions: [ExecutionArtifacts(executionId: 'exec-1', worker: 'critic', outputFiles: const ['review.md'])],
        // Three workers, two adapters, one of them twice — the label is the distinct adapters, in
        // first-appearance order, so it neither repeats nor reshuffles between refreshes.
        workerAdapters: const {'drafter': 'claude', 'critic': 'agy', 'editor': 'claude'},
      ));
      await tester.pumpAndSettle();

      expect(find.descendant(of: find.byType(AppBar), matching: find.text('claude + agy')), findsOneWidget);
    });

    testWidgets('a long name and a long worker label degrade rather than overflow', (tester) async {
      // Found by the second reader: only the name was Flexible, so with `actions` taking part of the
      // row the worker label had nothing to shrink into and Flutter painted overflow stripes. A
      // narrow screen with a long name is the same squeeze a third vendor or a large system font
      // would produce. An overflow makes the test fail on its own — Flutter reports it as an error.
      tester.view.physicalSize = const Size(320, 640);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      final client = _FakeDaemonClient();
      await tester.pumpWidget(MaterialApp(
        home: ChatScreen(client: client, sessionId: null, directoryPath: '/tasks/a-room-with-a-genuinely-long-name'),
      ));
      await tester.pumpAndSettle();

      client.push(RoomProjection(
        directoryPath: '/tasks/a-room-with-a-genuinely-long-name',
        sessionId: null,
        workflowTemplateId: 'draft-review',
        status: 'Paused',
        stepDefinitions: const [],
        steps: const [],
        executions: const [],
        workerAdapters: const {'a': 'claude', 'b': 'agy', 'c': 'some-third-vendor-with-a-long-name'},
      ));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
    });

    testWidgets('a room whose workers are not known yet shows a name and no separator', (tester) async {
      final client = await pumpWorkflowRoom(tester);

      client.push(RoomProjection(
        directoryPath: '/tasks/foo',
        sessionId: null,
        workflowTemplateId: 'draft-review',
        status: 'Paused',
        stepDefinitions: const [],
        steps: const [],
        executions: const [],
        workerAdapters: const {},
      ));
      await tester.pumpAndSettle();

      // Exactly one Text in the title itself: the name. A helper that joined an empty list would
      // render a second, empty one beside it — which no assertion on the visible characters can see.
      // Scoped to the title Row rather than the whole AppBar, so the mode indicator in `bottom:`
      // cannot make this pass or fail for a reason that has nothing to do with the header's workers.
      final titleRow = find.descendant(of: find.byType(AppBar), matching: find.byType(Row));
      expect(find.descendant(of: titleRow, matching: find.byType(Text)), findsOneWidget);
      expect(find.text('foo'), findsOneWidget);
    });
  });

  /// #1240: a room that has stopped says so, and the decisions already answered in it stay readable
  /// as history. Both are ports of shipped desktop behaviour, so what these tests are really pinning
  /// is that the phone says the same words about the same state — the disagreement #461/#976/#1219
  /// each cost a fix.
  ///
  /// The projection is built by parsing the real WS wire fixture rather than by hand, so a rename of
  /// either sibling on the daemon side reddens here instead of silently rendering nothing.
  group('A room that has stopped, and the decisions it already answered (#1240)', () {
    final wireFixture =
        jsonDecode(File('test/fixtures/wire/room_projection.ws.json').readAsStringSync()) as Map<String, dynamic>;

    RoomProjection fixtureProjection({Object? roomCardStatus = _unset}) {
      final json = Map<String, dynamic>.from(wireFixture);
      json['DirectoryPath'] = '/tasks/foo';
      json['SessionId'] = null;
      if (roomCardStatus != _unset) {
        if (roomCardStatus == null) {
          json.remove('RoomCardStatus');
        } else {
          json['RoomCardStatus'] = roomCardStatus;
        }
      }
      return RoomProjection.fromJson(json);
    }

    testWidgets('a finished room says it finished, with no offer it cannot honor', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Finished'));
      await tester.pumpAndSettle();

      expect(find.text('This room has finished'), findsOneWidget);
      // Headline-only: the desktop's body sentence is a caption for its Run-it-again button, and
      // this surface has no run flow. Asserting its absence is what keeps the subset deliberate —
      // paste the desktop body in "for parity" and this reddens.
      expect(find.textContaining('Run it again'), findsNothing);
      // Buttonless — scoped to this card, since the fixture's paused step legitimately renders its
      // own rungs elsewhere in the same transcript.
      expect(
        find.descendant(of: find.byType(RoomStoppedCard), matching: find.byType(ButtonStyleButton)),
        findsNothing,
      );
    });

    testWidgets('a cancelled room is not told it finished', (tester) async {
      // The #461 sentence, one platform over: "finished" over a room you just stopped.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Cancelled'));
      await tester.pumpAndSettle();

      expect(find.text('You stopped this room'), findsOneWidget);
      expect(find.text('This room has finished'), findsNothing);
    });

    testWidgets('a room whose process died says nothing is running it', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Stopped'));
      await tester.pumpAndSettle();

      expect(find.text('This room stopped mid-run'), findsOneWidget);
      expect(find.text('Nothing is running it and it is not waiting on you.'), findsOneWidget);
      // The desktop's third sentence describes Resume, a control this card does not have.
      expect(find.textContaining('Resume picks it up'), findsNothing);
    });

    testWidgets('a failed room is left to the failed-step banner, not given a terminal card', (tester) async {
      // `DeriveRoomStoppedReason` returns null for Failed on the desktop for the same reason. The
      // phone's own blank-Failed-room defect is #1245 and is not this card's to fix.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Failed'));
      await tester.pumpAndSettle();

      expect(find.byType(RoomStoppedCard), findsNothing);
    });

    testWidgets('a push with no derived status announces no ending', (tester) async {
      // The discriminating case for the whole feature: a daemon older than this app sends no
      // sibling, and unknown must render as nothing rather than as Finished. Also the shape of the
      // one frame this app already receives without siblings — the REST body of /api/rooms/open.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: null));
      await tester.pumpAndSettle();

      expect(find.byType(RoomStoppedCard), findsNothing);
    });

    testWidgets('a decision already answered stays in the transcript as history', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection());
      await tester.pumpAndSettle();

      // The fixture carries one Resume decision. "Approved" is `PlainLanguage.ForDecision`'s word
      // for it — not "Resumed", and not the raw wire value.
      expect(find.text('Approved'), findsOneWidget);
    });

    test('the sent-back row names its target, and only it does', () {
      // The grammar rule from `PlainLanguage.ForRecordedDecision`: only Supersede takes a target,
      // or another verb would one day read "Approved to review".
      String wording(String type, {String? target}) =>
          formatRecordedDecisionWording(RecordedDecisionMoment(
            decisionId: 'dec-1',
            decisionType: type,
            targetStepId: target,
            recordedAt: DateTime.utc(2026, 8, 3),
          ));

      expect(wording('Resume'), 'Approved');
      expect(wording('Reject'), 'Rejected');
      expect(wording('RetryWithRevision'), 'Retry requested');
      expect(wording('Supersede', target: 'draft'), 'Sent back to draft');
      expect(wording('Resume', target: 'draft'), 'Approved');
      // A daemon newer than this app renders its raw value rather than throwing, which would blank
      // the whole transcript over one row.
      expect(wording('SomethingNewerThanThisApp'), 'SomethingNewerThanThisApp');
    });
  });
}

/// Sentinel for "the caller said nothing", so a test can distinguish leaving the fixture's own value
/// alone from deliberately removing the field.
const Object _unset = Object();
