/// Dart port of two rules from `src/Aer.Adapters/ShellCommandPatternMatcher.cs`
/// (`TryReadCommandLine` and `ExtractCommandFamily`) — that C# type is the canonical rule; this
/// mirrors it the same way `models.dart` mirrors the C# wire models, because the permission gate's
/// command-family derivation has to agree with the engine's on both platforms or a rung offered here
/// could silently mean something different from what the amender persists. Only the two derivation
/// rules are ported — matching/allow-list evaluation stays engine-side.
library;

import 'dart:convert';

/// The claude/agy tool names a shell command line can be read from — claude's `Bash` and agy's
/// `run_command`. Mirrors `ShellCommandPatternMatcher.ShellToolNames`.
const shellToolNames = ['Bash', 'run_command'];

/// First whitespace-delimited-token scan's metacharacter set — mirrors
/// `ShellCommandPatternMatcher.MetaCharacters` exactly (used by [extractCommandFamily] only; this
/// port does not need the fuller quoting-aware scan `IsAllowed` does, since the gate only ever
/// derives a family for *display*, never evaluates a command against a persisted pattern).
const _metaCharacters = [';', '&', '|', '`', r'$', '<', '>', '(', ')', '\n', '\r', '\\', "'", '"'];

/// Reads the raw shell command line (e.g. "rm -rf build/") out of a shell tool's asked input, or
/// returns null when [toolName] isn't a recognized shell tool ([shellToolNames]) or the input JSON
/// can't be parsed / carries neither key. Mirrors `TryReadCommandLine`: "command" is claude's Bash
/// tool_input key, "CommandLine" is agy's run_command arg key.
String? tryReadCommandLine(String toolName, String toolInputJson) {
  if (!shellToolNames.contains(toolName)) return null;

  try {
    final decoded = jsonDecode(toolInputJson);
    if (decoded is! Map<String, dynamic>) return null;

    final command = decoded['command'];
    if (command is String) return command;

    final commandLine = decoded['CommandLine'];
    if (commandLine is String) return commandLine;

    return null;
  } on FormatException {
    return null;
  }
}

/// Derives a command's family (its first whitespace-delimited token, e.g. "rm" out of
/// "rm -rf build/") for scoping the `AllowCommandInRoom`/`DenyAlways` rungs. Returns null — never a
/// guess — when [commandLine] is empty/blank or its first token opens with a shell metacharacter
/// this scan already treats as unsafe to reason about. Mirrors `ExtractCommandFamily` exactly.
String? extractCommandFamily(String? commandLine) {
  if (commandLine == null || commandLine.trim().isEmpty) return null;

  final trimmed = commandLine.trimLeft();
  var end = 0;
  while (end < trimmed.length && !_isWhitespace(trimmed[end]) && !_metaCharacters.contains(trimmed[end])) {
    end++;
  }

  return end == 0 ? null : trimmed.substring(0, end);
}

bool _isWhitespace(String char) => char.trim().isEmpty;
