import 'package:flutter/material.dart';

import 'daemon/daemon_client.dart';
import 'daemon/models.dart';

/// The card a step paused for your sign-off renders as: who produced it, the output it produced
/// (expandable, fetched on demand), and its resolution affordances — kind-derived per decisions
/// 0015/0040 (#1325): a [PausePointKind.needsInput] step offers one Reply rung, since it is an
/// ordinary chat turn awaiting your next message, not a review; a [PausePointKind.readyForReview]
/// step keeps the full approval set (Send back / Reject / Approve).
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
  final bool isPending;
  final VoidCallback onApprove;
  final VoidCallback onReject;
  final Function(String targetStepId, String fileName)? onSendBack;

  const PausedStepCard({
    super.key,
    required this.client,
    required this.directoryPath,
    required this.step,
    required this.definition,
    required this.execution,
    required this.workerAdapters,
    required this.isPending,
    required this.onApprove,
    required this.onReject,
    this.onSendBack,
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

  @override
  Widget build(BuildContext context) {
    final hasOutput = widget.execution?.outputFiles.isNotEmpty ?? false;
    final supersedeTarget = widget.definition?.supersedeTargets.firstOrNull;
    final outputFile = widget.execution?.outputFiles.firstOrNull ?? 'draft.md';
    final kind = widget.definition?.pausePointKind ?? PausePointKind.readyForReview;

    final workerName = widget.definition?.worker ?? widget.step.stepId;
    final adapter = widget.workerAdapters[workerName];
    final titleText = adapter != null ? '$workerName ($adapter)' : workerName; // vocabulary-ok: technical adapter setting

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
            // 0015/0040 (#1325): a decision-kind pause (NeedsInput) is an ordinary chat turn awaiting
            // your next message, not a review -- it gets one honest Reply rung, never Approve/Reject,
            // which is the exact confusion 0015 exists to design out. An approval-kind pause
            // (ReadyForReview) keeps the full resolution set: Send back, Reject, Approve.
            kind == PausePointKind.needsInput
                ? Align(
                    alignment: Alignment.centerRight,
                    child: FilledButton(onPressed: widget.isPending ? null : widget.onApprove, child: const Text('Reply')),
                  )
                : Row(
                    mainAxisAlignment: MainAxisAlignment.end,
                    children: [
                      if (supersedeTarget != null && widget.onSendBack != null) ...[
                        OutlinedButton(
                          onPressed: widget.isPending ? null : () => widget.onSendBack!(supersedeTarget, outputFile),
                          child: Text('Send back to $supersedeTarget'),
                        ),
                        const SizedBox(width: 8),
                      ],
                      TextButton(onPressed: widget.isPending ? null : widget.onReject, child: const Text('Reject')),
                      const SizedBox(width: 8),
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
