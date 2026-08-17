import 'package:flutter/material.dart';

import 'daemon/daemon_client.dart';
import 'daemon/models.dart';
import 'theme/status_mark.dart';

/// The card a step paused for your sign-off renders as: who produced it, the output it produced
/// (expandable, fetched on demand), and its resolution affordances — kind-derived per decisions
/// 0015/0040 (#1325). A [PausePointKind.needsInput] step is announced, not answerable: the engine's
/// only mechanism for resolving a NeedsInput pause is a Supersede decision carrying the answer as a
/// supplementary artifact, and the wire protocol gives a remote client no way to mint one from typed
/// text (`RevisionFilePath` names a path on the daemon's own filesystem; `ArtifactReference` only
/// points at an execution the daemon already has) — there is also no composer on this screen to type
/// an answer into. So this card renders the kind honestly, with no button that promises an answer it
/// cannot deliver. See #1334 for the tracked gap. A [PausePointKind.readyForReview] step keeps the
/// full approval set (Send back / Reject / Approve / Retry, the last gated on the same
/// `PausedOutcome != Succeeded` precondition the daemon's `ExternalDecisionValidator` enforces).
///
/// Extracted from `inbox_screen.dart` unchanged by #1226 (#1196 slice 6a) so the room's transcript
/// can render the identical card. It is the same position-move-not-redesign the desktop family made
/// slice by slice (#1145/#1174/#1177/#1181/#1185/#1204): a gate is answered where it was raised, and
/// what it looks like when you answer it does not change on the way.
class PausedStepCard extends StatefulWidget {
  final DaemonClient client;
  final String? directoryPath;
  final WorkflowStepState step;
  final StepDefinition? definition;
  final ExecutionArtifacts? execution;
  final Map<String, String> workerAdapters;

  /// See [RoomProjection.workerEffortTiers] for the shape and why absence is never defaulted.
  final Map<String, String> workerEffortTiers;
  final bool isPending;
  final VoidCallback onApprove;
  final VoidCallback onReject;
  final Function(String targetStepId, String fileName)? onSendBack;

  /// #1323: RetryWithRevision, mirroring desktop's `PausedStepViewModel.RetryAsync`. [fileName] is
  /// this step's own current output, attached as the revision only when the operator opts in via the
  /// retry dialog's checkbox (null otherwise) -- the phone has no local filesystem to author a fresh
  /// revision file from, so unlike desktop's file picker, the only revision content the wire protocol
  /// lets a phone attach is an artifact the daemon already has (the same constraint [onSendBack]'s
  /// own supplementary artifact is already bound by).
  final void Function(String? fileName, String? supplementaryWorker, String? supplementaryOutputName)? onRetry;

  const PausedStepCard({
    super.key,
    required this.client,
    required this.directoryPath,
    required this.step,
    required this.definition,
    required this.execution,
    required this.workerAdapters,
    required this.workerEffortTiers,
    required this.isPending,
    required this.onApprove,
    required this.onReject,
    this.onSendBack,
    this.onRetry,
  });

  @override
  State<PausedStepCard> createState() => _PausedStepCardState();
}

class _PausedStepCardState extends State<PausedStepCard> {
  bool _isLoadingPreview = false;
  String? _preview;

  Future<void> _loadPreview() async {
    final directoryPath = widget.directoryPath;
    final executionId = widget.step.latestExecutionId;
    final fileName = widget.execution?.outputFiles.firstOrNull;
    if (directoryPath == null || executionId == null || fileName == null || _preview != null) return;

    setState(() => _isLoadingPreview = true);
    try {
      final content = await widget.client.fetchArtifact(directoryPath: directoryPath, executionId: executionId, fileName: fileName);
      if (mounted) setState(() => _preview = content ?? '(no content)');
    } on DaemonException catch (e) {
      if (mounted) setState(() => _preview = 'Could not load preview: ${e.message}');
    } finally {
      if (mounted) setState(() => _isLoadingPreview = false);
    }
  }

  Future<void> _showRetryDialog(BuildContext context, String? attachableFileName) async {
    final result = await showDialog<_RetryResult>(
      context: context,
      builder: (context) => _RetryDialog(attachableFileName: attachableFileName),
    );
    if (result != null) {
      widget.onRetry?.call(result.fileName, result.supplementaryWorker, result.supplementaryOutputName);
    }
  }

  @override
  Widget build(BuildContext context) {
    final hasOutput = widget.execution?.outputFiles.isNotEmpty ?? false;
    final supersedeTargets = widget.definition?.supersedeTargets ?? const <String>[];
    final outputFile = widget.execution?.outputFiles.firstOrNull ?? 'draft.md';
    // Unlike [outputFile] above (which falls back to a guessed name for send-back), the retry dialog
    // must only offer to attach a real, already-produced output -- a guessed 'draft.md' would fail
    // the daemon's ArtifactReference lookup the moment the operator opted in.
    final ownOutputFile = widget.execution?.outputFiles.firstOrNull;
    final kind = widget.definition?.pausePointKind ?? PausePointKind.readyForReview;

    final workerName = widget.definition?.worker ?? widget.step.stepId;
    final adapter = widget.workerAdapters[workerName];
    final titleText = adapter != null ? '$workerName ($adapter)' : workerName; // vocabulary-ok: technical adapter setting
    final effortTier = parseCanonicalEffortTier(widget.workerEffortTiers[workerName]);

    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                buildVendorIcon(adapter),
                const SizedBox(width: 8),
                // #1318: the mobile call site RoomView.axaml's desktop chip already has -- see its
                // own comment for the pairing's reasoning. DepthMark always gets a null tier (no
                // producer yet, #1330); EffortMark is wired live off workerEffortTiers.
                DepthMark(null, size: 14),
                const SizedBox(width: 2),
                EffortMark(effortTier, size: 14),
                const SizedBox(width: 8),
                Expanded(child: Text(titleText, style: Theme.of(context).textTheme.titleMedium)),
              ],
            ),
            Text(widget.step.stepId, style: Theme.of(context).textTheme.bodySmall),
            if (hasOutput)
              ExpansionTile(
                tilePadding: EdgeInsets.zero,
                title: Text(widget.execution!.outputFiles.first),
                onExpansionChanged: (expanded) {
                  if (expanded) _loadPreview();
                },
                children: [
                  if (_isLoadingPreview) const Padding(padding: EdgeInsets.all(8), child: LinearProgressIndicator()),
                  if (_preview != null)
                    Container(
                      width: double.infinity,
                      padding: const EdgeInsets.all(8),
                      decoration: BoxDecoration(color: Theme.of(context).colorScheme.surfaceContainerHighest),
                      child: Text(_preview!, style: Theme.of(context).textTheme.bodySmall),
                    ),
                ],
              ),
            const SizedBox(height: 8),
            // Kind-derived per this class's own doc comment above (0015/0040, #1325). NeedsInput has
            // no honest affordance to offer (#1334 tracks building one); ReadyForReview's resolution
            // set: one Send back rung per declared supersede target (#1322 -- every declared target
            // must be reachable, not just the first), Retry gated on the same PausedOutcome precondition
            // the daemon enforces with an optional attached revision (#1323), Reject, Approve.
            kind == PausePointKind.needsInput
                ? Text(
                    'This step is waiting on an answer to a question. The phone has no way to answer one yet.',
                    style: Theme.of(context).textTheme.bodySmall,
                  )
                : Wrap(
                    alignment: WrapAlignment.end,
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      for (final target in supersedeTargets)
                        if (widget.onSendBack != null)
                          OutlinedButton(
                            onPressed: widget.isPending ? null : () => widget.onSendBack!(target, outputFile),
                            child: Text('Send back to $target'),
                          ),
                      // The daemon's ExternalDecisionValidator rejects RetryWithRevision outright when
                      // PausedOutcome is Succeeded (the ordinary review-and-approve case) -- offering the
                      // button there would just 400. Gate on the same precondition so the button only
                      // ever appears where it can succeed.
                      if (widget.onRetry != null && widget.step.pausedOutcome != 'Succeeded')
                        OutlinedButton(
                          onPressed: widget.isPending ? null : () => _showRetryDialog(context, ownOutputFile),
                          child: const Text('Retry…'),
                        ),
                      TextButton(onPressed: widget.isPending ? null : widget.onReject, child: const Text('Reject')),
                      FilledButton(onPressed: widget.isPending ? null : widget.onApprove, child: const Text('Approve')),
                    ],
                  ),
          ],
        ),
      ),
    );
  }
}

extension FirstOrNull<T> on List<T> {
  T? get firstOrNull => isEmpty ? null : first;
}

class _RetryResult {
  final String? fileName;
  final String? supplementaryWorker;
  final String? supplementaryOutputName;

  const _RetryResult({this.fileName, this.supplementaryWorker, this.supplementaryOutputName});
}

/// #1323's revision dialog. The phone has no filesystem to author a fresh revision file from, unlike
/// desktop's `PausedStepViewModel`, which points `RetryWithRevision` at an operator-chosen local file
/// -- so the only revision content this dialog can attach is the paused step's own already-produced
/// output, opted into via the checkbox, which the caller then sends as an `ArtifactReference` the
/// same way [PausedStepCard.onSendBack]'s supplementary artifact already is. There is no free-text
/// "write a correction" field: `/api/rooms/decide`'s `RevisionFilePath` names a path on the daemon's
/// own filesystem, never content a remote client typed, and `ArtifactReference` only resolves an
/// execution the daemon already has -- neither can carry fresh operator-authored text.
class _RetryDialog extends StatefulWidget {
  final String? attachableFileName;

  const _RetryDialog({required this.attachableFileName});

  @override
  State<_RetryDialog> createState() => _RetryDialogState();
}

class _RetryDialogState extends State<_RetryDialog> {
  bool _attach = false;
  final _workerController = TextEditingController();
  final _outputNameController = TextEditingController();

  @override
  void dispose() {
    _workerController.dispose();
    _outputNameController.dispose();
    super.dispose();
  }

  // Mirrors desktop's PausedStepViewModel.CanRetry: the revision is optional, but once attached the
  // worker/output-name pair SupplyCommand mints the supplementary execution under is not.
  bool get _canSubmit =>
      !_attach || (_workerController.text.trim().isNotEmpty && _outputNameController.text.trim().isNotEmpty);

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Retry with revision'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (widget.attachableFileName != null)
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text("Attach this step's output as the revision"),
              value: _attach,
              onChanged: (value) => setState(() => _attach = value ?? false),
            ),
          if (_attach) ...[
            TextField(
              controller: _workerController,
              decoration: const InputDecoration(labelText: 'Supplementary worker'),
              onChanged: (_) => setState(() {}),
            ),
            TextField(
              controller: _outputNameController,
              decoration: const InputDecoration(labelText: 'Supplementary output name'),
              onChanged: (_) => setState(() {}),
            ),
          ],
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
        FilledButton(
          onPressed: _canSubmit
              ? () => Navigator.pop(
                    context,
                    _RetryResult(
                      fileName: _attach ? widget.attachableFileName : null,
                      supplementaryWorker: _attach ? _workerController.text.trim() : null,
                      supplementaryOutputName: _attach ? _outputNameController.text.trim() : null,
                    ),
                  )
              : null,
          child: const Text('Retry'),
        ),
      ],
    );
  }
}

/// M22 review follow-up (issue #250): a real vendor glyph instead of a stock Material icon standing
/// in for it. Same silhouette and brand-color pairing as desktop's `Icon.Vendor.Claude`/`.Gemini` in
/// `Theme/Icons.axaml` (6-point sunburst vs. 4-point sparkle — a distinct point count so the two
/// read apart without color alone), so the two clients agree on what a vendor "looks like". Only
/// recognizes the vendors `VendorCliPresence` actually probes for (`claude`, `agy`); anything
/// else falls back to a plain neutral dot rather than inventing icon branches for adapter names
/// ("shell", "stub", "codex", "openai") this codebase never registers.
Widget buildVendorIcon(String? adapter, {double size = 18.0}) {
  final name = (adapter ?? '').toLowerCase();
  if (name.contains('claude')) {
    return CustomPaint(size: Size(size, size), painter: _VendorGlyphPainter(_VendorGlyph.claude));
  }
  if (name.contains('agy')) { // vocabulary-ok: vendor key (glyph resource keeps the Gemini brand)
    return CustomPaint(size: Size(size, size), painter: _VendorGlyphPainter(_VendorGlyph.gemini));
  }
  return CustomPaint(size: Size(size, size), painter: _VendorGlyphPainter(_VendorGlyph.generic));
}

enum _VendorGlyph { claude, gemini, generic }

class _VendorGlyphPainter extends CustomPainter {
  const _VendorGlyphPainter(this.glyph);

  final _VendorGlyph glyph;

  static const _claudeColor = Color(0xFFD97757);
  static const _geminiColor = Color(0xFF4285F4);

  @override
  void paint(Canvas canvas, Size size) {
    if (glyph == _VendorGlyph.generic) {
      final paint = Paint()..color = Colors.grey;
      canvas.drawCircle(size.center(Offset.zero), size.shortestSide * 0.28, paint);
      return;
    }

    // Points authored on Aer.Ui's 16x16 icon grid, then scaled to this glyph's actual size —
    // identical coordinates to Icon.Vendor.Claude/.Gemini in Theme/Icons.axaml.
    final points = glyph == _VendorGlyph.claude
        ? const [
            Offset(8, 2), Offset(9.2, 5.9), Offset(13.2, 5), Offset(10.4, 8),
            Offset(13.2, 11), Offset(9.2, 10.1), Offset(8, 14), Offset(6.8, 10.1),
            Offset(2.8, 11), Offset(5.6, 8), Offset(2.8, 5), Offset(6.8, 5.9),
          ]
        : const [
            Offset(8, 1.5), Offset(9.4, 6.6), Offset(14.5, 8), Offset(9.4, 9.4),
            Offset(8, 14.5), Offset(6.6, 9.4), Offset(1.5, 8), Offset(6.6, 6.6),
          ];

    final scale = size.shortestSide / 16.0;
    final path = Path()..addPolygon(points.map((p) => p * scale).toList(), true);
    final paint = Paint()
      ..color = glyph == _VendorGlyph.claude ? _claudeColor : _geminiColor
      ..style = PaintingStyle.fill;
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(covariant _VendorGlyphPainter oldDelegate) => oldDelegate.glyph != glyph;
}
