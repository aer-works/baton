import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';
import 'package:aer_mobile/rooms_screen.dart';

/// Bulk select (issue #288) widget-level coverage — the Flutter counterpart of
/// `Aer.Ui.Tests`' `RoomsViewModelTests.cs`. Exercises long-press-to-select, the bulk archive/delete
/// app bar actions, and the "Delete N tasks?" confirm, all against a [MockClient] rather than a real
/// daemon (same approach `daemon_client_rooms_test.dart` already uses for the single-item calls this
/// screen was built on).
void main() {
  Map<String, dynamic> fleetItemJson(String path, {bool archived = false, String? status}) => {
        'roomDirectoryPath': path,
        'friendlyName': path.split('/').last,
        'typeLabel': 'solo-run-template',
        'statusText': status ?? 'Idle',
        'status': status,
        'pausedStepCount': 0,
        'isArchived': archived,
      };

  group('RoomsScreen bulk select (#288)', () {
    testWidgets('Long-pressing a card enters selection mode and shows the selected count', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      expect(find.text('Rooms'), findsOneWidget);
      expect(find.byType(Checkbox), findsNothing);

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();

      expect(find.text('1 selected'), findsOneWidget);
      expect(find.byType(Checkbox), findsNWidgets(2));
    });

    testWidgets('Archive selected only archives the selected, not-yet-archived items and exits selection mode', (tester) async {
      final archiveRequests = <String>[];
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        if (request.method == 'POST' && request.url.path == '/api/rooms/archive') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          archiveRequests.add(body['directoryPath'] as String);
          return http.Response('', 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Archive selected'));
      await tester.pumpAndSettle();

      expect(archiveRequests, ['/tasks/a']);
      expect(find.text('Rooms'), findsOneWidget);
      expect(find.byType(Checkbox), findsNothing);
    });

    testWidgets('Delete selected asks one confirm naming the count, then deletes every selected item', (tester) async {
      final deleteRequests = <String>[];
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        if (request.method == 'POST' && request.url.path == '/api/rooms/delete') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          deleteRequests.add(body['directoryPath'] as String);
          return http.Response('', 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();
      await tester.tap(find.byType(Checkbox).last);
      await tester.pumpAndSettle();
      expect(find.text('2 selected'), findsOneWidget);

      await tester.tap(find.byTooltip('Delete selected'));
      await tester.pumpAndSettle();

      // One confirm for the whole batch, not one per item.
      expect(find.text('Delete 2 rooms?'), findsOneWidget);
      expect(deleteRequests, isEmpty);

      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      expect(deleteRequests, unorderedEquals(['/tasks/a', '/tasks/b']));
      expect(find.text('Rooms'), findsOneWidget);
    });

    testWidgets('Cancelling the bulk delete confirm deletes nothing', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a')]), 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Delete selected'));
      await tester.pumpAndSettle();
      expect(find.text('Delete 1 room?'), findsOneWidget);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      // Still in selection mode with the same selection -- cancelling the confirm is not the same
      // as exiting selection mode.
      expect(find.text('1 selected'), findsOneWidget);
    });
  });

  group('Decision 0018 attention bands (#1133)', () {
    test('attentionBand maps status strings to four bands with unknown to band 2', () {
      expect(attentionBand('NeedsYou'), 0);
      expect(attentionBand('Running'), 1);
      expect(attentionBand('Finished'), 2);
      expect(attentionBand('Failed'), 2);
      expect(attentionBand('UnknownState'), 2);
      expect(attentionBand('Cancelled'), 3);
      expect(attentionBand('Unavailable'), 3);
      expect(attentionBand('OutOfPlan'), 3);
    });

    testWidgets('RoomsScreen partitions items by attention bands 0-3 preserving recency within bands', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          final items = [
            fleetItemJson('/tasks/cancelled', status: 'Cancelled'),
            fleetItemJson('/tasks/needs', status: 'NeedsYou'),
            fleetItemJson('/tasks/running', status: 'Running'),
            fleetItemJson('/tasks/finished', status: 'Finished'),
            fleetItemJson('/tasks/outofplan', status: 'OutOfPlan'),
          ];
          return http.Response(jsonEncode(items), 200);
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      final state = tester.state(find.byType(RoomsScreen)) as dynamic;
      final paths = (state.itemsForTests as List<RoomFleetItem>).map((i) => i.roomDirectoryPath).toList();
      expect(paths, ['/tasks/needs', '/tasks/running', '/tasks/finished', '/tasks/cancelled', '/tasks/outofplan']);
    });
  });
}
