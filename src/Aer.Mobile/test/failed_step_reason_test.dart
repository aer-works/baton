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

    test('empty excerpt after trim returns null excerpt', () {
      expect(
        splitReasonAndStderr(' Step failed. stderr:   '),
        ('Step failed.', null),
      );
    });
  });
}
