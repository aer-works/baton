import 'dart:convert';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/permission_decision_kind.dart';

/// Mobile counterpart of desktop's `RoomClient.Permissions.cs` `AnswerPermissionAsync` coverage
/// (0022, #390's mobile phase): posts to the same endpoint with the same body shape.
void main() {
  group('DaemonClient.answerPermission', () {
    test('posts to /api/rooms/permissions/answer with the answer body', () async {
      final mockClient = MockClient((request) async {
        expect(request.method, 'POST');
        expect(request.url.path, '/api/rooms/permissions/answer');
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['directoryPath'], 'C:/tasks/foo');
        expect(body['permissionRequestId'], 'perm-1');
        expect(body['decisionKind'], PermissionDecisionKind.allowOnce);
        expect(body['reason'], isNull);
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.answerPermission(
        directoryPath: 'C:/tasks/foo',
        permissionRequestId: 'perm-1',
        decisionKind: PermissionDecisionKind.allowOnce,
      );
    });

    test('carries an optional reason (e.g. a denial\'s message, 0022 §3)', () async {
      final mockClient = MockClient((request) async {
        final body = jsonDecode(request.body) as Map<String, dynamic>;
        expect(body['decisionKind'], PermissionDecisionKind.deny);
        expect(body['reason'], 'not now');
        return http.Response('', 200);
      });

      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await client.answerPermission(
        directoryPath: 'C:/tasks/foo',
        permissionRequestId: 'perm-1',
        decisionKind: PermissionDecisionKind.deny,
        reason: 'not now',
      );
    });

    test('throws DaemonException on a failed response', () async {
      final mockClient = MockClient((request) async => http.Response('boom', 500));
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      expect(
        () => client.answerPermission(
          directoryPath: 'C:/tasks/foo',
          permissionRequestId: 'perm-1',
          decisionKind: PermissionDecisionKind.allowOnce,
        ),
        throwsA(isA<DaemonException>()),
      );
    });
  });
}
