import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/chat_screen.dart';
import 'package:aer_mobile/daemon/models.dart';
import 'package:aer_mobile/daemon/permission_decision_kind.dart';
import 'package:aer_mobile/daemon/permission_grant_wording.dart';

/// Widget coverage for the mobile inline permission gate (0022, decision 0052, #390's mobile
/// phase) — the Flutter counterpart of Aer.Ui.Core's `PendingPermissionViewModel`/`ChatView.axaml`
/// card coverage. Pumps [PermissionGateCard] directly (no live daemon/WebSocket needed — see that
/// class's own doc comment for why it's a standalone widget).
void main() {
  PendingPermission shellPermission({required String toolName, required String toolInputJson}) => PendingPermission(
        permissionRequestId: 'perm-1',
        workerId: 'worker-1',
        vendorTag: 'claude',
        toolName: toolName,
        toolInputJson: toolInputJson,
        category: 'Shell',
        askedAt: DateTime.utc(2026, 8, 9, 12),
      );

  Future<void> pump(WidgetTester tester, PendingPermission pending, {bool enabled = true, ValueChanged<String>? onAnswer}) =>
      tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: PermissionGateCard(pending: pending, enabled: enabled, onAnswer: onAnswer ?? (_) {}),
        ),
      ));

  group('PermissionGateCard — a command whose family derives (0022 04-mockup rung set)', () {
    final pending = shellPermission(toolName: 'Bash', toolInputJson: '{"command":"rm -rf build/"}');

    testWidgets('renders the prompt and every rung, family-scoped ones included', (tester) async {
      await pump(tester, pending);

      expect(find.text('claude wants to run: rm -rf build/'), findsOneWidget);
      expect(find.text('Allow once'), findsOneWidget);
      expect(find.text('Deny once'), findsOneWidget);
      expect(find.text('Allow rm in this room'), findsOneWidget);
      expect(find.text('Allow any command in this room'), findsOneWidget);
      expect(find.text(allowRoomShellGrantReaches), findsOneWidget);
      expect(find.text('Always deny rm'), findsOneWidget);
    });

    testWidgets('the cross-room rung ("any this command in any room") is absent (0052)', (tester) async {
      await pump(tester, pending);

      expect(find.textContaining('any room'), findsNothing);
      expect(find.textContaining('Any rm in any room'), findsNothing);
    });

    testWidgets('each rung answers with the exact PermissionDecisionKind string', (tester) async {
      final answers = <String>[];
      await pump(tester, pending, onAnswer: answers.add);

      await tester.tap(find.text('Allow once'));
      await tester.tap(find.text('Deny once'));
      await tester.tap(find.text('Allow rm in this room'));
      await tester.tap(find.text('Allow any command in this room'));
      await tester.tap(find.text('Always deny rm'));
      await tester.pump();

      expect(answers, [
        PermissionDecisionKind.allowOnce,
        PermissionDecisionKind.deny,
        PermissionDecisionKind.allowCommandInRoom,
        PermissionDecisionKind.allowRoom,
        PermissionDecisionKind.denyAlways,
      ]);
    });

    testWidgets('disables every rung while an answer is in flight (no double-submit)', (tester) async {
      final answers = <String>[];
      await pump(tester, pending, enabled: false, onAnswer: answers.add);

      for (final label in [
        'Allow once',
        'Deny once',
        'Allow rm in this room',
        'Allow any command in this room',
        'Always deny rm',
      ]) {
        await tester.tap(find.text(label));
      }
      await tester.pump();

      expect(answers, isEmpty);
    });
  });

  group('PermissionGateCard — command family cannot be derived (fail-closed)', () {
    testWidgets('a non-shell tool hides the family rungs but keeps the room-ceiling rung', (tester) async {
      final pending = shellPermission(toolName: 'Edit', toolInputJson: '{"file_path":"x.txt"}');
      await pump(tester, pending);

      expect(find.text('claude wants to use Edit'), findsOneWidget);
      expect(find.text('Allow any command in this room'), findsOneWidget);
      expect(find.textContaining('Always deny'), findsNothing);
    });

    testWidgets('a command-substitution command line hides the family rungs (the metacharacter fail-closed)', (tester) async {
      // The case the task calls out explicitly: $(...) must not derive a bogus family.
      final pending = shellPermission(toolName: 'Bash', toolInputJson: '{"command":"\$(whoami)"}');
      await pump(tester, pending);

      expect(find.text(r'claude wants to run: $(whoami)'), findsOneWidget);
      expect(find.text('Allow any command in this room'), findsOneWidget);
      expect(find.textContaining('Always deny'), findsNothing);
    });
  });
}
