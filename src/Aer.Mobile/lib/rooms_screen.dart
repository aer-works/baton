import 'package:flutter/material.dart';

import 'chat_screen.dart';
import 'daemon/credentials_store.dart';
import 'daemon/daemon_client.dart';
import 'daemon/models.dart';
import 'pairing_screen.dart';
import 'theme/status_mark.dart';
import 'theme/tokens.dart';

/// Maps a status string to decision 0018's (docs/decisions/0018-attention-is-the-primary-signal.md)
/// attention band; unknown strings stay visible in band 2, never the muted band.
int attentionBand(String? status) => switch (status) {
      'NeedsYou' => 0,
      'Running' => 1,
      'Finished' || 'Failed' => 2,
      'Cancelled' || 'Unavailable' || 'OutOfPlan' => 3,
      _ => 2,
    };

/// Maps a room status string to decision 0006's [AerStatus]; canonical prose is design/tokens.json.
AerStatus roomStatus(String? status) => switch (status) {
      'NeedsYou' => AerStatus.needsInput,
      'Running' => AerStatus.working,
      'Finished' => AerStatus.finished,
      'Failed' => AerStatus.failed,
      'Cancelled' => AerStatus.cancelled,
      // #1219: the phone renders the same tenth state the desktop does. Without this row it would
      // fall to `idle` below, and a room whose process died would read as one that had never
      // started — the two surfaces disagreeing again, one platform over.
      'Stopped' => AerStatus.stopped,
      'Unavailable' => AerStatus.unavailable,
      'OutOfPlan' => AerStatus.outOfPlan,
      _ => AerStatus.idle,
    };

/// The switcher — the phone's front door (#337/#1044): every known room at once, and the screen a
/// paired device lands on. Tapping a room enters it directly (a session opens its chat; a workflow
/// opens its decision view), so a room row is a place, not a dead card. It also carries the
/// fleet-management affordances it began as (archive/unarchive/delete, bulk-select — M24 Phase 5,
/// #278); the Flutter counterpart of Aer.Ui's Rooms view.
///
/// Bulk select (issue #288): long-press a card to enter selection mode, then tap any card to
/// toggle it — the same long-press-to-select convention most Flutter list UIs use (no existing
/// precedent for multi-select anywhere else in this app, so this follows the platform default
/// rather than inventing one). Selected paths are tracked by directory path (a task's stable
/// identity), not list index, since `_refresh()` rebuilds `_items` from scratch after every
/// mutation.
class RoomsScreen extends StatefulWidget {
  final DaemonClient client;

  const RoomsScreen({super.key, required this.client});

  @override
  State<RoomsScreen> createState() => _RoomsScreenState();
}

class _RoomsScreenState extends State<RoomsScreen> with WidgetsBindingObserver {
  List<RoomFleetItem> _items = [];
  List<RoomFleetItem> get itemsForTests => _items;
  bool _includeArchived = false;
  bool _isLoading = true;
  String? _loadError;

  bool _selectionMode = false;
  final Set<String> _selectedPaths = {};

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _refresh();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  /// Refresh the fleet on foreground — the staleness #287 fixed for the inbox, now that the switcher
  /// is the landing. (Live WS-driven fleet updates are J5/#330, out of this slice.)
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed && !_isLoading) {
      _refresh();
    }
  }

  Future<void> _refresh() async {
    setState(() {
      _isLoading = true;
      _loadError = null;
    });

    try {
      final items = await widget.client.listRooms(includeArchived: _includeArchived);
      if (!mounted) return;
      // Group by decision 0018's four attention bands while preserving daemon recency within each group (stable partition).
      final bands = [
        <RoomFleetItem>[],
        <RoomFleetItem>[],
        <RoomFleetItem>[],
        <RoomFleetItem>[],
      ];
      for (final item in items) {
        bands[attentionBand(item.status)].add(item);
      }
      setState(() => _items = [for (final band in bands) ...band]);
    } on DaemonException catch (e) {
      if (!mounted) return;
      setState(() => _loadError = e.message);
    } finally {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  Future<void> _archive(RoomFleetItem item) async {
    try {
      await widget.client.archiveRoom(item.roomDirectoryPath);
      await _refresh();
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  Future<void> _unarchive(RoomFleetItem item) async {
    try {
      await widget.client.unarchiveRoom(item.roomDirectoryPath);
      await _refresh();
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  /// Reuses the `showDialog` + `AlertDialog` confirm pattern `ChatScreen._cancelRun` uses —
  /// mobile already has this precedent, unlike desktop, which has no
  /// modal-dialog infrastructure and uses an inline two-step confirm instead.
  Future<void> _delete(RoomFleetItem item) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete this room?'),
        content: Text('"${item.friendlyName}" will be permanently removed. This can\'t be undone.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Delete')),
        ],
      ),
    );
    if (confirmed != true) return;

    try {
      await widget.client.deleteRoom(item.roomDirectoryPath);
      await _refresh();
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  void _enterSelectionMode(RoomFleetItem item) {
    setState(() {
      _selectionMode = true;
      _selectedPaths.add(item.roomDirectoryPath);
    });
  }

  void _toggleSelection(RoomFleetItem item) {
    setState(() {
      if (_selectedPaths.contains(item.roomDirectoryPath)) {
        _selectedPaths.remove(item.roomDirectoryPath);
      } else {
        _selectedPaths.add(item.roomDirectoryPath);
      }
      if (_selectedPaths.isEmpty) {
        _selectionMode = false;
      }
    });
  }

  void _exitSelectionMode() {
    setState(() {
      _selectionMode = false;
      _selectedPaths.clear();
    });
  }

  /// Archives every selected, not-yet-archived item (issue #288) — sequential calls against the
  /// existing per-directory `/api/rooms/archive` endpoint (same reasoning as desktop's
  /// `RoomsViewModel.BulkArchiveAsync`: archive mutates the shared fleet index, so parallel calls
  /// could race), one `_refresh()` at the end rather than one per item.
  Future<void> _bulkArchive() async {
    final targets = _items.where((i) => _selectedPaths.contains(i.roomDirectoryPath) && !i.isArchived).toList();
    if (targets.isEmpty) {
      _exitSelectionMode();
      return;
    }

    final failures = <String>[];
    for (final item in targets) {
      try {
        await widget.client.archiveRoom(item.roomDirectoryPath);
      } on DaemonException catch (e) {
        failures.add('${item.friendlyName}: ${e.message}');
      }
    }

    _exitSelectionMode();
    await _refresh();

    if (failures.isNotEmpty && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text("${failures.length} of ${targets.length} room(s) couldn't be archived: ${failures.join('; ')}")),
      );
    }
  }

  /// Deletes every selected item (issue #288) after a single "Delete N tasks?" confirm — the bulk
  /// counterpart of `_delete`'s per-item confirm, not a regression to no confirmation.
  Future<void> _bulkDelete() async {
    final targets = _items.where((i) => _selectedPaths.contains(i.roomDirectoryPath)).toList();
    if (targets.isEmpty) {
      _exitSelectionMode();
      return;
    }

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Delete ${targets.length} room${targets.length == 1 ? '' : 's'}?'),
        content: const Text('These will be permanently removed. This can\'t be undone.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Delete')),
        ],
      ),
    );
    if (confirmed != true) return;

    final failures = <String>[];
    for (final item in targets) {
      try {
        await widget.client.deleteRoom(item.roomDirectoryPath);
      } on DaemonException catch (e) {
        failures.add('${item.friendlyName}: ${e.message}');
      }
    }

    _exitSelectionMode();
    await _refresh();

    if (failures.isNotEmpty && mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text("${failures.length} of ${targets.length} room(s) couldn't be deleted: ${failures.join('; ')}")),
      );
    }
  }

  /// The empty-state's "start work" action — J8's "a real first action, not a dead-end" (#337): an
  /// empty rooms surface must offer a way to begin, not just report that it is empty. A minimal chat
  /// start — pick a vendor, open a room, land in its chat. It was one of two such affordances, the
  /// other on the phone's old decision surface, and #337/J3 always intended them to collapse into
  /// one when the front door was unified; #1226 retired that surface, so this is now the one.
  Future<void> _startNewRoom() async {
    var availableVendorNames = <String>[];
    try {
      final data = await widget.client.listTemplates();
      final vendors = (data['availableVendors'] as List<dynamic>?) ?? [];
      availableVendorNames = vendors
          .where((v) => (v as Map<String, dynamic>)['isAvailable'] == true)
          .map((v) => v['adapterName'].toString())
          .toList();
    } catch (_) {
      // Best-effort probe -- the fallback list below still lets the dialog work.
    }
    if (availableVendorNames.isEmpty) {
      availableVendorNames = ['claude', 'agy']; // vocabulary-ok: vendor key
    }
    if (!mounted) return;

    var selectedAdapter = availableVendorNames.first;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => StatefulBuilder(
        builder: (context, setDialogState) => AlertDialog(
          title: const Text('New room'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text('Vendor'),
              DropdownButton<String>(
                value: selectedAdapter,
                isExpanded: true,
                items: availableVendorNames
                    .map((v) => DropdownMenuItem(value: v, child: Text(v)))
                    .toList(),
                onChanged: (val) {
                  if (val != null) setDialogState(() => selectedAdapter = val);
                },
              ),
            ],
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Start')),
          ],
        ),
      ),
    );
    if (confirmed != true || !mounted) return;

    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    try {
      final meta = await widget.client.startSession(adapter: selectedAdapter);
      final metaCi = caseInsensitive(meta);
      final directoryPath = metaCi['roomdirectorypath']?.toString();
      final sessionId = metaCi['sessionid']?.toString();
      if (directoryPath != null && sessionId != null) {
        await navigator.push(MaterialPageRoute(
          builder: (_) => ChatScreen(client: widget.client, sessionId: sessionId, directoryPath: directoryPath),
        ));
      } else {
        messenger.showSnackBar(const SnackBar(content: Text('Room started.')));
      }
      if (mounted) await _refresh();
    } on DaemonException catch (e) {
      messenger.showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  /// Enters a room from its fleet row (row-as-place, #1044). Both kinds open the same screen since
  /// #1226 (#1196 slice 6a): a room has one rendering on the phone as on the desktop, and which kind
  /// it is decides what the transcript carries and whether the composer is live, not which screen
  /// you land on. `ChatScreen.sessionId` being null IS the workflow case. Wired only outside
  /// selection mode.
  Future<void> _openRoom(RoomFleetItem item) async {
    final navigator = Navigator.of(context);
    await navigator.push(MaterialPageRoute(
      builder: (_) => ChatScreen(
          client: widget.client, sessionId: item.sessionId, directoryPath: item.roomDirectoryPath),
    ));
    // Re-fetch on return: the room's status (or its very existence, if cancelled/deleted in there)
    // may have changed while it was open.
    if (mounted) await _refresh();
  }

  /// Clears this phone's pairing and returns to the pairing screen. Moved here from the old decision
  /// surface once the switcher became the landing — a paired device must be able to unpair from its
  /// front door. Clears credentials on this phone only; the desktop still lists the device until it
  /// is removed there.
  Future<void> _signOut() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Sign out of this desktop?'),
        content: const Text(
          "This clears the pairing on this phone only. The desktop will still list this device "
          "until it's removed there.",
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Sign out')),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    final navigator = Navigator.of(context);
    await CredentialsStore().clear();
    navigator.pushReplacement(MaterialPageRoute(builder: (_) => const PairingScreen()));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: _selectionMode
          ? AppBar(
              leading: IconButton(icon: const Icon(Icons.close), tooltip: 'Cancel selection', onPressed: _exitSelectionMode),
              title: Text('${_selectedPaths.length} selected'),
              actions: [
                IconButton(icon: const Icon(Icons.archive_outlined), tooltip: 'Archive selected', onPressed: _bulkArchive),
                IconButton(icon: const Icon(Icons.delete_outline), tooltip: 'Delete selected', onPressed: _bulkDelete),
              ],
            )
          : AppBar(
              title: const Text('Rooms'),
              actions: [
                IconButton(icon: const Icon(Icons.refresh), tooltip: 'Refresh', onPressed: _isLoading ? null : _refresh),
                IconButton(icon: const Icon(Icons.logout), tooltip: 'Sign out', onPressed: _signOut),
              ],
            ),
      body: Column(
        children: [
          SwitchListTile(
            title: const Text('Show archived'),
            value: _includeArchived,
            onChanged: (value) {
              setState(() => _includeArchived = value);
              _refresh();
            },
          ),
          if (_loadError != null)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Text(_loadError!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
            ),
          if (_isLoading) const LinearProgressIndicator(),
          Expanded(
            child: _items.isEmpty && !_isLoading
                ? Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        const Text('No rooms yet.'),
                        const SizedBox(height: 16),
                        FilledButton.icon(
                          onPressed: _startNewRoom,
                          icon: const Icon(Icons.add),
                          label: const Text('New room'),
                        ),
                      ],
                    ),
                  )
                : RefreshIndicator(
                    onRefresh: _refresh,
                    child: ListView.builder(
                    itemCount: _items.length,
                    itemBuilder: (context, index) {
                      final item = _items[index];
                      final isSelected = _selectedPaths.contains(item.roomDirectoryPath);
                      return Card(
                        margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                        color: isSelected ? Theme.of(context).colorScheme.primaryContainer : null,
                        child: InkWell(
                          onTap: _selectionMode ? () => _toggleSelection(item) : () => _openRoom(item),
                          onLongPress: _selectionMode ? null : () => _enterSelectionMode(item),
                          child: Padding(
                            padding: const EdgeInsets.all(12),
                            child: Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                if (_selectionMode)
                                  Padding(
                                    padding: const EdgeInsets.only(right: 8, top: 4),
                                    child: Checkbox(value: isSelected, onChanged: (_) => _toggleSelection(item)),
                                  ),
                                Expanded(
                                  child: Column(
                                    crossAxisAlignment: CrossAxisAlignment.start,
                                    children: [
                                      Row(
                                        children: [
                                          Expanded(
                                            child: Text(item.friendlyName, style: const TextStyle(fontWeight: FontWeight.bold)),
                                          ),
                                          Text(item.typeLabel, style: Theme.of(context).textTheme.bodySmall),
                                          if (item.isArchived) ...[
                                            const SizedBox(width: 8),
                                            Text('archived', style: Theme.of(context).textTheme.bodySmall),
                                          ],
                                        ],
                                      ),
                                      const SizedBox(height: 4),
                                      // The canonical status line (J3, #1049) — "Waiting for your reply"
                                      // for a chat turn, "Waiting for your review" for a real gate. Replaces
                                      // the old raw "N step(s) awaiting a decision", which mislabelled a chat.
                                      Row(
                                        // Top-align the mark against a wrapping status line, and keep
                                        // the Text width-bounded: a Row does not pass the Column's
                                        // width constraint to non-flex children, so without Expanded a
                                        // long line ("Out of plan — resumes …") overflows instead of
                                        // wrapping the way the bare Text here always did.
                                        crossAxisAlignment: CrossAxisAlignment.start,
                                        children: [
                                          Padding(
                                            padding: const EdgeInsets.only(top: 2),
                                            child: StatusMark(roomStatus(item.status), size: 12),
                                          ),
                                          const SizedBox(width: 4),
                                          Expanded(child: Text(item.statusText)),
                                        ],
                                      ),
                                      Text(item.roomDirectoryPath, style: Theme.of(context).textTheme.bodySmall),
                                      const SizedBox(height: 8),
                                      if (!_selectionMode)
                                        Row(
                                          mainAxisAlignment: MainAxisAlignment.end,
                                          children: [
                                            if (!item.isArchived)
                                              TextButton(onPressed: () => _archive(item), child: const Text('Archive'))
                                            else
                                              TextButton(onPressed: () => _unarchive(item), child: const Text('Unarchive')),
                                            TextButton(onPressed: () => _delete(item), child: const Text('Delete')),
                                          ],
                                        ),
                                    ],
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      );
                    },
                  ),
                ),
          ),
        ],
      ),
    );
  }
}
