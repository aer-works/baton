import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/daemon/failed_step_reason.dart';

void main() {
  group('splitReasonAndStderr (#1245)', () {
    test('null or blank reason returns default sentence and null excerpt', () {
      expect(splitReasonAndStderr(null), ('Step failed.', null));
      expect(splitReasonAndStderr(''), ('Step failed.', null));
      expect(splitReasonAndStderr('   '), ('Step failed.', null));
    });

    test('reason with no separator returns trimmed whole string and null excerpt', () {
      expect(splitReasonAndStderr(' Worker exited 1 '), ('Worker exited 1', null));
    });

    test('reason with separator splits into trimmed sentence and trimmed excerpt', () {
      expect(
        splitReasonAndStderr(' Step failed. stderr:  error output '),
        ('Step failed.', 'error output'),
      );
    });

    test('a cut tail keeps the truncation mark that says it was cut', () {
      // The one input class the C# original singles out as must-survive, and the reason it does:
      // strip the mark and a cut tail reads as the whole capture. `trim()` takes whitespace, and
      // U+2026 is not whitespace — so this passes today, and the point of the arm is that a later
      // "tidy up the excerpt" edit cannot quietly break it. Twin of
      // OutcomeClassifierTests.SplitReasonAndStderr_keeps_the_truncation_ellipsis_on_a_cut_tail.
      expect(
        splitReasonAndStderr('Worker exited with non-zero code 1. stderr: …cc: no such file'),
        ('Worker exited with non-zero code 1.', '…cc: no such file'),
      );
    });

    test('empty excerpt after trim returns null excerpt', () {
      expect(
        splitReasonAndStderr(' Step failed. stderr:   '),
        ('Step failed.', null),
      );
    });
  });
}
