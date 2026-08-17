import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:markdown/markdown.dart' as md;
import 'package:flutter_markdown_plus/flutter_markdown_plus.dart';

import 'daemon/daemon_client.dart';
import 'daemon/models.dart';
import 'daemon/permission_decision_kind.dart';
import 'daemon/permission_grant_wording.dart';
import 'daemon/recorded_decision_wording.dart';
import 'daemon/shell_command_pattern_matcher.dart';
import 'failed_step_card.dart';
import 'paused_step_card.dart';
import 'room_stopped_card.dart';
import 'theme/tokens.dart';

/// #483: the only seam for driving the thinking-time stopwatch's elapsed duration in a widget test
/// -- flutter_test's clock does not fast-forward wall time, so proving the >=10s caption format
/// requires overriding this rather than waiting for real seconds to pass. Tests must restore the
/// default in `tearDown` so the override never leaks into another test.
@visibleForTesting
DateTime Function() debugThinkingTimeClock = DateTime.now;

/// One rendered row in the chat transcript — a human turn or an assistant response, never both.
/// Mirrors Aer.Ui.Core's ChatMessageViewModel (see ChatViewModel.cs).
class _ChatMessage {
  final String senderLabel;
  final String text;
  final bool isFromUser;
  final bool isSystem;
  final bool isFailure;
  final bool isDormancy;
  final bool isOutOfPlan;
  final VoidCallback? onFix;
  final VoidCallback? onWake;

  /// Overrides what Copy places on the clipboard (null → [text]). See the canonical
  /// `ChatMessageViewModel.CopyText` doc in Aer.Ui.Core/ChatViewModel.cs for the raw-vendor-words
  /// rationale on the out-of-plan bubble.
  final String? copyText;

  _ChatMessage({
    required this.senderLabel,
    required this.text,
    required this.isFromUser,
    this.isSystem = false,
    this.isFailure = false,
    this.isDormancy = false,
    this.isOutOfPlan = false,
    this.onFix,
    this.onWake,
    this.copyText,
  });
}

/// One queued composer entry (#1131) — deliberately a class with default identity equality, the
/// Flutter stand-in for QueuedChatMessageViewModel: two same-text entries must stay two entries.
class _QueuedMessage {
  final String text;

  _QueuedMessage(this.text);
}

/// The mobile room screen (M24, issue #262) — the Flutter counterpart of Aer.Ui's `ChatView`, and
/// since #1226 (#1196 slice 6a) the **only** rendering a room has on the phone, whether it is a
/// chat session or a workflow. `Turns` (the actual message content of a session) live outside
/// RoomProjection entirely, in SessionMetadata, so a session room re-fetches
/// GET /api/sessions/{sessionId} for those; everything a workflow room shows — its gate, its paused
/// steps, its permission and dormancy history — comes off the projection this screen already
/// watches.
///
/// It absorbed the phone's separate decision surface, `InboxScreen`, which #1226 deleted: with rooms
/// routed here, nothing opened it, and every capability it held (approve/reject/send-back, stopping
/// the run) came with the room rather than being left behind.
///
/// Unlike desktop (which polls `.aer/session.json` off disk on a 2-second timer, since it's the
/// same machine), this phone has no filesystem access to the daemon host — it instead re-fetches
/// on every filtered `/api/ws` push for [directoryPath], which the daemon already sends whenever a
/// turn completes (Aer.Daemon.Program's ExecuteSessionTurnAsync calls BroadcastStateAsync through
/// the same session/DecideAsync path every other run uses).
///
/// Navigation into this screen only ever happens from an explicit local action (starting a
/// session here, or tapping an inbox card for a session this phone already has open) — never
/// automatically off an incoming WS push, so a different client starting its own session can't
/// yank this phone into a chat it didn't ask to view.
class ChatScreen extends StatefulWidget {
  final DaemonClient client;

  /// The interactive session this room materialized, or **null for a workflow room** — which has no
  /// session and never will (#1226, #1196 slice 6a). Null is not "not loaded yet": it is the room
  /// kind, and it decides whether there is anything to talk to. Everything else on this screen —
  /// the projection subscription, the gate, the decision cards, the transcript merge — is keyed on
  /// [directoryPath] and works the same either way, which is why a workflow room can render here at
  /// all rather than needing a second screen.
  final String? sessionId;

  final String directoryPath;

  const ChatScreen({super.key, required this.client, required this.sessionId, required this.directoryPath});

  @override
  State<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends State<ChatScreen> {
  final _inputController = TextEditingController();
  final _scrollController = ScrollController();

  StreamSubscription<RoomProjection>? _projectionSubscription;
  StreamSubscription<SessionProgressEvent>? _progressSubscription;
  Timer? _sendTimeoutTimer;

  SessionMetadata? _metadata;
  bool _isLoading = true;
  String? _loadError;
  String? _sendError;

  /// A dropped projection socket, surfaced as a recoverable banner. Without this the `watch()` stream's error was swallowed silently and every
  /// future push — including the inline permission gate's appear/clear — stopped arriving with no
  /// signal to the user (found while live-driving #390's mobile gate).
  String? _connectionError;

  bool _isSending = false;
  String? _pendingUserMessage;
  int _turnsCountAtSendTime = 0;
  String _liveProgressText = '';
  // #323/#1290: mirrors ChatViewModel._lastProgressWasPartialText — see its doc comment on
  // ChatViewModel.AppendProgress (src/Aer.Ui.Core/ChatViewModel.cs) for the reasoning.
  bool _lastProgressWasPartialText = false;

  // #483: mirrors ChatViewModel.ThinkingTimeText/ThinkingTimeReportThreshold — see
  // ChatViewModel.FormatThinkingTime (src/Aer.Ui.Core/ChatViewModel.cs) for the reasoning. Reported
  // once, after the turn completes; never a live-updating count.
  String _thinkingTimeText = '';
  static const _thinkingTimeReportThreshold = Duration(seconds: 10);
  DateTime? _turnStartedAt;

  /// Client-local queue preventing concurrent turns per ChatViewModel.EnqueueMessage
  /// (src/Aer.Ui.Core/ChatViewModel.cs:246-260). Entries are identity-carrying objects, not bare
  /// strings, for TryPeekQueuedMessage's reason: the drain consumes its exact head, so a removal
  /// racing the in-flight dispatch can never make two same-text entries collapse into one.
  final List<_QueuedMessage> _queuedMessages = [];

  /// Pauses queue draining after a failed send until the operator's next manual send or enqueue,
  /// per ChatViewModel.EnqueueMessage / FailSend (src/Aer.Ui.Core/ChatViewModel.cs:246-304).
  bool _drainPaused = false;

  bool _isLoadingCommands = false;

  /// The active session mode (#286), or null until [_refreshMode] resolves it — shown persistently
  /// in the AppBar rather than only reflected transiently right after a mode-button tap.
  String? _currentMode;

  /// The open gate (see [PendingPermission]) this screen renders inline above the composer when
  /// non-null. A projected fact — see [_surfacePendingPermission] — never edited directly, and
  /// re-derived (never carried over) whenever the projection's own value changes.
  PendingPermission? _pendingPermission;

  /// History of answered or revoked permissions from the latest projection.
  List<PermissionAnswer> _permissionAnswers = const [];
  List<DormancyTransition> _dormancyTransitions = const [];
  bool _isDormant = false;

  /// The /clear orphan-bubble guard: [_buildMessages] drops any answer stamped at or under this.
  /// Mechanism, daemon-clock choice, and the restart limitation are documented canonically on the
  /// desktop twin, ChatViewModel._answersClearedThrough (#1142 review).
  DateTime? _answersClearedThrough;

  /// True while an answer POST is in flight — disables the gate's rungs, the same in-flight
  /// discipline [_decideStep] applies, so a slow round trip can't be double-submitted. Independent of
  /// [_pendingPermission]'s identity: the gate disappears entirely once the next projection clears
  /// it, so this never needs to be reset on success.
  bool _isAnsweringPermission = false;

  /// The latest projection for this room, kept for a workflow room's paused steps and their
  /// definitions/artifacts (#1226). A session room reads its transcript from [getSession] instead
  /// and does not need this held.
  RoomProjection? _projection;

  /// Step ids with a decision POST in flight — the same in-flight discipline
  /// `InboxScreen._decide` applies, moved with the cards.
  final Set<String> _pendingStepIds = {};

  /// Whether this room has a session to talk to. A workflow room does not, which is what decides
  /// the composer and the transcript's source — never "is it still loading".
  bool get _isSessionRoom => widget.sessionId != null;

  /// The session id, for the paths that exist only in a session room — send, drain, the commands
  /// sheet, mode, compact, clear. Every one of them is unreachable in a workflow room: the composer
  /// and its send button are disabled, and the commands action is not rendered at all. Stating that
  /// invariant once, here, is deliberate — the alternative is threading a nullable through seven
  /// call sites whose operations are meaningless without a session, which would turn a structural
  /// impossibility into seven silent no-ops.
  String get _sessionId => widget.sessionId!;

  @override
  void initState() {
    super.initState();
    _refresh();
    _refreshMode();
    _subscribeProjection();
    _requestFirstProjection();
    _progressSubscription = widget.client.watchProgress().listen((event) {
      if (!mounted) return;
      if (event.directoryPath == widget.directoryPath && _isSending) {
        setState(() {
          final isContinuingPartialText =
              _lastProgressWasPartialText && event.kind == 'text' && event.isPartial;
          if (!isContinuingPartialText && _liveProgressText.isNotEmpty) {
            _liveProgressText += ' · ';
          }
          _liveProgressText += event.text;
          _lastProgressWasPartialText = event.kind == 'text' && event.isPartial;
        });
      }
    });
  }

  /// Rings the daemon's doorbell so this room's CURRENT projection is pushed over the socket
  /// [_subscribeProjection] just opened. Without it a workflow room opens **empty** and stays empty
  /// until something else in the world happens to change — which for a paused room waiting on a
  /// person is never, since the thing that would change it is the answer they came here to give.
  ///
  /// Found by driving the built app, not by a test: the widget tests push a projection in by hand,
  /// so every one of them passed against a screen that could never obtain one. `InboxScreen._init`
  /// did exactly this before #1226 deleted it, and this is that call restored to the screen that
  /// inherited its job — the WS broadcast path #390 established, never an out-of-band read.
  ///
  /// Only for a workflow room: a session room's content comes from `getSession`, and `openRoom`
  /// reassigns the daemon's own notion of the current room (see its remarks), so it is not called
  /// where it is not needed. Opening a room from the switcher is the explicit user action that call
  /// requires.
  Future<void> _requestFirstProjection() async {
    if (_isSessionRoom) return;
    try {
      await widget.client.openRoom(widget.directoryPath);
    } on DaemonException catch (e) {
      if (mounted) setState(() => _connectionError = e.message);
    }
  }

  /// (Re)subscribes to the daemon's filtered projection stream.
  /// `onError`/`onDone` surface a recoverable [_connectionError] banner rather than letting a dropped
  /// socket swallow every future push — the silent-swallow that would otherwise strand the inline
  /// permission gate. Also the Reconnect button's action. A push that arrives after an error clears
  /// the banner, so a self-healing transport needs no tap.
  void _subscribeProjection() {
    _projectionSubscription?.cancel();
    setState(() => _connectionError = null);
    _projectionSubscription = widget.client.watch().listen(
      (projection) {
        if (!mounted) return;
        if (_connectionError != null) setState(() => _connectionError = null);
        if (projection.directoryPath == widget.directoryPath) {
          setState(() => _projection = projection);
          _surfacePendingPermission(projection.pendingPermission);
          _surfacePermissionAnswers(projection.permissionAnswers);
          _surfaceDormancyTransitions(projection.dormancyTransitions, projection.isDormant);
          _refresh();
        }
      },
      onError: (Object error) {
        if (mounted) setState(() => _connectionError = 'Disconnected — $error');
      },
      onDone: () {
        if (mounted) setState(() => _connectionError ??= 'Disconnected from the Baton daemon.');
      },
    );
  }

  @override
  void dispose() {
    _projectionSubscription?.cancel();
    _progressSubscription?.cancel();
    _sendTimeoutTimer?.cancel();
    _inputController.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    // A workflow room has no session to fetch (#1226). Its transcript comes from the projection
    // pushes this screen is already subscribed to, so there is nothing to load and nothing to wait
    // for — leaving _isLoading true here would spin a progress indicator forever over a room whose
    // content had already arrived.
    final sessionId = widget.sessionId;
    if (sessionId == null) {
      if (mounted) setState(() { _isLoading = false; _loadError = null; });
      return;
    }

    try {
      final metadata = await widget.client.getSession(sessionId);
      if (!mounted) return;
      bool turnCompleted = false;
      setState(() {
        _metadata = metadata;
        _isLoading = false;
        _loadError = null;

        if (_isSending && metadata.turnCount > _turnsCountAtSendTime) {
          _isSending = false;
          _liveProgressText = '';
          _lastProgressWasPartialText = false;
          _pendingUserMessage = null;
          _sendTimeoutTimer?.cancel();
          final startedAt = _turnStartedAt;
          _thinkingTimeText = startedAt == null
              ? ''
              : _formatThinkingTime(debugThinkingTimeClock().difference(startedAt));
          _turnStartedAt = null;
          turnCompleted = true;
        }
      });
      _scrollToEnd();

      if (turnCompleted && _queuedMessages.isNotEmpty && !_drainPaused) {
        _drainQueue();
      }
    } on DaemonException catch (e) {
      if (mounted) setState(() { _isLoading = false; _loadError = e.message; });
    }
  }

  /// Best-effort: a stale/missing mode indicator is cosmetic, not worth surfacing as a chat error.
  Future<void> _refreshMode() async {
    final sessionId = widget.sessionId;
    if (sessionId == null) return;
    try {
      final mode = await widget.client.getSessionMode(sessionId);
      if (mounted) setState(() => _currentMode = mode);
    } on DaemonException {
      // Leave _currentMode as-is (null on first load, last-known value otherwise).
    }
  }

  /// Applies [pending] to the inline gate per `ChatViewModel.SurfacePendingPermission` (the C#
  /// canonical for the same-id-keep / different-id-replace / null-clear rule). Dart-specific:
  /// [_isAnsweringPermission] survives a same-id push, so an in-flight answer isn't reset by an
  /// unrelated projection change.
  void _surfacePendingPermission(PendingPermission? pending) {
    if (pending == null) {
      if (_pendingPermission != null) setState(() => _pendingPermission = null);
      return;
    }
    if (_pendingPermission?.permissionRequestId == pending.permissionRequestId) {
      return;
    }
    setState(() {
      _pendingPermission = pending;
      _isAnsweringPermission = false;
    });
    _scrollToEnd();
  }

  void _surfacePermissionAnswers(List<PermissionAnswer> answers) {
    if (mounted) {
      setState(() => _permissionAnswers = answers);
    }
  }

  void _surfaceDormancyTransitions(List<DormancyTransition> transitions, bool isDormant) {
    if (mounted) {
      setState(() {
        _dormancyTransitions = transitions;
        _isDormant = isDormant;
      });
    }
  }

  Future<void> _clearDormancy() async {
    try {
      await widget.client.clearTurnHostDormancy(widget.directoryPath);
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Failed to wake: $e')));
      }
    }
  }

  /// Calls [DaemonClient.reassignOrchestrator] (see that method's doc comment for what the
  /// endpoint does) — same shape as [_clearDormancy]: fire the request, let [_refresh] pick up the
  /// new `Participants` on its next poll rather than mutating local state, and surface a refusal
  /// as a snackbar rather than swallowing it.
  Future<void> _reassignOrchestrator(String workerId) async {
    try {
      await widget.client.reassignOrchestrator(widget.directoryPath, workerId);
      await _refresh();
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Could not reassign orchestrator: $e')));
      }
    }
  }

  /// Answers the open gate with one of [PermissionDecisionKind]'s rungs — the same shape as
  /// [_decideStep]: disable-during-flight, then let the next projection push (via
  /// [_surfacePendingPermission]) clear the gate on success rather than clearing it locally, since
  /// the daemon's answer may itself raise the *next* pending permission in the same push.
  Future<void> _answerPermission(String decisionKind) async {
    final pending = _pendingPermission;
    if (pending == null || _isAnsweringPermission) return;

    setState(() => _isAnsweringPermission = true);
    try {
      await widget.client.answerPermission(
        directoryPath: widget.directoryPath,
        permissionRequestId: pending.permissionRequestId,
        decisionKind: decisionKind,
      );
    } catch (e) {
      // Catch EVERY error, not just DaemonException: _post/_get don't wrap transport failures, so a
      // dropped connection or tsnet blip mid-answer throws a SocketException/ClientException that a
      // narrow `on DaemonException` would miss — leaving _isAnsweringPermission stuck true and the gate
      // frozen-disabled with the permission still open server-side. Desktop's RoomClient.AnswerPermissionAsync
      // catches `Exception` + resets in a finally for exactly this reason; mirror that here.
      if (mounted) {
        setState(() => _isAnsweringPermission = false);
        final message = e is DaemonException ? e.message : e.toString();
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
      }
    }
  }

  void _scrollToEnd() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_scrollController.hasClients) return;
      _scrollController.animateTo(
        _scrollController.position.maxScrollExtent,
        duration: const Duration(milliseconds: 200),
        curve: Curves.easeOut,
      );
    });
  }

  /// Sets the send state and starts the 5-minute client-side response timeout.
  void _setupSendState(String message, int turnCount) {
    _turnsCountAtSendTime = turnCount;
    _pendingUserMessage = message;
    _liveProgressText = '';
    _lastProgressWasPartialText = false;
    _thinkingTimeText = '';
    _turnStartedAt = debugThinkingTimeClock();
    _isSending = true;
    _sendError = null;

    // The daemon runs a turn fire-and-forget in the background and never reports failure back to
    // any client (Aer.Daemon.Program's /api/sessions/send handler only logs to Console.Error) — a
    // client-side timeout is the only thing that stops this screen spinning forever if that
    // background task dies silently or a WS push never arrives.
    _sendTimeoutTimer?.cancel();
    _sendTimeoutTimer = Timer(const Duration(minutes: 5), () {
      if (mounted && _isSending) {
        setState(() {
          _isSending = false;
          // The backlog deliberately holds here rather than auto-draining (#1131 review): the
          // timeout is a client-side guess and the turn may still be running server-side, and the
          // drain's only automatic trigger requires _isSending at completion time — so queued
          // messages wait for the operator's next manual send, the same resume contract as a
          // failed drained send. Say so, or a held queue reads as a hang.
          _sendError = _queuedMessages.isEmpty
              ? 'No response after 5 minutes — the room may still be working in the background.'
              : 'No response after 5 minutes — the room may still be working in the background. Queued messages wait for your next send.';
        });
      }
    });
  }

  String _formatThinkingTime(Duration elapsed) {
    if (elapsed < _thinkingTimeReportThreshold) return '';
    final totalSeconds = elapsed.inSeconds;
    return totalSeconds < 60
        ? 'Thought for ${totalSeconds}s'
        : 'Thought for ${totalSeconds ~/ 60}m ${totalSeconds % 60}s';
  }

  Future<void> _send() async {
    final message = _inputController.text.trim();
    final metadata = _metadata;
    if (message.isEmpty || metadata == null) return;

    if (_isSending || _queuedMessages.isNotEmpty) {
      setState(() {
        _queuedMessages.add(_QueuedMessage(message));
        _inputController.clear();
        _drainPaused = false;
      });
      if (!_isSending) {
        _drainQueue();
      }
      return;
    }

    setState(() {
      _inputController.clear();
      _setupSendState(message, metadata.turnCount);
    });
    _scrollToEnd();

    try {
      await widget.client.sendSessionMessage(sessionId: _sessionId, message: message);
    } catch (e) {
      // Catch EVERY error, not just DaemonException — _answerPermission's comment above has the
      // canonical why; a narrow catch here would strand _isSending true and jam the composer.
      _sendTimeoutTimer?.cancel();
      if (mounted) {
        setState(() {
          _isSending = false;
          _pendingUserMessage = null;
          _turnStartedAt = null;
          _sendError = e is DaemonException ? e.message : e.toString();
        });
      }
    }
  }

  /// Peek-then-remove-on-success, removing by identity of the head entry per
  /// ChatViewModel.TryPeekQueuedMessage (src/Aer.Ui.Core/ChatViewModel.cs:262-274).
  Future<void> _drainQueue() async {
    final metadata = _metadata;
    if (_isSending || _queuedMessages.isEmpty || _drainPaused || metadata == null) return;

    final head = _queuedMessages.first;
    setState(() {
      _setupSendState(head.text, metadata.turnCount);
    });
    _scrollToEnd();

    try {
      await widget.client.sendSessionMessage(sessionId: _sessionId, message: head.text);
      if (mounted) {
        setState(() {
          _queuedMessages.remove(head);
        });
      }
    } catch (e) {
      // Catch EVERY error, not just DaemonException — _answerPermission's comment has the
      // canonical why; a narrow catch here would strand _isSending true and jam both the
      // composer's enqueue gate and this drain's own re-entry guard.
      _sendTimeoutTimer?.cancel();
      if (mounted) {
        setState(() {
          _isSending = false;
          _pendingUserMessage = null;
          _turnStartedAt = null;
          _drainPaused = true;
          _sendError = e is DaemonException ? e.message : e.toString();
        });
      }
    }
  }

  /// Chat capability picker (M24 Phase 2 follow-up): fetches this session's discovered skills/
  /// commands/agents (recently-used first) plus session-level mode buttons, in a bottom sheet.
  Future<void> _openCommandsSheet() async {
    if (_isLoadingCommands) return;
    setState(() => _isLoadingCommands = true);
    SessionCommandsResult? commands;
    try {
      commands = await widget.client.getSessionCommands(_sessionId);
    } on DaemonException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
      }
    } finally {
      if (mounted) setState(() => _isLoadingCommands = false);
    }
    if (commands == null || !mounted) return;

    final invokable = commands.items.where((i) => i.isInvokable).toList()
      ..sort((a, b) => (b.isRecentlyUsed ? 1 : 0).compareTo(a.isRecentlyUsed ? 1 : 0));
    final info = commands.items.where((i) => !i.isInvokable).toList();

    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (sheetContext) => SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Mode', style: Theme.of(sheetContext).textTheme.labelLarge),
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                children: [
                  ('auto', 'Auto'),
                  ('default', 'Default'),
                  ('plan', 'Plan (read-only)'),
                ].map((mode) => OutlinedButton(
                      onPressed: () async {
                        Navigator.of(sheetContext).pop();
                        try {
                          await widget.client.setSessionMode(_sessionId, mode.$1);
                          await _refreshMode();
                          if (mounted) {
                            ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Mode set to ${mode.$2}.')));
                          }
                        } on DaemonException catch (e) {
                          if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
                        }
                      },
                      child: Text(mode.$2),
                    )).toList(),
              ),
              const Divider(height: 24),
              Flexible(
                child: ListView(
                  shrinkWrap: true,
                  children: [
                    for (final item in invokable)
                      ListTile(
                        title: Text(item.name),
                        subtitle: Text(item.description),
                        trailing: item.isRecentlyUsed ? const Text('recent') : null,
                        onTap: () {
                          Navigator.of(sheetContext).pop();
                          _handleCommandItemTap(item);
                        },
                      ),
                    if (info.isNotEmpty) ...[
                      const Divider(),
                      for (final item in info)
                        ListTile(
                          dense: true,
                          title: Text(item.name, style: Theme.of(sheetContext).textTheme.bodySmall),
                          subtitle: Text(item.description, style: Theme.of(sheetContext).textTheme.bodySmall),
                        ),
                    ],
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  /// A command/skill/agent picked from the Commands sheet (#286). "/compact" and "/clear" are real
  /// dedicated actions, not text insertion — inserting them as literal text only ever "worked"
  /// because the resulting message happened to be interpreted by the vendor CLI's own (unverified,
  /// vendor-owned) slash-command handling, not because AER actually invoked anything. Everything
  /// else still inserts into the message box for the user to review/edit before Send.
  Future<void> _handleCommandItemTap(ChatCapabilityItem item) async {
    unawaited(widget.client.recordCommandUsed(_sessionId, item.name));

    switch (item.name) {
      case '/compact':
        try {
          await widget.client.compactSession(_sessionId);
          if (mounted) {
            ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Compacting room context…')));
          }
        } on DaemonException catch (e) {
          if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
        }
        break;

      case '/clear':
        try {
          final cleared = await widget.client.clearSession(_sessionId);
          if (mounted) {
            setState(() {
              if (_permissionAnswers.isNotEmpty) {
                _answersClearedThrough = _permissionAnswers
                    .map((a) => a.answeredAt)
                    .reduce((a, b) => a.isAfter(b) ? a : b);
              }
              _metadata = cleared;
            });
            ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Room context cleared.')));
          }
        } on DaemonException catch (e) {
          if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
        }
        break;

      default:
        setState(() {
          _inputController.text =
              _inputController.text.isEmpty ? item.name : '${_inputController.text} ${item.name}';
        });
        break;
    }
  }

  static String formatPermissionAnswerWording(PermissionAnswer answer) {
    if (answer.wasRevoked) {
      final reasonText = answer.reason == 'turn_ended'
          ? 'turn ended'
          : answer.reason == 'timeout'
              ? 'timed out'
              : (answer.reason ?? '');
      return 'Expired unanswered — $reasonText';
    }

    if (answer.decisionKind.startsWith('Allow')) {
      final String scope;
      switch (answer.decisionKind) {
        case PermissionDecisionKind.allowRoom:
          scope = 'for this room';
          break;
        case PermissionDecisionKind.allowCommandInRoom:
          scope = 'command in this room';
          break;
        // A literal on purpose: PermissionDecisionKind deliberately doesn't port this rung (its
        // 0052 note) — the phone never OFFERS it, but a desktop-given answer still renders here.
        case 'AllowCommandAnyRoom':
          scope = 'command in any room';
          break;
        default:
          scope = 'once';
          break;
      }
      return 'Allowed $scope — ${answer.toolName}';
    }

    final reasonSuffix = (answer.reason != null && answer.reason!.isNotEmpty) ? ': ${answer.reason}' : '';
    return 'Denied — ${answer.toolName}$reasonSuffix';
  }

  /// The 0026 §5 exhaustion sentence, derived locally to match the shape
  /// `PlainLanguage.ForExhaustion` produces (Aer.Ui.Core/RoomStepViewModels.cs) -- this file has no
  /// `intl` dependency to share the C# formatter with, so the digits are padded by hand.
  static String _forExhaustion(DateTime? exhaustedUntil) {
    if (exhaustedUntil == null) {
      return 'Out of plan — reset unknown';
    }

    final local = exhaustedUntil.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return 'Out of plan — resumes ${local.year.toString().padLeft(4, '0')}-${two(local.month)}-${two(local.day)} ${two(local.hour)}:${two(local.minute)}';
  }

  void _addTurnMessages(List<_ChatMessage> messages, SessionTurn turn) {
    messages.add(_ChatMessage(senderLabel: 'You', text: turn.humanMessage, isFromUser: true));

    if (turn.isDormancyAnswer) {
      // #1179: mirrors ChatViewModel.AddTurnMessages' IsDormancyAnswer arm (Aer.Ui.Core/ChatViewModel.cs)
      // -- see that comment for why AssistantResponse/errorMessage are never populated here and why
      // onWake gates on `_isDormant` alone rather than the latest-entered watermark.
      messages.add(
        _ChatMessage(
          senderLabel: 'System',
          text: "Still dormant — waking is yours to choose.",
          isFromUser: false,
          isDormancy: true,
          onWake: _isDormant ? _clearDormancy : null,
        ),
      );
      return;
    }

    // #1180: mirrors ChatViewModel.AddTurnMessages' IsExhausted arm -- that comment carries the
    // ordering rationale (why this precedes the failure arm, why errorMessage stays populated,
    // why a partial response still renders first).
    if (turn.isExhausted) {
      if (turn.assistantResponse != null) {
        messages.add(_ChatMessage(senderLabel: turn.vendor, text: turn.assistantResponse!, isFromUser: false));
      }

      messages.add(
        _ChatMessage(
          senderLabel: turn.vendor,
          text: _forExhaustion(turn.exhaustedUntil),
          isFromUser: false,
          isOutOfPlan: true,
          copyText: turn.errorMessage,
        ),
      );
      return;
    }

    if (turn.assistantResponse != null) {
      messages.add(_ChatMessage(senderLabel: turn.vendor, text: turn.assistantResponse!, isFromUser: false));
    }
    if (turn.errorMessage != null && turn.errorMessage!.isNotEmpty) {
      final err = turn.errorMessage!;
      messages.add(
        _ChatMessage(
          senderLabel: turn.vendor,
          text: err,
          isFromUser: false,
          isFailure: true,
          onFix: () {
            _inputController.text = 'The last turn failed with:\n> $err\nPlease diagnose and fix it.';
          },
        ),
      );
    }
  }

  /// Merges what this room has to show, whichever kind it is. [metadata] is null for a workflow
  /// room (#1226), which has no turns — so the merge below runs on the permission and dormancy
  /// streams alone, exactly as desktop's `ChatViewModel.RebuildMessages` does for the same case
  /// (`src/Aer.Ui.Core/ChatViewModel.cs:526-560`, which carries the canonical reasoning). Thin on
  /// purpose until 0054's participant and turn identity make a worker's turns renderable.
  List<_ChatMessage> _buildMessages(SessionMetadata? metadata) {
    final messages = <_ChatMessage>[];
    final turns = metadata?.turns ?? const <SessionTurn>[];
    final answers = _answersClearedThrough == null
        ? _permissionAnswers
        : _permissionAnswers.where((a) => a.answeredAt.isAfter(_answersClearedThrough!)).toList();
    final transitions = _answersClearedThrough == null
        ? _dormancyTransitions
        : _dormancyTransitions.where((t) => t.timestamp.isAfter(_answersClearedThrough!)).toList();
    // #1240: sorted here rather than assumed, and a decision with no recorded time treated as older
    // than any clear — both rules, and why they are these rules, are `ChatViewModel.RebuildMessages`'
    // (src/Aer.Ui.Core/ChatViewModel.cs) to state. This is that merge, one platform over.
    final decisions = ((_answersClearedThrough == null
                ? _projection?.recordedDecisionMoments
                : _projection?.recordedDecisionMoments
                    .where((d) => d.recordedAt.isAfter(_answersClearedThrough!))) ??
            const <RecordedDecisionMoment>[])
        .toList()
      ..sort((a, b) => a.recordedAt.compareTo(b.recordedAt));

    final latestEntered = _isDormant && transitions.any((t) => t.isEntered)
        ? transitions.lastWhere((t) => t.isEntered)
        : null;

    int turnIdx = 0;
    int ansIdx = 0;
    int transIdx = 0;
    int decIdx = 0;

    final farFuture = DateTime(9999);

    while (turnIdx < turns.length ||
        ansIdx < answers.length ||
        transIdx < transitions.length ||
        decIdx < decisions.length) {
      final turnTs = turnIdx < turns.length ? turns[turnIdx].executedAt : farFuture;
      final ansTs = ansIdx < answers.length ? answers[ansIdx].answeredAt : farFuture;
      final transTs = transIdx < transitions.length ? transitions[transIdx].timestamp : farFuture;
      // The decision arm goes LAST and every arm above it also outranks decTs, so this repo's
      // existing tie precedence — turn, then answer, then transition — is left exactly as it was.
      final decTs = decIdx < decisions.length ? decisions[decIdx].recordedAt : farFuture;

      if ((turnTs.isBefore(ansTs) || turnTs.isAtSameMomentAs(ansTs)) &&
          (turnTs.isBefore(transTs) || turnTs.isAtSameMomentAs(transTs)) &&
          (turnTs.isBefore(decTs) || turnTs.isAtSameMomentAs(decTs))) {
        _addTurnMessages(messages, turns[turnIdx]);
        turnIdx++;
      } else if ((ansTs.isBefore(transTs) || ansTs.isAtSameMomentAs(transTs)) &&
          (ansTs.isBefore(decTs) || ansTs.isAtSameMomentAs(decTs))) {
        final answer = answers[ansIdx];
        final text = formatPermissionAnswerWording(answer);
        messages.add(_ChatMessage(senderLabel: 'System', text: text, isFromUser: false, isSystem: true));
        ansIdx++;
      } else if (transTs.isBefore(decTs) || transTs.isAtSameMomentAs(decTs)) {
        final transition = transitions[transIdx];
        if (transition.isEntered) {
          final isLatest = _isDormant && transition == latestEntered;
          var text = 'Dormant — stopped after ${transition.consecutiveFailures} machine turns without progress.';
          if (transition.detail != null && transition.detail!.isNotEmpty) {
            text += '\n${transition.detail}';
          }
          messages.add(
            _ChatMessage(
              senderLabel: 'System',
              text: text,
              isFromUser: false,
              isDormancy: true,
              onWake: isLatest ? _clearDormancy : null,
            ),
          );
        } else {
          var text = 'Woken by ${transition.clearedBy}.';
          messages.add(_ChatMessage(senderLabel: 'System', text: text, isFromUser: false, isSystem: true));
        }
        transIdx++;
      } else {
        messages.add(_ChatMessage(
          senderLabel: 'System',
          text: formatRecordedDecisionWording(decisions[decIdx]),
          isFromUser: false,
          isSystem: true,
        ));
        decIdx++;
      }
    }

    if (_isSending && _pendingUserMessage != null) {
      messages.add(_ChatMessage(senderLabel: 'You', text: _pendingUserMessage!, isFromUser: true));
    }
    return messages;
  }

  /// The room's own name — the last segment of its directory, the same friendly name the rooms list
  /// shows, so the room you tapped is the room the header names.
  String get _roomName => widget.directoryPath.split(RegExp(r'[\\/]')).last;

  /// The workers this room runs, as `02-screens.md:370` writes them — `claude + agy`. Distinct
  /// adapters in first-appearance order, so the label is stable across refreshes rather than
  /// reordering under the reader.
  ///
  /// A workflow room reads them from the projection's worker-to-adapter map; a session room has the
  /// one adapter it is talking to. Null when neither is known yet — an empty separator is worse than
  /// no chips.
  String? _workerChipLabel(SessionMetadata? metadata) {
    if (_isSessionRoom) {
      // #1305: prefers the participant's name; falls back to currentAdapter on a pre-participant
      // room. Same rendering rule as desktop's ChatViewModel.WorkerChipText, where the why lives.
      final participants = metadata?.participants;
      final name = (participants != null && participants.isNotEmpty) ? participants.first.name : null;
      if (name != null && name.isNotEmpty) return name;
      final adapter = metadata?.currentAdapter;
      return (adapter == null || adapter.isEmpty) ? null : adapter;
    }

    final adapters = <String>[];
    for (final adapter in _projection?.workerAdapters.values ?? const <String>[]) {
      if (adapter.isNotEmpty && !adapters.contains(adapter)) adapters.add(adapter);
    }
    return adapters.isEmpty ? null : adapters.join(' + ');
  }

  /// Mirrors desktop's `ChatViewModel.WorkerIsOrchestrator` — see that property's doc comment for
  /// what it means and why it renders regardless of participant count. False, not just absent, on
  /// a pre-#1305 room.
  bool _workerIsOrchestrator(SessionMetadata? metadata) {
    final participants = metadata?.participants;
    if (participants == null || participants.isEmpty) return false;
    return participants.first.isOrchestrator;
  }

  /// The session-room participant's model — #1311's mobile half of 0054 §1 (#1305), mirroring
  /// desktop's `ChatViewModel.WorkerModelText`, where the name/model split's rationale lives.
  /// Workflow rooms have no single participant to read a model from (#641, out of scope), so this
  /// is null there. Null/empty renders nothing extra beside the name.
  String? _workerModelLabel(SessionMetadata? metadata) {
    if (!_isSessionRoom) return null;
    final participants = metadata?.participants;
    final model = (participants != null && participants.isNotEmpty) ? participants.first.model : null;
    return (model == null || model.isEmpty) ? null : model;
  }

  @override
  Widget build(BuildContext context) {
    final metadata = _metadata;
    final workers = _workerChipLabel(metadata);
    final workerModel = _workerModelLabel(metadata);
    final isOrchestrator = _workerIsOrchestrator(metadata);
    // Ruling 3: the reassign control hides entirely for a single-participant room — every room
    // today — rather than showing disabled. A control with no possible target is clutter.
    final reassignCandidates = (metadata?.participants ?? const <Participant>[]);
    final canReassignOrchestrator = _isSessionRoom && reassignCandidates.length > 1;

    return Scaffold(
      appBar: AppBar(
        // #1236: the room's name, in both kinds of room, with its workers beside it —
        // `02-screens.md:370` draws `‹ aer-flow    claude + agy`. A session room used to read
        // "claude — turn 4": the adapter and a turn counter, which is the engine's business, so you
        // could not tell which room you were in from inside it. Same defect slice 4 fixed on the
        // desktop, one platform over.
        title: Row(
          children: [
            Flexible(child: Text(_roomName, overflow: TextOverflow.ellipsis)),
            if (workers != null) ...[
              const SizedBox(width: 12),
              // Flexible too, not just the name: `actions` eats into this row's width, so with the
              // name already squeezed to nothing a non-flexible label has no room left to shrink
              // into and Flutter renders overflow stripes rather than clipping. Short today because
              // there are two vendors, but a third one or a large system font size is all it takes.
              Flexible(
                child: Text(
                  workers,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                ),
              ),
              if (workerModel != null) ...[
                const SizedBox(width: 6),
                // #1311, 0054 §1: the participant's model as a second, more-muted Text beside its
                // name — never concatenated into one string, mirroring desktop's name-primary/
                // model-secondary split (ChatHeaderView.axaml).
                Flexible(
                  child: Text(
                    workerModel,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: Theme.of(context).colorScheme.onSurfaceVariant.withValues(alpha: 0.7),
                        ),
                  ),
                ),
              ],
              // Orchestrator status (0054 §6, #592) — a quiet dot beside the chip, same shape and
              // same "status renders regardless of participant count" rule as desktop's
              // ChatHeaderView marker.
              if (isOrchestrator) ...[
                const SizedBox(width: 4),
                Text('●', style: TextStyle(fontSize: 8, color: Theme.of(context).colorScheme.onSurfaceVariant)),
              ],
            ],
          ],
        ),
        // Persistent mode indicator (#286): mode buttons live in the Commands & mode bottom sheet,
        // but the currently active mode was previously invisible until you reopened that sheet.
        bottom: _currentMode == null
            ? null
            : PreferredSize(
                preferredSize: const Size.fromHeight(20),
                child: Padding(
                  padding: const EdgeInsets.only(bottom: 6),
                  child: Text('Mode: $_currentMode', style: Theme.of(context).textTheme.bodySmall),
                ),
              ),
        actions: [
          // #592 (0054 §6): reassigns the orchestrator. Wired but invisible on every room today
          // (canReassignOrchestrator's own ruling-3 gate) — kept minimal on purpose: a menu of
          // candidate participants rather than a bespoke picker screen, since there is nothing yet
          // to justify more.
          if (canReassignOrchestrator)
            PopupMenuButton<String>(
              icon: const Icon(Icons.swap_horiz),
              tooltip: 'Reassign orchestrator',
              onSelected: _reassignOrchestrator,
              itemBuilder: (context) => [
                for (final participant in reassignCandidates)
                  PopupMenuItem(
                    value: participant.id,
                    enabled: !participant.isOrchestrator,
                    child: Text('Make ${participant.name} orchestrator'),
                  ),
              ],
            ),
          // Stop, for a workflow room. It came with the room rather than being left behind (#1226):
          // this was InboxScreen's only home, and routing workflow rooms here would otherwise have
          // taken the phone's ability to stop a run away until slice 6b builds the room header.
          // Losing Stop, even for one slice, is not a trade worth a tidier PR boundary.
          if (!_isSessionRoom)
            IconButton(
              icon: const Icon(Icons.stop_circle_outlined),
              tooltip: 'Stop this room',
              onPressed: _cancelRun,
            ),
          if (_isSessionRoom)
            IconButton(
              icon: _isLoadingCommands
                  ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.tune),
              tooltip: 'Commands & mode',
              onPressed: _isLoadingCommands ? null : _openCommandsSheet,
            ),
        ],
      ),
      body: Column(
        children: [
          Expanded(child: _buildBody(context, metadata)),
          if (_isSending && _liveProgressText.isNotEmpty)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(12),
              color: Theme.of(context).colorScheme.surfaceContainerHighest,
              child: Text(_liveProgressText, style: Theme.of(context).textTheme.bodySmall),
            ),
          // #483: see the field doc comment above for why this is never a live count.
          if (!_isSending && _thinkingTimeText.isNotEmpty)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
              child: Text(
                _thinkingTimeText,
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant),
              ),
            ),
          if (_sendError != null)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
              color: Theme.of(context).colorScheme.errorContainer,
              child: Text(_sendError!, style: TextStyle(color: Theme.of(context).colorScheme.onErrorContainer)),
            ),
          if (_connectionError != null)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.only(left: 12, right: 4, top: 4, bottom: 4),
              color: Theme.of(context).colorScheme.errorContainer,
              child: Row(
                children: [
                  Expanded(
                    child: Text(_connectionError!,
                        style: TextStyle(color: Theme.of(context).colorScheme.onErrorContainer)),
                  ),
                  TextButton(onPressed: _subscribeProjection, child: const Text('Reconnect')),
                ],
              ),
            ),
          if (_queuedMessages.isNotEmpty) _buildQueuedStrip(context),
          // Present but disabled in a workflow room, with a sentence saying why — 02-screens.md:57-63
          // settled that fork for both clients and desktop shipped this wording in #1204. Absent was
          // the alternative and is the wrong one: a composer that vanishes reads as a capability
          // taken away, a disabled one as a capability that has not arrived.
          if (!_isSessionRoom)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
              child: Text(
                "This room's workers aren't conversational yet — you can answer its decisions here, but not talk to it.",
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
              ),
            ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.all(8),
              child: Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _inputController,
                      enabled: _isSessionRoom,
                      minLines: 1,
                      maxLines: 5,
                      textInputAction: TextInputAction.newline,
                      decoration: const InputDecoration(hintText: 'Message', border: OutlineInputBorder()),
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton.filled(icon: const Icon(Icons.send), onPressed: _isSessionRoom ? _send : null),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildQueuedStrip(BuildContext context) {
    final theme = Theme.of(context);
    final textTheme = theme.textTheme;
    final scheme = theme.colorScheme;

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      color: scheme.surfaceContainerHighest,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            'Queued — sends when the reply finishes.',
            style: textTheme.bodySmall?.copyWith(color: scheme.onSurfaceVariant),
          ),
          const SizedBox(height: 4),
          for (int i = 0; i < _queuedMessages.length; i++)
            Row(
              children: [
                Expanded(
                  child: Text(
                    _queuedMessages[i].text,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: textTheme.bodySmall,
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close, size: 18),
                  tooltip: 'Remove',
                  visualDensity: VisualDensity.compact,
                  onPressed: () {
                    setState(() {
                      // By identity like the drain, never by captured index — one removal idiom.
                      _queuedMessages.remove(_queuedMessages[i]);
                    });
                  },
                ),
              ],
            ),
        ],
      ),
    );
  }

  Widget _buildBody(BuildContext context, SessionMetadata? metadata) {
    if (_isLoading) {
      return const Center(child: CircularProgressIndicator());
    }
    if (_loadError != null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(_loadError!, textAlign: TextAlign.center),
              const SizedBox(height: 16),
              FilledButton(onPressed: _refresh, child: const Text('Retry')),
            ],
          ),
        ),
      );
    }
    // A session room with no metadata yet has genuinely nothing to draw. A workflow room never has
    // metadata at all (#1226) and must not take this exit, or its transcript would be permanently
    // blank — the same trap desktop's RebuildMessages documents falling into.
    if (metadata == null && _isSessionRoom) {
      return const SizedBox.shrink();
    }

    final messages = _buildMessages(metadata);
    final hasGate = _pendingPermission != null;
    // The steps waiting on a person, rendered as cards at the end of the transcript — where the
    // permission gate renders, because they are the same act: a decision answered where it was
    // raised rather than on a screen of its own.
    final pausedSteps = _isSessionRoom ? const <WorkflowStepState>[] : (_projection?.pausedSteps ?? const []);
    final failedSteps = _isSessionRoom ? const <WorkflowStepState>[] : (_projection?.failedSteps ?? const []);
    // #1240: last of all, and only for a room the daemon has actually told us has stopped. An absent
    // status is unknown, not finished — a daemon older than this app, or a push with no directory to
    // probe, must leave the transcript exactly as it was rather than announce an ending.
    final stoppedStatus = _projection?.roomCardStatus;
    final hasStoppedCard = RoomStoppedCard.speaksFor(stoppedStatus);
    final itemCount =
        messages.length + (hasGate ? 1 : 0) + pausedSteps.length + failedSteps.length + (hasStoppedCard ? 1 : 0);

    return ListView.builder(
      controller: _scrollController,
      padding: const EdgeInsets.all(12),
      itemCount: itemCount,
      itemBuilder: (context, index) {
        if (index < messages.length) {
          return _MessageBubble(message: messages[index]);
        }
        if (hasGate && index == messages.length) {
          return PermissionGateCard(
            pending: _pendingPermission!,
            enabled: !_isAnsweringPermission,
            onAnswer: _answerPermission,
          );
        }
        if (hasStoppedCard && index == itemCount - 1) {
          return RoomStoppedCard(roomCardStatus: stoppedStatus!);
        }
        final baseIndex = index - messages.length - (hasGate ? 1 : 0);
        if (baseIndex < pausedSteps.length) {
          final step = pausedSteps[baseIndex];
          final projection = _projection!;
          return PausedStepCard(
            client: widget.client,
            directoryPath: projection.directoryPath,
            step: step,
            definition: projection.definitionFor(step.stepId),
            execution: projection.executionFor(step.latestExecutionId),
            workerAdapters: projection.workerAdapters,
            workerEffortTiers: projection.workerEffortTiers,
            workerDepthTiers: projection.workerDepthTiers,
            isPending: _pendingStepIds.contains(step.stepId),
            onApprove: () => _decideStep(step, 'Resume'),
            onReject: () => _decideStep(step, 'Reject'),
            onSendBack: (targetStepId, fileName) =>
                _decideStepWithReference(step, 'Supersede', targetStepId, fileName), // vocabulary-ok: decision type label
            onRetry: (fileName, supplementaryWorker, supplementaryOutputName) => _decideStepRetry(
              step,
              fileName: fileName,
              supplementaryWorker: supplementaryWorker,
              supplementaryOutputName: supplementaryOutputName,
            ),
          );
        }
        final step = failedSteps[baseIndex - pausedSteps.length];
        // No definition means the shape does not name a worker for this step. The card drops the
        // worker clause rather than substituting the step id, which would read as a worker called
        // "review" that nobody can find.
        return FailedStepCard(
          step: step,
          worker: _projection!.definitionFor(step.stepId)?.worker,
        );
      },
    );
  }

  /// Answers a paused step from the transcript — moved from `InboxScreen._decide` with its snackbar
  /// confirmation and its in-flight guard intact. The confirmation earns its place for the reason
  /// recorded there: the card vanishes as soon as the next projection lands, which without a word
  /// reads as "did that even work?".
  Future<void> _decideStep(WorkflowStepState step, String decisionType) async {
    final directoryPath = _projection?.directoryPath;
    final executionId = step.latestExecutionId;
    if (directoryPath == null || executionId == null) return;

    setState(() => _pendingStepIds.add(step.stepId));
    try {
      await widget.client.decide(
          directoryPath: directoryPath, stepId: step.stepId, executionId: executionId, decisionType: decisionType);
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(decisionType == 'Reject' ? 'Rejected ${step.stepId}' : 'Approved ${step.stepId}')),
        );
      }
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _pendingStepIds.remove(step.stepId));
    }
  }

  /// RetryWithRevision (#1323) — desktop's equivalent is `PausedStepViewModel.RetryAsync`. [fileName]
  /// is non-null only when the operator opted into attaching this step's own output as the revision —
  /// see [PausedStepCard]'s doc comment for why that's the only revision content on offer here.
  Future<void> _decideStepRetry(
    WorkflowStepState step, {
    String? fileName,
    String? supplementaryWorker,
    String? supplementaryOutputName,
  }) async {
    final directoryPath = _projection?.directoryPath;
    final executionId = step.latestExecutionId;
    if (directoryPath == null || executionId == null) return;

    setState(() => _pendingStepIds.add(step.stepId));
    try {
      await widget.client.decide(
        directoryPath: directoryPath,
        stepId: step.stepId,
        executionId: executionId,
        decisionType: 'RetryWithRevision',
        artifactReference: fileName != null ? {'executionId': executionId, 'fileName': fileName} : null,
        supplementaryWorker: supplementaryWorker,
        supplementaryOutputName: supplementaryOutputName,
      );
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text('Retry requested for ${step.stepId}')));
      }
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _pendingStepIds.remove(step.stepId));
    }
  }

  /// Stops the whole room, moved from `InboxScreen._cancelRun` unchanged — including the confirm,
  /// which earns its place because the thing being stopped is bigger than the button suggests.
  Future<void> _cancelRun() async {
    final directoryPath = _projection?.directoryPath ?? widget.directoryPath;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cancel this run?'),
        content: const Text('This stops the whole room, not just one step.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Keep running')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Cancel run')),
        ],
      ),
    );
    if (confirmed != true) return;

    try {
      await widget.client.cancelRun(directoryPath: directoryPath);
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Run cancelled')));
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  /// Send-back, moved from `InboxScreen._decideWithReference` unchanged.
  Future<void> _decideStepWithReference(
      WorkflowStepState step, String decisionType, String targetStepId, String fileName) async {
    final directoryPath = _projection?.directoryPath;
    final executionId = step.latestExecutionId;
    if (directoryPath == null || executionId == null) return;

    setState(() => _pendingStepIds.add(step.stepId));
    try {
      await widget.client.decide(
        directoryPath: directoryPath,
        stepId: step.stepId,
        executionId: executionId,
        decisionType: decisionType,
        targetStepId: targetStepId,
        artifactReference: {'executionId': executionId, 'fileName': fileName},
      );
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Sent back to $targetStepId for revision')));
      }
    } on DaemonException catch (e) {
      if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
    } finally {
      if (mounted) setState(() => _pendingStepIds.remove(step.stepId));
    }
  }
}

/// The conversational permission gate (0022, decision 0052, #390's mobile phase) — rendered inline
/// above the composer when a worker is blocked on a runtime permission, mirroring desktop's
/// `ChatView.axaml` card. A standalone `StatelessWidget` (like [MarkdownBodyWidget] below) rather
/// than a private method on [_ChatScreenState], so it's pumpable directly in a widget test without
/// [ChatScreen]'s live WebSocket dependency.
///
/// Tap-based rungs only — phone is tap-based, so 0022 §4's y/n keyboard rule is N/A here. The
/// command-family rungs (`Allow <family> in this room`, `Always deny <family>`) render only when
/// [tryReadCommandLine]/[extractCommandFamily] (ported from `ShellCommandPatternMatcher.cs`) can
/// derive one from the asked tool input — HIDDEN, not merely disabled, the same fail-closed the
/// amender applies to a command it can't parse safely. The cross-room rung is deliberately absent
/// per 0052 — the same omission, for the same reason, that desktop's `PendingPermissionViewModel`
/// documents canonically.
@visibleForTesting
class PermissionGateCard extends StatelessWidget {
  final PendingPermission pending;

  /// False while an answer is in flight — disables every rung so a slow round trip can't be
  /// double-submitted — the same in-flight discipline every decision on this screen applies.
  final bool enabled;

  /// Called with one of [PermissionDecisionKind]'s values when a rung is tapped.
  final ValueChanged<String> onAnswer;

  const PermissionGateCard({super.key, required this.pending, required this.enabled, required this.onAnswer});

  @override
  Widget build(BuildContext context) {
    final commandLine = tryReadCommandLine(pending.toolName, pending.toolInputJson);
    final hasCommand = commandLine != null && commandLine.trim().isNotEmpty;
    final commandFamily = hasCommand ? extractCommandFamily(commandLine) : null;
    final promptText =
        hasCommand ? '${pending.vendorTag} wants to run: $commandLine' : '${pending.vendorTag} wants to use ${pending.toolName}';
    final hasCommandScope = commandFamily != null;
    final scheme = Theme.of(context).colorScheme;
    final textTheme = Theme.of(context).textTheme;

    return Container(
      width: double.infinity,
      margin: const EdgeInsets.fromLTRB(12, 8, 12, 0),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        border: Border.all(color: scheme.error),
        borderRadius: BorderRadius.circular(AerTokens.radiusMd),
        color: scheme.surfaceContainerHighest,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(promptText, style: textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.bold)),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 4,
            children: [
              OutlinedButton(
                onPressed: enabled ? () => onAnswer(PermissionDecisionKind.allowOnce) : null,
                child: const Text('Allow once'),
              ),
              OutlinedButton(
                onPressed: enabled ? () => onAnswer(PermissionDecisionKind.deny) : null,
                child: const Text('Deny once'),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Text('Scope this decision:', style: textTheme.bodySmall),
          const SizedBox(height: 4),
          if (hasCommandScope)
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton(
                onPressed: enabled ? () => onAnswer(PermissionDecisionKind.allowCommandInRoom) : null,
                child: Text('Allow $commandFamily in this room'),
              ),
            ),
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton(
              onPressed: enabled ? () => onAnswer(PermissionDecisionKind.allowRoom) : null,
              child: const Text('Allow any command in this room'),
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: Text(allowRoomShellGrantReaches, style: textTheme.bodySmall),
          ),
          if (hasCommandScope)
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton(
                onPressed: enabled ? () => onAnswer(PermissionDecisionKind.denyAlways) : null,
                child: Text('Always deny $commandFamily'),
              ),
            ),
        ],
      ),
    );
  }
}

class _MessageBubble extends StatelessWidget {
  final _ChatMessage message;

  const _MessageBubble({required this.message});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    if (message.isSystem) {
      return Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: Center(
          child: Text(
            message.text,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: scheme.onSurfaceVariant.withValues(alpha: 0.7),
                ),
            textAlign: TextAlign.center,
          ),
        ),
      );
    }

    // #1180: out-of-plan styling comes from the outOfPlan token colour (AerTokens/AerStatus in
    // theme/tokens.dart, same source rooms_screen reads for the room-list status dot), never
    // scheme.errorContainer/onErrorContainer -- the state-vs-failure distinction Base.axaml's
    // .card.outofplan style draws on desktop.
    final outOfPlanColor = AerStatus.outOfPlan.color(Theme.of(context).brightness);
    final background = message.isOutOfPlan
        ? outOfPlanColor.withValues(alpha: 0.15)
        : message.isDormancy
            ? scheme.surfaceContainerHighest
            : message.isFailure
                ? scheme.errorContainer
                : message.isFromUser
                    ? scheme.primaryContainer
                    : scheme.surfaceContainerHighest;
    final foreground = message.isOutOfPlan
        ? outOfPlanColor
        : message.isDormancy
            ? scheme.onSurfaceVariant
            : message.isFailure
                ? scheme.onErrorContainer
                : message.isFromUser
                    ? scheme.onPrimaryContainer
                    : scheme.onSurfaceVariant;

    return Align(
      alignment: message.isFromUser ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        constraints: BoxConstraints(maxWidth: MediaQuery.of(context).size.width * 0.8),
        margin: const EdgeInsets.symmetric(vertical: 4),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: background, borderRadius: BorderRadius.circular(12)),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (message.isDormancy) ...[
                  const Text('⏾ ', style: TextStyle(fontSize: 12, fontWeight: FontWeight.bold)),
                ],
                Text(message.senderLabel, style: TextStyle(fontSize: 12, fontWeight: FontWeight.bold, color: foreground)),
              ],
            ),
            const SizedBox(height: 4),
            MarkdownBodyWidget(text: message.text, foreground: foreground),
            if (message.isFailure) ...[
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                children: [
                  OutlinedButton(
                    onPressed: message.onFix,
                    child: Text('Ask ${message.senderLabel} to fix'),
                  ),
                  OutlinedButton(
                    onPressed: () {
                      Clipboard.setData(ClipboardData(text: message.text));
                    },
                    child: const Text('Copy'),
                  ),
                ],
              ),
            ],
            if (message.isOutOfPlan) ...[
              // Copy only; the deliberate absence of a fix button is documented on the desktop
              // twin's IsOutOfPlan field.
              const SizedBox(height: 8),
              OutlinedButton(
                onPressed: () {
                  Clipboard.setData(ClipboardData(text: message.copyText ?? message.text));
                },
                child: const Text('Copy'),
              ),
            ],
            if (message.isDormancy && message.onWake != null) ...[
              const SizedBox(height: 8),
              OutlinedButton(
                onPressed: message.onWake,
                child: const Text('Wake'),
              ),
            ],
          ],
        ),
      ),
    );
  }
}


@visibleForTesting
class MarkdownBodyWidget extends StatelessWidget {
  final String text;
  final Color foreground;

  const MarkdownBodyWidget({
    super.key,
    required this.text,
    required this.foreground,
  });

  /// #1080 review (severe): flutter_markdown_plus builds the widget tree by recursing the parsed AST
  /// (one stack frame per nesting level), and the `markdown` package does not cap inline-emphasis
  /// depth — so thousands of `*` or nested `>` in untrusted model output (0051 §1) could overflow the
  /// render on a real device's smaller stack, which a `flutter test` pass on the host's larger stack
  /// does not disprove. Mirror the desktop guard: bound the depth before the recursive render.
  static const int _maxRenderDepth = 64;

  /// Iterative (explicit-stack) depth probe over the exact AST MarkdownBody will build (same parser,
  /// same `commonMark`/`encodeHtml: false` config), so the probe itself cannot overflow. Parsing is
  /// linear and non-recursive; only the subsequent build recurses, which this gates.
  static bool _exceedsMaxDepth(String text) {
    final document = md.Document(extensionSet: md.ExtensionSet.commonMark, encodeHtml: false);
    final nodes = document.parseLines(const LineSplitter().convert(text));
    final stack = <(md.Node, int)>[for (final node in nodes) (node, 1)];
    while (stack.isNotEmpty) {
      final (node, depth) = stack.removeLast();
      if (depth > _maxRenderDepth) return true;
      if (node is md.Element) {
        final children = node.children;
        if (children != null) {
          for (final child in children) {
            stack.add((child, depth + 1));
          }
        }
      }
    }
    return false;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;

    if (_exceedsMaxDepth(text)) {
      return SelectableText(text, style: TextStyle(color: foreground));
    }

    final codeBackground = isDark ? AerTokens.surfaceCodeDark : AerTokens.surfaceCodeLight;

    // One `code` slot styles both inline code and fenced-block text in this package, so it carries no
    // background — the flat block fill comes from codeblockDecoration below, matching desktop's
    // single-fill code block (a per-glyph background here would double-tint every code line).
    final styleSheet = MarkdownStyleSheet.fromTheme(theme).copyWith(
      p: (theme.textTheme.bodyMedium ?? const TextStyle()).copyWith(color: foreground),
      code: TextStyle(
        fontFamily: AerTokens.fontMono,
        fontSize: AerTokens.fontSizeCode,
        color: foreground,
      ),
      codeblockDecoration: BoxDecoration(
        color: codeBackground,
        borderRadius: BorderRadius.circular(AerTokens.radiusSm),
      ),
    );

    return MarkdownBody(
      data: text,
      selectable: true,
      imageBuilder: (uri, title, alt) {
        final displayText = (alt != null && alt.isNotEmpty) ? alt : '[image]';
        return Text(displayText);
      },
      extensionSet: md.ExtensionSet.commonMark,
      styleSheet: styleSheet,
    );
  }
}
