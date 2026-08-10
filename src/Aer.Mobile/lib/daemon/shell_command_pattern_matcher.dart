/// Dart port of two rules from `src/Aer.Adapters/ShellCommandPatternMatcher.cs`
/// (`TryReadCommandLine` and `ExtractCommandFamily`) — that C# type is the canonical rule; this
/// mirrors it the same way `models.dart` mirrors the C# wire models, because the permission gate's
/// command-family derivation has to agree with the engine's on both platforms or a rung offered here
/// could silently mean something different from what the amender persists. Only the two derivation
/// rules are ported — matching/allow-list evaluation stays engine-side.
library;

import 'dart:convert';

/// The shell tool names this port recognizes — mirrors `ShellCommandPatternMatcher.ShellToolNames`
/// (the canonical list). The values are the wire contract with each vendor's CLI.
const shellToolNames = ['Bash', 'run_command'];

/// Metacharacter set for the family scan — mirrors `ShellCommandPatternMatcher.MetaCharacters`
/// (canonical). Used by [extractCommandFamily] only; see the C# for why only this subset of the
/// matcher is ported to the phone.
const _metaCharacters = [';', '&', '|', '`', r'$', '<', '>', '(', ')', '\n', '\r', '\\', "'", '"'];

/// Ports `TryReadCommandLine` (C# canonical): returns the command string for a recognized shell
/// [toolName], or null when the tool isn't in [shellToolNames] or the JSON parse fails / has neither
/// key. Which key belongs to which vendor is read in order below.
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

/// Derives the command family that scopes the `AllowCommandInRoom`/`DenyAlways` rungs — ports
/// `ShellCommandPatternMatcher.ExtractCommandFamily` (C# canonical). Null (never a guess) for a
/// blank [commandLine], or when the leading token would start on a metacharacter.
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
