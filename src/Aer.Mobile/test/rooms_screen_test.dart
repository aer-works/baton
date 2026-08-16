import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'package:aer_mobile/daemon/daemon_client.dart';
import 'package:aer_mobile/daemon/models.dart';
import 'package:aer_mobile/rooms_screen.dart';
import 'package:aer_mobile/theme/tokens.dart';

/// Bulk select (issue #288) widget-level coverage — the Flutter counterpart of
/// `Aer.Ui.Tests`' `RoomsViewModelTests.cs`. Exercises long-press-to-select, the bulk archive/delete
/// app bar actions, and the "Delete N tasks?" confirm, all against a [MockClient] rather than a real
/// daemon (same approach `daemon_client_rooms_test.dart` already uses for the single-item calls this
/// screen was built on).
void main() {
  Map<String, dynamic> fleetItemJson(String path, {bool archived = false, String? status, String? statusText}) => {
        'roomDirectoryPath': path,
        'friendlyName': path.split('/').last,
        'typeLabel': 'solo-run-template',
        'statusText': statusText ?? status ?? 'Idle',
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

      // "Not in selection mode", said directly. It used to be asserted as `find.text('Rooms')`
      // finding exactly one widget — the ordinary AppBar title rather than "N selected" — which
      // #1232 broke by adding a nav destination that is also called Rooms. The word was never the
      // claim; the absence of the selection AppBar is.
      expect(find.byTooltip('Cancel selection'), findsNothing);
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
      // Selection mode exited — see the note on the same assertion above.
      expect(find.byTooltip('Cancel selection'), findsNothing);
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
      // Selection mode exited — see the note on the same assertion above.
      expect(find.byTooltip('Cancel selection'), findsNothing);
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
      expect(attentionBand('WaitingToStart'), 3);
      expect(attentionBand('WaitingOnLock'), 3);
    });

    // #1233: 'Stopped' reached attentionBand's catch-all and sorted among Finished/Failed, while
    // roomStatus twelve lines below already special-cased it (#1219). Pinned as a pair so a status
    // the phone renders cannot silently keep the default band: every string roomStatus maps
    // deliberately (a non-idle AerStatus) must also reach a deliberate band arm.
    test('a status the phone renders never falls to the default band', () {
      const knownStatusToBand = {
        'NeedsYou': 0,
        'Running': 1,
        'Finished': 2,
        'Failed': 2,
        'Cancelled': 3,
        'Stopped': 3,
        'Unavailable': 3,
        'OutOfPlan': 3,
        'WaitingToStart': 3,
        'WaitingOnLock': 3,
      };

      for (final entry in knownStatusToBand.entries) {
        expect(roomStatus(entry.key), isNot(AerStatus.idle),
            reason: '${entry.key} is in this table, so roomStatus must map it deliberately');
        expect(attentionBand(entry.key), entry.value, reason: entry.key);
      }

      // The default arms stay — a phone can talk to a daemon that knows a status it doesn't.
      expect(roomStatus('SomeFutureStatus'), AerStatus.idle);
      expect(attentionBand('SomeFutureStatus'), 2);
    });

    test('roomStatus maps status strings to AerStatus', () {
      expect(roomStatus('NeedsYou'), AerStatus.needsInput);
      expect(roomStatus('Running'), AerStatus.working);
      expect(roomStatus('Finished'), AerStatus.finished);
      expect(roomStatus('Failed'), AerStatus.failed);
      expect(roomStatus('Cancelled'), AerStatus.cancelled);
      expect(roomStatus('Unavailable'), AerStatus.unavailable);
      expect(roomStatus('OutOfPlan'), AerStatus.outOfPlan);
      expect(roomStatus('WaitingToStart'), AerStatus.waitingToStart);
      expect(roomStatus('WaitingOnLock'), AerStatus.waitingOnLock);
      expect(roomStatus('UnknownState'), AerStatus.idle);
      expect(roomStatus(null), AerStatus.idle);
    });

    testWidgets('RoomsScreen partitions items by attention bands 0-3 preserving recency within bands', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          final items = [
            fleetItemJson('/tasks/cancelled', status: 'Cancelled'),
            fleetItemJson('/tasks/unavailable', status: 'Unavailable'),
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
      expect(paths, [
        '/tasks/needs',
        '/tasks/running',
        '/tasks/finished',
        '/tasks/cancelled',
        '/tasks/unavailable',
        '/tasks/outofplan',
      ]);
    });

    testWidgets('RoomsScreen renders StatusMark with token label for each status', (tester) async {
      final handle = tester.ensureSemantics();
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          final items = [
            fleetItemJson('/tasks/needs', status: 'NeedsYou'),
            fleetItemJson('/tasks/running', status: 'Running'),
            fleetItemJson('/tasks/finished', status: 'Finished'),
            fleetItemJson('/tasks/failed', status: 'Failed'),
            fleetItemJson('/tasks/cancelled', status: 'Cancelled'),
            fleetItemJson('/tasks/unavailable', status: 'Unavailable'),
            // The review's overflow case (#1132): OutOfPlan's realistic status line is the longest
            // in the vocabulary, and the mark's Row must width-bound it so it wraps as the bare
            // Text did — at phone width, not a widened test surface, or the hazard is invisible.
            fleetItemJson('/tasks/outofplan',
                status: 'OutOfPlan',
                statusText: 'Out of plan — resumes 2026-08-12 18:00 (vendor plan window)'),
            fleetItemJson('/tasks/unknown', status: 'UnknownState'),
          ];
          // Response.bytes + explicit UTF-8: the String overload encodes Latin-1 by default and
          // rejects the em dash the daemon's real "Out of plan — resumes …" line carries.
          return http.Response.bytes(utf8.encode(jsonEncode(items)), 200,
              headers: {'content-type': 'application/json; charset=utf-8'});
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      // Phone width on purpose (the overflow hazard hides on a widened surface); tall enough that
      // the lazy ListView builds every fixture row without scrolling.
      await tester.binding.setSurfaceSize(const Size(400, 4000));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      expect(find.bySemanticsLabel(RegExp(r'Needs input')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Working')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Finished')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Failed')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Cancelled')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Unavailable')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Out of plan')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Idle')), findsOneWidget);

      handle.dispose();
    });
  });

  /// #1232: the phone's nav, and the one rule that makes "Needs you" implementable without a design
  /// fork. That rule is stated on `RoomsScreen._visibleItems`, which is the code it governs; these
  /// tests are what stop it drifting.
  group('The phone nav and the Needs you filter (#1232)', () {
    Map<String, dynamic> item(String path, String status) => {
          'roomDirectoryPath': path,
          'friendlyName': path.split('/').last,
          'typeLabel': 'solo-run-template',
          'statusText': status,
          'status': status,
          'pausedStepCount': 0,
          'isArchived': false,
        };

    Future<int> pumpAndCountFetches(WidgetTester tester, Future<void> Function(WidgetTester) drive) async {
      var roomFetches = 0;
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          roomFetches++;
          return http.Response(
            jsonEncode([item('/tasks/needy', 'NeedsYou'), item('/tasks/busy', 'Running'), item('/tasks/done', 'Finished')]),
            200,
          );
        }
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();
      await drive(tester);
      return roomFetches;
    }

    testWidgets('Needs you shows only the rooms that need you, and Rooms shows them all', (tester) async {
      await pumpAndCountFetches(tester, (t) async {
        expect(find.text('needy'), findsOneWidget);
        expect(find.text('busy'), findsOneWidget);
        expect(find.text('done'), findsOneWidget);

        await t.tap(find.text('Needs you'));
        await t.pumpAndSettle();

        expect(find.text('needy'), findsOneWidget);
        expect(find.text('busy'), findsNothing);
        expect(find.text('done'), findsNothing);
      });
    });

    testWidgets('switching to Needs you fetches nothing — it is a filter, not a second surface', (tester) async {
      // The load-bearing assertion of this slice. A "Needs you" that fetches has begun keeping its
      // own copy of the world, which is exactly what the corpus forbids and what the phone's deleted
      // inbox used to be.
      final fetches = await pumpAndCountFetches(tester, (t) async {
        await t.tap(find.text('Needs you'));
        await t.pumpAndSettle();
        await t.tap(find.text('Rooms').last);
        await t.pumpAndSettle();
      });

      expect(fetches, 1);
    });

    testWidgets('an empty Needs you says your rooms are fine, and offers no new room', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode([item('/tasks/done', 'Finished')]), 200);
        }
        return http.Response('unexpected', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Needs you'));
      await tester.pumpAndSettle();

      expect(find.text('Nothing needs you.'), findsOneWidget);
      // J8's "a real first action" answers "you have no rooms", not "nothing is waiting on you".
      expect(find.text('New room'), findsNothing);
    });

    testWidgets('Settings carries what a paired phone should be able to answer about itself', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode(<Map<String, dynamic>>[]), 200);
        }
        return http.Response('unexpected', 500);
      });
      final client = DaemonClient(host: 'desktop.local:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Settings'));
      await tester.pumpAndSettle();

      expect(find.text('desktop.local:5000'), findsOneWidget);
      expect(find.text('Sign out'), findsOneWidget);
    });

    // #1234, measured on-device before it was fixed — what it cost is recorded on
    // `RoomsScreen`'s `onDestinationSelected`. Reachable only once this issue added a second
    // destination to switch to, which is why it is pinned here.
    testWidgets('leaving the destination leaves the selection behind', (tester) async {
      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode([fleetItemJson('/tasks/a'), fleetItemJson('/tasks/b')]), 200);
        }
        return http.Response('unexpected', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.longPress(find.text('a'));
      await tester.pumpAndSettle();
      expect(find.text('1 selected'), findsOneWidget);

      await tester.tap(find.text('Settings'));
      await tester.pumpAndSettle();

      expect(find.text('1 selected'), findsNothing);
      expect(find.byTooltip('Archive selected'), findsNothing);
      expect(find.byTooltip('Delete selected'), findsNothing);
      expect(find.text('Sign out'), findsOneWidget);

      // And back on Rooms it is genuinely cleared, not merely hidden behind the Settings body.
      await tester.tap(find.text('Rooms'));
      await tester.pumpAndSettle();
      expect(find.byTooltip('Cancel selection'), findsNothing);
      expect(find.byType(Checkbox), findsNothing);
    });
  });

  /// The capability #1226's second reader caught going missing — see `RoomsScreen._startNewRoom` for
  /// what was lost and how. It had no test, which is how it nearly left silently; it has one now, on
  /// the entry point that owns it.
  group('Starting a room from a template (#1226)', () {
    Map<String, dynamic> templatesJson() => {
          'templates': [
            {'id': 'solo-run', 'title': 'Solo Run (Advanced)', 'requiresSecondaryVendor': false},
          ],
          'availableVendors': [
            {'adapterName': 'claude', 'isAvailable': true},
          ],
        };

    testWidgets('picking a template runs it and opens the workflow room it started', (tester) async {
      String? ranTemplateId;
      String? ranPrimaryAdapter;
      var startSessionCalls = 0;

      final mockClient = MockClient((request) async {
        if (request.method == 'GET' && request.url.path == '/api/rooms') {
          return http.Response(jsonEncode(<Map<String, dynamic>>[]), 200);
        }
        if (request.method == 'GET' && request.url.path == '/api/templates') {
          return http.Response(jsonEncode(templatesJson()), 200);
        }
        if (request.method == 'POST' && request.url.path == '/api/templates/run') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          ranTemplateId = body['templateId']?.toString();
          ranPrimaryAdapter = body['primaryAdapter']?.toString();
          return http.Response(jsonEncode({'roomDirectoryPath': '/rooms/from-template'}), 200);
        }
        if (request.method == 'POST' && request.url.path == '/api/sessions') {
          startSessionCalls++;
          return http.Response(jsonEncode({'roomDirectoryPath': '/rooms/chat', 'sessionId': 'sess-1'}), 200);
        }
        // A projection socket the opened room would subscribe to is not served here; the room screen
        // renders its empty transcript without one.
        return http.Response('unexpected request: ${request.method} ${request.url}', 500);
      });
      final client = DaemonClient(host: 'localhost:5000', token: 'fake-token', httpClient: mockClient);

      await tester.pumpWidget(MaterialApp(home: RoomsScreen(client: client)));
      await tester.pumpAndSettle();

      await tester.tap(find.text('New room').first);
      await tester.pumpAndSettle();

      // "Just talk" is the default; choosing a shape is what this pins.
      await tester.tap(find.text('Just talk'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Solo Run (Advanced)').last);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Start'));
      await tester.pumpAndSettle();

      expect(ranTemplateId, 'solo-run');
      expect(ranPrimaryAdapter, 'claude');
      // A template's room is a workflow room, so it must NOT have been started as a chat session.
      expect(startSessionCalls, 0);
    });
  });
}
