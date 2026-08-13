import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/daemon/models.dart';

Map<String, dynamic> loadFixture(String relativePath) {
  final file = File(relativePath);
  return jsonDecode(file.readAsStringSync()) as Map<String, dynamic>;
}

void main() {
  // Aer.Daemon serializes REST responses camelCase and WS pushes PascalCase (see models.dart's
  // doc comment) — both are loaded from checked-in golden contract fixtures emitted by the daemon's
  // real serializer options (issue #953).
  final fixtures = {
    'camelCase (REST)': loadFixture('test/fixtures/wire/room_projection.rest.json'),
    'PascalCase (WS)': loadFixture('test/fixtures/wire/room_projection.ws.json'),
  };

  for (final entry in fixtures.entries) {
    test('RoomProjection.fromJson parses ${entry.key} wire fixture correctly', () {
      final projection = RoomProjection.fromJson(entry.value);

      expect(projection.directoryPath, 'C:/tasks/foo');
      expect(projection.sessionId, 'session-123');
      expect(projection.workflowTemplateId, 'golden-wire-contract');
      expect(projection.status, 'Paused');
      expect(projection.pausedSteps, hasLength(1));

      final step = projection.pausedSteps.single;
      expect(step.stepId, 'critic');
      expect(step.latestExecutionId, 'exec-2');

      final definition = projection.definitionFor(step.stepId);
      expect(definition?.worker, 'agy');
      expect(definition?.supersedeTargets, ['architect']);

      final execution = projection.executionFor(step.latestExecutionId);
      expect(execution?.outputFiles, ['review.md']);

      // Issue #606's failure fields: reason and failureClassification
      final failedStep = projection.steps.firstWhere((s) => s.stepId == 'coder');
      expect(failedStep.status, 'Failed');
      expect(failedStep.latestExecutionId, 'exec-3');
      expect(failedStep.latestFailureReason, 'Syntax error on line 42');
      expect(failedStep.latestFailureClassification, 'Permanent');

      // #1142: the fixture carries one answered and one expired permission entry, so the parse of
      // both PermissionAnswer shapes is pinned against the daemon's real serializer output.
      expect(projection.permissionAnswers, hasLength(2));
      expect(projection.permissionAnswers[0].decisionKind, 'AllowOnce');
      expect(projection.permissionAnswers[0].toolName, 'Bash');
      expect(projection.permissionAnswers[0].wasRevoked, isFalse);
      expect(projection.permissionAnswers[1].wasRevoked, isTrue);
      expect(projection.permissionAnswers[1].reason, 'turn_ended');

      // #1178: the fixture carries one entered and one cleared dormancy transition.
      expect(projection.dormancyTransitions, hasLength(2));
      expect(projection.dormancyTransitions[0].isEntered, isTrue);
      expect(projection.dormancyTransitions[0].consecutiveFailures, 3);
      expect(projection.dormancyTransitions[0].detail, 'The last three turns tried to fix build');
      expect(projection.dormancyTransitions[1].isEntered, isFalse);
      expect(projection.dormancyTransitions[1].clearedBy, 'operator');
      expect(projection.isDormant, isFalse);
    });

    test('parses absent dormancyTransitions as empty list', () {
      final projection = RoomProjection.fromJson({
        'snapshot': {'workflowTemplateId': 'wf', 'steps': <dynamic>[]},
        'state': {'status': 'Paused', 'steps': <dynamic>[]},
      });
      expect(projection.dormancyTransitions, isEmpty);
      expect(projection.isDormant, isFalse);
    });
  }

  final fleetFixtures = {
    'camelCase (REST)': loadFixture('test/fixtures/wire/fleet_item.rest.json'),
    'PascalCase (WS)': loadFixture('test/fixtures/wire/fleet_item.ws.json'),
  };

  for (final entry in fleetFixtures.entries) {
    test('RoomFleetItem.fromJson parses ${entry.key} wire fixture correctly', () {
      final item = RoomFleetItem.fromJson(entry.value);

      expect(item.roomDirectoryPath, 'C:/Users/pbree/.aer/tasks/foo');
      expect(item.friendlyName, 'foo');
      expect(item.typeLabel, 'solo-run-template');
      expect(item.statusText, 'Waiting for your review');
      expect(item.pausedStepCount, 2);
      // Pins the RoomCardStatus enum's wire name across the C#->Dart boundary (#1049): the
      // switcher's waiting-on-you-first sort compares to this literal, and nothing else guards it.
      expect(item.status, 'NeedsYou');
      expect(item.isArchived, isFalse);
      // The wire fixture is a workflow room, which carries no session id (#1044). Populated-id
      // parse is covered by the dedicated test below, since the fixture can't be both.
      expect(item.sessionId, isNull);
    });
  }

  group('RoomProjection.pendingPermission (0022, #390 mobile phase)', () {
    Map<String, dynamic> minimalProjection(Map<String, dynamic>? pendingPermissionJson, {required String key}) => {
          'snapshot': {'workflowTemplateId': 'wf', 'steps': <dynamic>[]},
          'state': {'status': 'Paused', 'steps': <dynamic>[]},
          key: ?pendingPermissionJson,
        };

    test('parses a PascalCase (WS) top-level pendingPermission sibling of state', () {
      final projection = RoomProjection.fromJson(minimalProjection(
        {
          'PermissionRequestId': 'perm-1',
          'WorkerId': 'worker-1',
          'VendorTag': 'claude',
          'ToolName': 'Bash',
          'ToolInputJson': '{"command":"rm -rf build/"}',
          'Category': 'Shell',
          'AskedAt': '2026-08-09T12:00:00Z',
        },
        key: 'PendingPermission',
      ));

      final pending = projection.pendingPermission;
      expect(pending, isNotNull);
      expect(pending!.permissionRequestId, 'perm-1');
      expect(pending.workerId, 'worker-1');
      expect(pending.vendorTag, 'claude');
      expect(pending.toolName, 'Bash');
      expect(pending.toolInputJson, '{"command":"rm -rf build/"}');
      expect(pending.category, 'Shell');
      expect(pending.askedAt, DateTime.parse('2026-08-09T12:00:00Z'));
    });

    test('parses a camelCase (REST) top-level pendingPermission sibling of state', () {
      final projection = RoomProjection.fromJson(minimalProjection(
        {
          'permissionRequestId': 'perm-2',
          'workerId': 'worker-2',
          'vendorTag': 'agy',
          'toolName': 'run_command',
          'toolInputJson': '{"CommandLine":"git status"}',
          'category': 'Shell',
          'askedAt': '2026-08-09T12:05:00Z',
        },
        key: 'pendingPermission',
      ));

      expect(projection.pendingPermission?.permissionRequestId, 'perm-2');
      expect(projection.pendingPermission?.vendorTag, 'agy');
    });

    test('is null when the projection carries no pendingPermission (the common case)', () {
      final projection = RoomProjection.fromJson(minimalProjection(null, key: 'pendingPermission'));
      expect(projection.pendingPermission, isNull);
    });
  });

  test('RoomFleetItem.fromJson reads a session row\'s sessionId (#1044 row-as-place)', () {
    // A session room's fleet entry carries the id its row taps into. The daemon serializes PascalCase
    // (RoomFleetItem.SessionId); the parser lowercases keys, so a phone reads j['sessionid'].
    final item = RoomFleetItem.fromJson({
      'RoomDirectoryPath': 'C:/Users/pbree/.aer/rooms/chat-abc',
      'FriendlyName': 'chat-abc',
      'TypeLabel': 'interactive session',
      'StatusText': 'Idle',
      'PausedStepCount': 0,
      'IsArchived': false,
      'SessionId': 'sess-123',
      'Status': 'NeedsYou',
    });
    expect(item.sessionId, 'sess-123');
    expect(item.status, 'NeedsYou');
  });

  group('SessionTurn.fromJson (errorMessage parse)', () {
    test('parses camelCase errorMessage', () {
      final turn = SessionTurn.fromJson({
        'turnIndex': 1,
        'vendor': 'claude',
        'humanMessage': 'Do work',
        'assistantResponse': null,
        'executedAt': '2026-08-13T08:00:00Z',
        'errorMessage': 'Process exited with code 1',
      });
      expect(turn.errorMessage, 'Process exited with code 1');
    });

    test('parses PascalCase ErrorMessage', () {
      final turn = SessionTurn.fromJson({
        'TurnIndex': 1,
        'Vendor': 'claude',
        'HumanMessage': 'Do work',
        'AssistantResponse': null,
        'ExecutedAt': '2026-08-13T08:00:00Z',
        'ErrorMessage': 'Process crashed',
      });
      expect(turn.errorMessage, 'Process crashed');
    });

    test('parses absent errorMessage as null', () {
      final turn = SessionTurn.fromJson({
        'turnIndex': 1,
        'vendor': 'claude',
        'humanMessage': 'Hello',
        'assistantResponse': 'Hi',
        'executedAt': '2026-08-13T08:00:00Z',
      });
      expect(turn.errorMessage, isNull);
    });
  });

  group('SessionTurn.fromJson (isDormancyAnswer parse, #1179)', () {
    test('parses camelCase isDormancyAnswer true', () {
      final turn = SessionTurn.fromJson({
        'turnIndex': 2,
        'vendor': 'System',
        'humanMessage': "how's it going?",
        'assistantResponse': null,
        'executedAt': '2026-08-13T08:00:00Z',
        'isDormancyAnswer': true,
      });
      expect(turn.isDormancyAnswer, isTrue);
    });

    test('parses PascalCase IsDormancyAnswer true', () {
      final turn = SessionTurn.fromJson({
        'TurnIndex': 2,
        'Vendor': 'System',
        'HumanMessage': "how's it going?",
        'AssistantResponse': null,
        'ExecutedAt': '2026-08-13T08:00:00Z',
        'IsDormancyAnswer': true,
      });
      expect(turn.isDormancyAnswer, isTrue);
    });

    test('parses absent isDormancyAnswer as false', () {
      final turn = SessionTurn.fromJson({
        'turnIndex': 1,
        'vendor': 'claude',
        'humanMessage': 'Hello',
        'assistantResponse': 'Hi',
        'executedAt': '2026-08-13T08:00:00Z',
      });
      expect(turn.isDormancyAnswer, isFalse);
    });
  });

  group('SessionTurn.fromJson (isExhausted/exhaustedUntil parse, 0026 §4/#1180)', () {
    test('parses camelCase isExhausted/exhaustedUntil', () {
      final turn = SessionTurn.fromJson({
        'turnIndex': 3,
        'vendor': 'agy',
        'humanMessage': 'keep going',
        'assistantResponse': null,
        'executedAt': '2026-08-13T08:00:00Z',
        'errorMessage': 'Individual quota reached. Resets in 1h39m10s.',
        'isExhausted': true,
        'exhaustedUntil': '2030-01-01T12:00:00Z',
      });
      expect(turn.isExhausted, isTrue);
      expect(turn.exhaustedUntil, DateTime.utc(2030, 1, 1, 12, 0));
    });

    test('parses PascalCase IsExhausted/ExhaustedUntil', () {
      final turn = SessionTurn.fromJson({
        'TurnIndex': 3,
        'Vendor': 'claude',
        'HumanMessage': 'keep going',
        'AssistantResponse': null,
        'ExecutedAt': '2026-08-13T08:00:00Z',
        'ErrorMessage': 'credits_required',
        'IsExhausted': true,
        'ExhaustedUntil': null,
      });
      expect(turn.isExhausted, isTrue);
      expect(turn.exhaustedUntil, isNull);
    });

    test('parses absent isExhausted/exhaustedUntil as false/null (old metadata, tolerant per InteractiveSessions.SessionTurn)', () {
      final turn = SessionTurn.fromJson({
        'turnIndex': 1,
        'vendor': 'claude',
        'humanMessage': 'Hello',
        'assistantResponse': 'Hi',
        'executedAt': '2026-08-13T08:00:00Z',
      });
      expect(turn.isExhausted, isFalse);
      expect(turn.exhaustedUntil, isNull);
    });
  });
}
