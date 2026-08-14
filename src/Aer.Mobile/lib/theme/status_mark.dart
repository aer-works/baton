import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'tokens.dart';

/// Draws a status's mark (#458).
///
/// Decision 0006 requires status to read without colour, and the original mechanism — a Unicode
/// character per state — cannot deliver that here: three of the five codepoints are absent from
/// Source Sans 3, one from JetBrains Mono, and between them the two shipped faces carry no
/// checkmark and no cross at all. A codepoint a font lacks renders as tofu or falls back to
/// whatever the device happens to have, which is the per-device resolution 0006 exists to rule out
/// — arriving on the one element the accessibility rule depends on.
///
/// So `design/tokens.json` names a *shape* and each toolkit draws it. These coordinates are
/// authored on the same 16x16 grid as `Aer.Ui/Theme/Icons.axaml` and match its `Icon.*` geometries
/// point for point, following the precedent `_VendorGlyphPainter` in `inbox_screen.dart` already
/// set for cross-toolkit shapes. `Aer.Architecture.Tests` fails the build if a status names a mark
/// this file does not handle.
///
/// The marks differ in silhouette, not merely in colour — open arc, solid bubble, wide lens,
/// angular line, angular X, bar, three dots, slashed circle. Marks that could only be told apart
/// once you could see their colour would satisfy a literal reading of the rule and fail the people
/// it is for.
///
/// Whether a shape is solid or stroked comes from the token's `filled`, never from this file
/// (#461): the two toolkits previously decided independently and drew the same status two
/// different ways.
class StatusMark extends StatelessWidget {
  const StatusMark(this.status, {super.key, this.size = 16.0, this.color});

  final AerStatus status;
  final double size;

  /// Defaults to the status's own colour for the ambient brightness. Pass a colour to render the
  /// mark against a surface that already carries the status hue, where repeating it would vanish.
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final resolved = color ?? status.color(Theme.of(context).brightness);
    return Semantics(
      label: status.label,
      child: CustomPaint(
        size: Size(size, size),
        painter: _StatusMarkPainter(mark: status.mark, filled: status.markFilled, color: resolved),
      ),
    );
  }
}

class _StatusMarkPainter extends CustomPainter {
  const _StatusMarkPainter({required this.mark, required this.filled, required this.color});

  final String mark;

  /// From the token file, never decided here - see [StatusMark]'s notes on #461.
  final bool filled;

  final Color color;

  /// The grid these coordinates are authored on, shared with `Icons.axaml`.
  static const double _grid = 16.0;

  /// Proportional to the grid so the stroke keeps its weight when the mark is scaled.
  static const double _strokeOnGrid = 1.6;

  @override
  void paint(Canvas canvas, Size size) {
    final scale = size.shortestSide / _grid;
    Offset at(double x, double y) => Offset(x * scale, y * scale);

    final stroke = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = _strokeOnGrid * scale
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;

    final fill = Paint()
      ..color = color
      ..style = PaintingStyle.fill;

    // The shape's own paint comes from the token's `filled`, so desktop and mobile cannot disagree.
    // Composite marks keep their detail paint explicit (the eye's pupil is filled either way).
    final primary = filled ? fill : stroke;

    switch (mark) {
      // Idle: a solid disc — the state a room is created in, and the one it rests in. Filled rather
      // than stroked on purpose (#489): Icon.Dot is a closed circle and Icon.Ring is a 240-degree
      // arc, so as outlines the two differ only by a gap, which is not a silhouette difference at
      // 16px in greyscale. A disc against an open arc is.
      case 'dot':
        canvas.drawCircle(at(8, 8), 5 * scale, primary);
      // An open ring — the static frame of a spinner. Matches Icon.Ring's arc: centred at (8,8)
      // with radius 5, starting at the top and sweeping 240 degrees, leaving the gap that stops it
      // reading as a plain circle.
      case 'ring':
        canvas.drawArc(
          Rect.fromCircle(center: at(8, 8), radius: 5 * scale),
          -math.pi / 2,
          240 * math.pi / 180,
          false,
          stroke,
        );
      // A speech bubble - "your turn to reply". Filled: this is the loudest state in the set.
      case 'bubble':
        canvas.drawPath(
          Path()
            ..addPolygon([
              at(3, 3.5), at(13, 3.5), at(13, 10.5),
              at(7, 10.5), at(4.5, 13.5), at(4.5, 10.5), at(3, 10.5),
            ], true),
          primary,
        );
      // An eye - there is a result here for your eyes to judge. Two mirrored cubics for the lid,
      // matching Icon.Eye's control points, plus the pupil.
      case 'eye':
        canvas.drawPath(
          Path()
            ..moveTo(at(2.5, 8).dx, at(2.5, 8).dy)
            ..cubicTo(at(5, 3.6).dx, at(5, 3.6).dy, at(11, 3.6).dx, at(11, 3.6).dy, at(13.5, 8).dx, at(13.5, 8).dy)
            ..cubicTo(at(11, 12.4).dx, at(11, 12.4).dy, at(5, 12.4).dx, at(5, 12.4).dy, at(2.5, 8).dx, at(2.5, 8).dy)
            ..close(),
          stroke,
        );
        canvas.drawCircle(at(8, 8), 1.9 * scale, fill);
      // Cancelled: a bare dash - "no outcome". Never a filled square; that is a stop *control*.
      case 'dash':
        canvas.drawLine(at(3.5, 8), at(12.5, 8), stroke);
      // Stopped (#1219): a square outline — the run halted because its process died. Matches
      // Icon.Square: (3.5,3.5) to (12.5,12.5) on the same grid. Stroked, never filled, for exactly
      // the reason the 'dash' case above gives: a *filled* square is a stop control, and a state
      // that looks like an action is a trap (owner review, #461). The outline keeps the one
      // hard-edged silhouette in a set otherwise made of circles and arcs.
      case 'square':
        canvas.drawRect(
          Rect.fromPoints(at(3.5, 3.5), at(12.5, 12.5)),
          stroke,
        );
      // Queued: an ellipsis, filled so it holds at row size.
      case 'ellipsis':
        for (final cx in [4.2, 8.0, 11.8]) {
          canvas.drawCircle(at(cx, 8), 1.35 * scale, primary);
        }
      // Unavailable: a slashed circle - recorded, no longer readable.
      case 'slashed':
        canvas.drawCircle(at(8, 8), 5 * scale, stroke);
        canvas.drawLine(at(4.5, 11.5), at(11.5, 4.5), stroke);
      case 'check':
        canvas.drawPath(
          Path()
            ..moveTo(at(3, 8.5).dx, at(3, 8.5).dy)
            ..lineTo(at(6.5, 12).dx, at(6.5, 12).dy)
            ..lineTo(at(13, 4).dx, at(13, 4).dy),
          stroke,
        );
      case 'cross':
        canvas.drawLine(at(4, 4), at(12, 12), stroke);
        canvas.drawLine(at(12, 4), at(4, 12), stroke);
      default:
        // Never silently draw nothing: a status whose mark this file does not know would otherwise
        // render as empty space and read as "no status" rather than as a bug. The drift gate makes
        // this unreachable in a built tree; this is what happens if it is ever bypassed.
        throw ArgumentError.value(mark, 'mark', 'No painter for this status mark');
    }
  }

  @override
  bool shouldRepaint(_StatusMarkPainter oldDelegate) =>
      oldDelegate.mark != mark || oldDelegate.filled != filled || oldDelegate.color != color;
}
