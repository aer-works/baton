import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';

import 'package:aer_mobile/daemon/shell_command_pattern_matcher.dart';

/// Dart-side coverage of the two rules ported from `ShellCommandPatternMatcher.cs`
/// (`TryReadCommandLine`, `ExtractCommandFamily`) — see that file's own doc comments for the
/// canonical behavior this mirrors.
void main() {
  group('tryReadCommandLine', () {
    test('reads claude Bash tool_input\'s "command" key', () {
      expect(tryReadCommandLine('Bash', jsonEncode({'command': 'rm -rf build/'})), 'rm -rf build/');
    });

    test('reads agy run_command\'s "CommandLine" key', () {
      expect(tryReadCommandLine('run_command', jsonEncode({'CommandLine': 'git status'})), 'git status');
    });

    test('returns null for a tool name that is not a recognized shell tool', () {
      expect(tryReadCommandLine('Edit', jsonEncode({'command': 'rm -rf build/'})), isNull);
    });

    test('returns null for unparseable JSON', () {
      expect(tryReadCommandLine('Bash', 'not json'), isNull);
    });

    test('returns null when neither key is present', () {
      expect(tryReadCommandLine('Bash', jsonEncode({'foo': 'bar'})), isNull);
    });
  });

  group('extractCommandFamily', () {
    test('returns the first whitespace-delimited token', () {
      expect(extractCommandFamily('rm -rf build/'), 'rm');
    });

    test('returns null for an empty or blank command line', () {
      expect(extractCommandFamily(''), isNull);
      expect(extractCommandFamily('   '), isNull);
      expect(extractCommandFamily(null), isNull);
    });

    test('returns null (fail-closed) when the command opens with a command-substitution metacharacter', () {
      // The case the mobile task calls out explicitly: a $(...) command must hide the family rungs
      // rather than derive a bogus family from it.
      expect(extractCommandFamily(r'$(whoami)'), isNull);
    });

    test('returns null (fail-closed) when the command opens with any other shell metacharacter', () {
      expect(extractCommandFamily(';rm -rf /'), isNull);
      expect(extractCommandFamily('`whoami`'), isNull);
      expect(extractCommandFamily('|cat'), isNull);
    });

    test('stops the family at a trailing metacharacter', () {
      expect(extractCommandFamily('rm;whoami'), 'rm');
    });
  });
}
