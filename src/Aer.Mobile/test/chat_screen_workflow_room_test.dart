import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';
import 'package:aer_mobile/daemon/recorded_decision_wording.dart';
import 'package:aer_mobile/failed_step_card.dart';
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

  /// Left null for every workflow-room test, which is what makes the two throws below load-bearing:
  /// a workflow room must never ask for a session, and a fake that quietly answered would hide
  /// exactly the defect #1226 pins. Set only where a test needs the *session* rendering to compare
  /// against — an opt-in, so no existing test loses the guard.
  SessionMetadata? sessionMetadata;

  @override
  Future<SessionMetadata> getSession(String sessionId) async =>
      sessionMetadata ?? (throw StateError('getSession must not be called for a room with no session'));

  @override
  Future<String> getSessionMode(String sessionId) async =>
      sessionMetadata != null ? 'default' : throw StateError('getSessionMode must not be called for a room with no session');

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
  RoomProjection workflowProjection({
    String stepStatus = 'Paused',
    PausePointKind pausePointKind = PausePointKind.readyForReview,
    List<String> supersedeTargets = const ['draft'],
  }) =>
      RoomProjection(
        directoryPath: '/tasks/foo',
        // No session: this is what makes it a workflow room rather than a chat.
        sessionId: null,
        workflowTemplateId: 'draft-review',
        status: 'Paused',
        stepDefinitions: [
          StepDefinition(
              stepId: 'review', worker: 'critic', supersedeTargets: supersedeTargets, pausePointKind: pausePointKind),
        ],
        steps: [
          WorkflowStepState(stepId: 'review', status: stepStatus, latestExecutionId: 'exec-1'),
        ],
        executions: [
          ExecutionArtifacts(executionId: 'exec-1', worker: 'critic', outputFiles: const ['review.md']),
        ],
        workerAdapters: const {'critic': 'claude'},
      );

  /// The real WS wire fixture, parsed rather than hand-built, so a rename of any sibling on the
  /// daemon side reddens here instead of silently rendering nothing. It carries a Running step, a
  /// Paused one, and a `Permanent`-classification Failed one — the last is what #1245's card draws.
  final wireFixture =
      jsonDecode(File('test/fixtures/wire/room_projection.ws.json').readAsStringSync()) as Map<String, dynamic>;

  /// [failedClassification] and [failedReason] rewrite the fixture's failed step in place;
  /// [withFailedStep] `false` drops it, and [withFailedStepDefinition] `false` leaves the step but
  /// removes the shape entry that names its worker.
  ///
  /// Dropping the step is what the terminal-card tests want: `HomeViewModel.DeriveStatus` reaches
  /// Failed before Finished, Cancelled, or Stopped whenever a step has failed for any reason but
  /// exhaustion, so a room carrying both is a state the daemon cannot emit — pushing one would ask
  /// those tests to assert against a projection that never arrives.
  RoomProjection fixtureProjection({
    Object? roomCardStatus = _unset,
    bool withFailedStep = true,
    bool withFailedStepDefinition = true,
    String? failedClassification,
    String? failedReason,
  }) {
    bool isFailed(Map<String, dynamic> step) => step['Status'] == 'Failed';

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

    final state = Map<String, dynamic>.from(json['State'] as Map<String, dynamic>);
    final steps =
        (state['Steps'] as List<dynamic>).map((s) => Map<String, dynamic>.from(s as Map<String, dynamic>)).toList();
    final failedIds = steps.where(isFailed).map((s) => s['StepId']).toSet();

    state['Steps'] = withFailedStep
        ? [
            for (final step in steps)
              if (!isFailed(step))
                step
              else
                {
                  ...step,
                  'LatestFailureClassification': ?failedClassification,
                  'LatestFailureReason': ?failedReason,
                },
          ]
        : steps.where((s) => !isFailed(s)).toList();
    json['State'] = state;

    if (!withFailedStepDefinition) {
      final snapshot = Map<String, dynamic>.from(json['Snapshot'] as Map<String, dynamic>);
      snapshot['Steps'] =
          (snapshot['Steps'] as List<dynamic>).where((s) => !failedIds.contains((s as Map)['StepId'])).toList();
      json['Snapshot'] = snapshot;
    }

    return RoomProjection.fromJson(json);
  }

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

  /// #1325: the phone must not offer Approve/Reject on a pause that is asking a question, not
  /// requesting sign-off on finished work — decisions 0015/0040's kind-derived affordances. The
  /// polarity pair below is deliberate: a fixture using only one kind cannot fail against the
  /// pre-#1325 code, which always rendered the same three rungs regardless of kind.
  group('A paused step\'s affordances are kind-derived (#1325)', () {
    testWidgets('a NeedsInput step offers a Reply rung, and no Approve/Reject', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection(pausePointKind: PausePointKind.needsInput));
      await tester.pumpAndSettle();

      expect(find.text('Reply'), findsOneWidget);
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
      expect(find.text('Send back to draft'), findsNothing);
    });

    testWidgets('a ReadyForReview step keeps Approve/Reject, and offers no Reply', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection(pausePointKind: PausePointKind.readyForReview));
      await tester.pumpAndSettle();

      expect(find.text('Approve'), findsOneWidget);
      expect(find.text('Reject'), findsOneWidget);
      expect(find.text('Reply'), findsNothing);
    });

    testWidgets('a NeedsInput reply is sent as Resume, worded as a reply rather than an approval', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection(pausePointKind: PausePointKind.needsInput));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Reply'));
      await tester.pumpAndSettle();

      expect(client.decisions, [
        {'stepId': 'review', 'decisionType': 'Resume'}
      ]);
      expect(find.text('Replied to review'), findsOneWidget);
      expect(find.text('Approved review'), findsNothing);
    });
  });

  /// #1322: a pause point may declare more than one supersede target, and every one of them must be
  /// reachable, not just the first. A single-target fixture (the group above's `workflowProjection`
  /// default) cannot fail against the pre-#1322 code, which rendered exactly one button regardless of
  /// how many targets were declared — this group's fixture always carries two.
  group('Every declared supersede target is reachable (#1322)', () {
    testWidgets('a pause point with two targets exposes both', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection(supersedeTargets: const ['draft', 'outline']));
      await tester.pumpAndSettle();

      expect(find.text('Send back to draft'), findsOneWidget);
      expect(find.text('Send back to outline'), findsOneWidget);
    });

    testWidgets('superseding to the second target sends the second target\'s id', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(workflowProjection(supersedeTargets: const ['draft', 'outline']));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Send back to outline'));
      await tester.pumpAndSettle();

      expect(client.decisions, [
        {'stepId': 'review', 'decisionType': 'Supersede', 'targetStepId': 'outline'}
      ]);
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
    testWidgets('a finished room says it finished, with no offer it cannot honor', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Finished', withFailedStep: false));
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
      client.push(fixtureProjection(roomCardStatus: 'Cancelled', withFailedStep: false));
      await tester.pumpAndSettle();

      expect(find.text('You stopped this room'), findsOneWidget);
      expect(find.text('This room has finished'), findsNothing);
    });

    testWidgets('a room whose process died says nothing is running it', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Stopped', withFailedStep: false));
      await tester.pumpAndSettle();

      expect(find.text('This room stopped mid-run'), findsOneWidget);
      expect(find.text('Nothing is running it and it is not waiting on you.'), findsOneWidget);
      // The desktop's third sentence describes Resume, a control this card does not have.
      expect(find.textContaining('Resume picks it up'), findsNothing);
    });

    testWidgets('a failed room is left to the failed-step card, not given a terminal one', (tester) async {
      // `DeriveRoomStoppedReason` returns null for Failed on the desktop for the same reason. Since
      // #1245 the phone has the other half, so this asserts the handover rather than a hole: the
      // failed step draws, and this card stays out of its way.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(roomCardStatus: 'Failed'));
      await tester.pumpAndSettle();

      expect(find.byType(RoomStoppedCard), findsNothing);
      expect(find.byType(FailedStepCard), findsOneWidget);
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

    testWidgets('the decision rows land in time order among the other history', (tester) async {
      // #1240's second reader: the test above carries a single decision, so it would pass against a
      // broken sort, a reversed tie precedence or a wrong clear watermark — it proves only that the
      // pipe is wired. This is the ordering half. Two decisions, deliberately pushed in the WRONG
      // order, straddling the fixture's two permission answers (13:00 and 14:00).
      final client = await pumpWorkflowRoom(tester);
      final json = Map<String, dynamic>.from(wireFixture);
      json['DirectoryPath'] = '/tasks/foo';
      json['RecordedDecisionMoments'] = [
        {'decisionId': 'dec-late', 'decisionType': 'Reject', 'recordedAt': '2026-08-03T15:45:00+00:00'},
        {'decisionId': 'dec-early', 'decisionType': 'Resume', 'recordedAt': '2026-08-03T13:30:00+00:00'},
      ];
      client.push(RoomProjection.fromJson(json));
      await tester.pumpAndSettle();

      List<String> transcript() => tester
          .widgetList<Text>(find.descendant(of: find.byType(ListView), matching: find.byType(Text)))
          .map((t) => t.data ?? '')
          .toList();

      final rows = transcript();
      // Approved (13:30) sits between the 13:00 answer and the 14:00 one; Rejected (15:45) after both.
      expect(rows.indexOf('Allowed once — Bash'), lessThan(rows.indexOf('Approved')));
      expect(rows.indexOf('Approved'), lessThan(rows.indexOf('Expired unanswered — turn ended')));
      expect(rows.indexOf('Expired unanswered — turn ended'), lessThan(rows.indexOf('Rejected')));
    });

    testWidgets('a room that stopped with a gate still live shows both, gate first', (tester) async {
      // #1240's second reader: the index arithmetic has to hold when the gate and the card are BOTH
      // present, and no test covered that pair. It is a real state, not a hypothetical —
      // `RoomCardViewModel.DeriveStatus`' own remarks describe an orphaned ask sitting in the journal
      // beside a terminal flow state.
      final client = await pumpWorkflowRoom(tester);
      final json = Map<String, dynamic>.from(wireFixture);
      json['DirectoryPath'] = '/tasks/foo';
      json['RoomCardStatus'] = 'Finished';
      json['PendingPermission'] = {
        'permissionRequestId': 'perm-live',
        'workerId': 'critic',
        'vendorTag': 'claude',
        'toolName': 'Bash',
        'toolInputJson': '{"command":"ls"}',
        'category': 'run_command',
        'askedAt': '2026-08-03T16:00:00+00:00',
      };
      client.push(RoomProjection.fromJson(json));
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull);
      expect(find.byType(PermissionGateCard), findsOneWidget);
      expect(find.byType(RoomStoppedCard), findsOneWidget);
      // Since #1245 the untouched fixture also carries a non-exhausted failed step, so this push is
      // now the only place all four kinds of trailing item appear at once — the hardest case for the
      // hand-rolled index arithmetic, and worth asserting rather than leaving to takeException.
      // The daemon cannot actually emit this pairing (the amendment says why); a test that renders
      // it anyway is the cheapest place to notice if that ever stops being true.
      expect(find.byType(FailedStepCard), findsOneWidget);
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

  /// #1245: a Failed workflow room used to render nothing at all. The phone's failure rendering was
  /// reachable only from the interactive-turn arm of the merge loop, and a workflow room has no
  /// turns — so the desktop's #617 banner had no counterpart here for the case that most needed one.
  ///
  /// Same wire fixture as the group above, and for the same reason: it already carries a Failed
  /// step, so these tests read the field names the daemon actually sends rather than ones written
  /// twice.
  group('A failed step in a workflow room (#1245)', () {
    testWidgets('says which step failed, which worker, and why', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection());
      await tester.pumpAndSettle();

      expect(find.byType(FailedStepCard), findsOneWidget);
      expect(find.text('coder (agy) failed — Syntax error on line 42'), findsOneWidget);
    });

    testWidgets('an out-of-plan step is not called failed', (tester) async {
      // #1116's must-fix, one platform over: an ExhaustedUntil step is waiting on quota, not broken,
      // and 0026's whole point is that the wait reads as calm. A red "failed" card would un-say it.
      // This is the discriminating arm — a test that only checked "Failed renders" would stay green
      // against a version that dropped the carve-out.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(failedClassification: 'ExhaustedUntil'));
      await tester.pumpAndSettle();

      expect(find.byType(FailedStepCard), findsNothing);
    });

    testWidgets('the stderr tail is shown as an excerpt, not folded into the sentence', (tester) async {
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(failedReason: 'Worker exited with non-zero code 1. stderr: cc: no such file'));
      await tester.pumpAndSettle();

      expect(find.text('coder (agy) failed — Worker exited with non-zero code 1.'), findsOneWidget);
      expect(find.text('cc: no such file'), findsOneWidget);
    });

    testWidgets('a step the shape does not name a worker for drops the clause rather than inventing one', (tester) async {
      // The fallback that reads as a worker called "coder" is the one this rules out — a name the
      // person would then go looking for in a room that has none.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection(withFailedStepDefinition: false));
      await tester.pumpAndSettle();

      expect(find.text('coder failed — Syntax error on line 42'), findsOneWidget);
    });

    testWidgets('carries no offer it cannot honor', (tester) async {
      // Which of the desktop banner's parts cross to a surface with no run flow is the 2026-08-15
      // amendment's clause; this pins the half it subtracts. Scoped to this card, since the
      // fixture's paused step legitimately renders its own rungs in the same transcript.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection());
      await tester.pumpAndSettle();

      expect(
        find.descendant(of: find.byType(FailedStepCard), matching: find.byType(ButtonStyleButton)),
        findsNothing,
      );
    });

    testWidgets('sits alongside the paused step rather than replacing it', (tester) async {
      // The index arithmetic in _buildBody is hand-rolled across five kinds of item, so a room
      // holding both is what catches an off-by-one that a single-card fixture cannot.
      final client = await pumpWorkflowRoom(tester);
      client.push(fixtureProjection());
      await tester.pumpAndSettle();

      expect(find.byType(PausedStepCard), findsOneWidget);
      expect(find.byType(FailedStepCard), findsOneWidget);
    });

    testWidgets('a session room never draws one, however its steps read', (tester) async {
      // Session rooms take the turn arm that already had a failure rendering; drawing here too would
      // put two failure cards on one screen.
      final client = _FakeDaemonClient()
        ..sessionMetadata = SessionMetadata(
          sessionId: 'sess-1',
          roomDirectoryPath: '/tasks/foo',
          currentAdapter: 'claude',
          turnCount: 0,
          turns: const [],
        );
      await tester.pumpWidget(MaterialApp(
        home: ChatScreen(client: client, sessionId: 'sess-1', directoryPath: '/tasks/foo'),
      ));
      await tester.pumpAndSettle();
      client.push(fixtureProjection());
      await tester.pumpAndSettle();

      expect(find.byType(FailedStepCard), findsNothing);
    });
  });
}

/// Sentinel for "the caller said nothing", so a test can distinguish leaving the fixture's own value
/// alone from deliberately removing the field.
const Object _unset = Object();
