import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:markdown/markdown.dart' as md;
import 'package:flutter_markdown_plus/flutter_markdown_plus.dart';

import 'daemon/daemon_client.dart';
import 'daemon/models.dart';
import 'daemon/permission_decision_kind.dart';
import 'daemon/permission_grant_wording.dart';
import 'daemon/shell_command_pattern_matcher.dart';
import 'theme/tokens.dart';

/// One rendered row in the chat transcript — a human turn or an assistant response, never both.
/// Mirrors Aer.Ui.Core's ChatMessageViewModel (see ChatViewModel.cs).
class _ChatMessage {
  final String senderLabel;
  final String text;
  final bool isFromUser;

  _ChatMessage({required this.senderLabel, required this.text, required this.isFromUser});
}

/// The mobile chat/codebase-session screen (M24, issue #262) — the Flutter counterpart of
/// Aer.Ui's dedicated Chat view. `Turns` (the actual message content) live outside RoomProjection
/// entirely, in SessionMetadata, so this screen re-fetches GET /api/sessions/{sessionId} rather
/// than reading anything off InboxScreen's projection.
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
  final String sessionId;
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

  /// A dropped projection socket, surfaced as a recoverable banner (mirrors InboxScreen's
  /// `_connectionError`). Without this the `watch()` stream's error was swallowed silently and every
  /// future push — including the inline permission gate's appear/clear — stopped arriving with no
  /// signal to the user (found while live-driving #390's mobile gate).
  String? _connectionError;

  bool _isSending = false;
  String? _pendingUserMessage;
  int _turnsCountAtSendTime = 0;
  String _liveProgressText = '';

  bool _isLoadingCommands = false;

  /// The active session mode (#286), or null until [_refreshMode] resolves it — shown persistently
  /// in the AppBar rather than only reflected transiently right after a mode-button tap.
  String? _currentMode;

  /// The open gate (see [PendingPermission]) this screen renders inline above the composer when
  /// non-null. A projected fact — see [_surfacePendingPermission] — never edited directly, and
  /// re-derived (never carried over) whenever the projection's own value changes.
  PendingPermission? _pendingPermission;

  /// True while an answer POST is in flight — disables the gate's rungs (mirrors InboxScreen's
  /// `_decide`'s in-flight discipline) so a slow round trip can't be double-submitted. Independent of
  /// [_pendingPermission]'s identity: the gate disappears entirely once the next projection clears
  /// it, so this never needs to be reset on success.
  bool _isAnsweringPermission = false;

  @override
  void initState() {
    super.initState();
    _refresh();
    _refreshMode();
    _subscribeProjection();
    _progressSubscription = widget.client.watchProgress().listen((event) {
      if (!mounted) return;
      if (event.directoryPath == widget.directoryPath && _isSending) {
        setState(() => _liveProgressText += event.text);
      }
    });
  }

  /// (Re)subscribes to the daemon's filtered projection stream. Mirrors InboxScreen's `_connect`:
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
          _surfacePendingPermission(projection.pendingPermission);
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
    try {
      final metadata = await widget.client.getSession(widget.sessionId);
      if (!mounted) return;
      setState(() {
        _metadata = metadata;
        _isLoading = false;
        _loadError = null;

        if (_isSending && metadata.turnCount > _turnsCountAtSendTime) {
          _isSending = false;
          _liveProgressText = '';
          _pendingUserMessage = null;
          _sendTimeoutTimer?.cancel();
        }
      });
      _scrollToEnd();
    } on DaemonException catch (e) {
      if (mounted) setState(() { _isLoading = false; _loadError = e.message; });
    }
  }

  /// Best-effort: a stale/missing mode indicator is cosmetic, not worth surfacing as a chat error.
  Future<void> _refreshMode() async {
    try {
      final mode = await widget.client.getSessionMode(widget.sessionId);
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
  }

  /// Answers the open gate with one of [PermissionDecisionKind]'s rungs — mirrors InboxScreen's
  /// `_decide`: disable-during-flight, then let the next projection push (via
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

  Future<void> _send() async {
    final message = _inputController.text.trim();
    final metadata = _metadata;
    if (message.isEmpty || metadata == null || _isSending) return;

    setState(() {
      _turnsCountAtSendTime = metadata.turnCount;
      _pendingUserMessage = message;
      _liveProgressText = '';
      _isSending = true;
      _sendError = null;
      _inputController.clear();
    });
    _scrollToEnd();

    // The daemon runs a turn fire-and-forget in the background and never reports failure back to
    // any client (Aer.Daemon.Program's /api/sessions/send handler only logs to Console.Error) — a
    // client-side timeout is the only thing that stops this screen spinning forever if that
    // background task dies silently or a WS push never arrives.
    _sendTimeoutTimer?.cancel();
    _sendTimeoutTimer = Timer(const Duration(minutes: 5), () {
      if (mounted && _isSending) {
        setState(() {
          _isSending = false;
          _sendError = 'No response after 5 minutes — the room may still be working in the background.';
        });
      }
    });

    try {
      await widget.client.sendSessionMessage(sessionId: widget.sessionId, message: message);
    } on DaemonException catch (e) {
      _sendTimeoutTimer?.cancel();
      if (mounted) {
        setState(() {
          _isSending = false;
          _pendingUserMessage = null;
          _sendError = e.message;
        });
      }
    }
  }

  /// Chat capability picker (M24 Phase 2 follow-up): fetches this session's discovered skills/
  /// commands/agents (recently-used first) plus session-level mode buttons, in a bottom sheet
  /// matching InboxScreen's own `_pickRecentRoom` idiom.
  Future<void> _openCommandsSheet() async {
    if (_isLoadingCommands) return;
    setState(() => _isLoadingCommands = true);
    SessionCommandsResult? commands;
    try {
      commands = await widget.client.getSessionCommands(widget.sessionId);
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
                          await widget.client.setSessionMode(widget.sessionId, mode.$1);
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
    unawaited(widget.client.recordCommandUsed(widget.sessionId, item.name));

    switch (item.name) {
      case '/compact':
        try {
          await widget.client.compactSession(widget.sessionId);
          if (mounted) {
            ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Compacting room context…')));
          }
        } on DaemonException catch (e) {
          if (mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.message)));
        }
        break;

      case '/clear':
        try {
          final cleared = await widget.client.clearSession(widget.sessionId);
          if (mounted) {
            setState(() => _metadata = cleared);
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

  List<_ChatMessage> _buildMessages(SessionMetadata metadata) {
    final messages = <_ChatMessage>[];
    for (final turn in metadata.turns) {
      messages.add(_ChatMessage(senderLabel: 'You', text: turn.humanMessage, isFromUser: true));
      if (turn.assistantResponse != null) {
        messages.add(_ChatMessage(senderLabel: turn.vendor, text: turn.assistantResponse!, isFromUser: false));
      }
    }
    if (_isSending && _pendingUserMessage != null) {
      messages.add(_ChatMessage(senderLabel: 'You', text: _pendingUserMessage!, isFromUser: true));
    }
    return messages;
  }

  @override
  Widget build(BuildContext context) {
    final metadata = _metadata;
    final title = metadata == null
        ? widget.directoryPath.split(RegExp(r'[\\/]')).last
        : '${metadata.currentAdapter} — turn ${metadata.turnCount}';

    return Scaffold(
      appBar: AppBar(
        title: Text(title),
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
          if (_pendingPermission != null)
            PermissionGateCard(
              pending: _pendingPermission!,
              enabled: !_isAnsweringPermission,
              onAnswer: _answerPermission,
            ),
          if (_isSending && _liveProgressText.isNotEmpty)
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(12),
              color: Theme.of(context).colorScheme.surfaceContainerHighest,
              child: Text(_liveProgressText, style: Theme.of(context).textTheme.bodySmall),
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
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.all(8),
              child: Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _inputController,
                      minLines: 1,
                      maxLines: 5,
                      textInputAction: TextInputAction.newline,
                      decoration: const InputDecoration(hintText: 'Message', border: OutlineInputBorder()),
                      enabled: !_isSending,
                    ),
                  ),
                  const SizedBox(width: 8),
                  _isSending
                      ? const SizedBox(width: 48, height: 48, child: Padding(padding: EdgeInsets.all(12), child: CircularProgressIndicator(strokeWidth: 2)))
                      : IconButton.filled(icon: const Icon(Icons.send), onPressed: _send),
                ],
              ),
            ),
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
    if (metadata == null) {
      return const SizedBox.shrink();
    }

    final messages = _buildMessages(metadata);
    return ListView.builder(
      controller: _scrollController,
      padding: const EdgeInsets.all(12),
      itemCount: messages.length,
      itemBuilder: (context, index) => _MessageBubble(message: messages[index]),
    );
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
  /// double-submitted (mirrors `InboxScreen._decide`'s in-flight discipline).
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
              FilledButton(
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
    final background = message.isFromUser ? scheme.primaryContainer : scheme.surfaceContainerHighest;
    final foreground = message.isFromUser ? scheme.onPrimaryContainer : scheme.onSurfaceVariant;

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
            Text(message.senderLabel, style: TextStyle(fontSize: 12, fontWeight: FontWeight.bold, color: foreground)),
            const SizedBox(height: 4),
            MarkdownBodyWidget(text: message.text, foreground: foreground),
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
