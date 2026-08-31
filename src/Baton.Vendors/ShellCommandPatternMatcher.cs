using System.Linq;
using System.Text.Json;

namespace Baton.Vendors;


/// <summary>
/// Evaluates a shell command line against a pattern allowlist using claude-compatible
/// <c>Bash(pattern)</c> glob semantics, enforcing strict shell metacharacter rejection (#659).
/// </summary>
public static class ShellCommandPatternMatcher
{
    /// <summary>
    /// The claude/agy tool names a shell command line can be read from — claude's <c>Bash</c> and
    /// agy's <c>run_command</c>. The one canonical list (record-once): the grant amender's
    /// pattern derivation and the gate UI's command display both gate on this rather than each
    /// restating the pair, and any other tool name reads back no command line at all.
    /// </summary>
    public static readonly string[] ShellToolNames = ["Bash", "run_command"];

    /// <summary>
    /// Reads the raw shell command line (e.g. <c>"rm -rf build/"</c>) out of a shell tool's asked
    /// input, or returns <see langword="false"/> when <paramref name="toolName"/> isn't a recognized
    /// shell tool (<see cref="ShellToolNames"/>) or the input JSON can't be parsed. This is the
    /// display/derivation seam only: callers that need a scoped <em>pattern</em> pass the result
    /// through <see cref="ExtractCommandFamily"/> themselves, which is where the fail-closed
    /// metacharacter rule lives.
    /// </summary>
    /// <param name="toolName">The originally-asked tool name (e.g. <c>"Bash"</c>).</param>
    /// <param name="toolInputJson">The originally-asked tool input JSON.</param>
    /// <param name="commandLine">The read command line, or <see langword="null"/> on any miss.</param>
    public static bool TryReadCommandLine(string toolName, string toolInputJson, out string? commandLine)
    {
        commandLine = null;
        if (toolName is null || toolInputJson is null || !ShellToolNames.Contains(toolName, StringComparer.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // "command" is claude's Bash tool_input key; "CommandLine" is agy's run_command arg key
            // (AgyHookCheckCommand reads the same name for the same tool).
            if (doc.RootElement.TryGetProperty("command", out var commandProp) &&
                commandProp.ValueKind == JsonValueKind.String)
            {
                commandLine = commandProp.GetString();
            }
            else if (doc.RootElement.TryGetProperty("CommandLine", out var commandLineProp) &&
                commandLineProp.ValueKind == JsonValueKind.String)
            {
                commandLine = commandLineProp.GetString();
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return commandLine is not null;
    }

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="commandLine"/> contains no unquoted shell
    /// metacharacters and matches at least one pattern in <paramref name="patterns"/>.
    /// </summary>
    /// <param name="commandLine">The command line to evaluate.</param>
    /// <param name="patterns">The pattern allowlist (e.g. <c>["git *"]</c>).</param>
    public static bool IsAllowed(string? commandLine, IReadOnlyList<string>? patterns)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || patterns is null || patterns.Count == 0)
        {
            return false;
        }

        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (inSingleQuote)
            {
                // Single quotes are fully literal in POSIX shells — no expansion of any kind,
                // not even a backslash escape — so nothing inside them can execute. The only
                // character that matters is the closing quote. Do not add substitution checks here.
                if (c == '\'')
                {
                    inSingleQuote = false;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\')
                {
                    // A backslash inside double quotes escapes the next character (bash does this
                    // for $ ` " \ and newline). Skipping it is what keeps `"\$(x)"` — an escaped,
                    // non-executing literal — allowed, while `"\a$(x)"` still trips the $( below.
                    i++;
                    continue;
                }
                // Command substitution and parameter expansion STILL fire inside double quotes:
                // `"$(cmd)"`, "`cmd`", and `"${x}"` all execute or expand. The unquoted branch's
                // metacharacter scan never runs in here, so these must be rejected explicitly or a
                // scoped grant is escaped through a quoted substitution (the first cut missed this).
                if (c == '`')
                {
                    return false;
                }
                if (c == '$' && i + 1 < commandLine.Length && commandLine[i + 1] is '(' or '{')
                {
                    return false;
                }
                if (c == '"')
                {
                    inDoubleQuote = false;
                }
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            // Unquoted metacharacters: ; & | ` $ < > ( ) \n \r \
            // A bare unquoted '$' is denied outright, not merely '$(' / '${'. Besides command
            // substitution and expansion, a bare '$' before a quote opens ANSI-C quoting ($'...'),
            // whose backslash-escaped quote (\') is a NON-terminating escape in bash but closes this
            // scanner's escape-free single-quote branch one character early. A later stray ' rebalances
            // the parity, hiding a live ';' inside a region the scanner still believes is quoted -- a
            // confirmed escape from a scoped grant: `git $'\''; rm -rf / #'` executes rm outside `git *`.
            // Denying '$' outright also covers $VAR, ${...}, $((...)) and $[...]. A scoped command needs
            // none of these unquoted; a literal dollar can be quoted ("$5") to pass.
            if (c is ';' or '&' or '|' or '`' or '$' or '<' or '>' or '(' or ')' or '\n' or '\r' or '\\')
            {
                return false;
            }
        }

        if (inSingleQuote || inDoubleQuote)
        {
            return false;
        }

        string trimmed = commandLine.Trim();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (pattern.EndsWith('*'))
            {
                string prefix = pattern[..^1];
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else
            {
                if (trimmed.Equals(pattern, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>DenyAlways</c> rung's standing-"never" check (0022, M-Phase-6 #390) — called by
    /// <c>AgyHookCheckCommand</c> to refuse a <c>run_command</c> whose command line matches a persisted
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> entry, deny-beats-allow. (claude enforces
    /// the same rung through <c>--disallowedTools</c> rather than this matcher.) Returns
    /// <see langword="true"/> iff <paramref name="commandLine"/> matches at least one pattern in
    /// <paramref name="deniedPatterns"/>. Same glob shape and the same metacharacter fail-closed rules as
    /// <see cref="IsAllowed"/> (deliberately reuses it): a command this scanner cannot parse safely is
    /// not matched against the deny list either, since whatever else grants it (categorical
    /// <see cref="PermissionGrant.RunShellCommands"/> or an allow pattern) already refuses it on the
    /// same unparseable-metacharacter grounds.
    /// </summary>
    public static bool IsDenied(string? commandLine, IReadOnlyList<string>? deniedPatterns) =>
        IsAllowed(commandLine, deniedPatterns);

    /// <summary>
    /// Derives a command's family (its first whitespace-delimited token, e.g. <c>"rm"</c> out of
    /// <c>"rm -rf build/"</c>) for scoping a new <see cref="PermissionGrant.ShellCommandPatterns"/> or
    /// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> entry (0022's <c>AllowCommandInRoom</c>
    /// / <c>DenyAlways</c> rungs, M-Phase-6 #390). Returns <see langword="null"/> — never a guess — when
    /// <paramref name="commandLine"/> is empty or its first token opens with a shell metacharacter this
    /// matcher already treats as unsafe to reason about (<see cref="IsAllowed"/>'s own set): persisting
    /// a pattern derived from an unparseable head would scope a standing permission to something this same
    /// matcher could not evaluate consistently later.
    /// </summary>
    public static string? ExtractCommandFamily(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var trimmed = commandLine.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && Array.IndexOf(MetaCharacters, trimmed[end]) < 0)
        {
            end++;
        }

        return end == 0 ? null : trimmed[..end];
    }

    private static readonly char[] MetaCharacters =
        [';', '&', '|', '`', '$', '<', '>', '(', ')', '\n', '\r', '\\', '\'', '"'];

    /// <summary>
    /// The overall result of <see cref="EvaluateChainedCommand"/> — <see cref="Allowed"/> when every
    /// chained segment independently matched an allowed pattern and none matched a denied one,
    /// <see cref="DeniedSegment"/> when a specific segment failed that test, and
    /// <see cref="Unparseable"/> when the scanner would not trust its own segment boundaries at all.
    /// </summary>
    public enum ScopedShellVerdict
    {
        Allowed,
        DeniedSegment,
        Unparseable,
    }

    /// <param name="Verdict">The overall decision.</param>
    /// <param name="Segment">
    /// The offending segment, for <see cref="ScopedShellVerdict.DeniedSegment"/> only. An
    /// <see cref="ScopedShellVerdict.Unparseable"/> command has no segment boundary this scanner
    /// trusts, and an <see cref="ScopedShellVerdict.Allowed"/> command has nothing to name.
    /// </param>
    /// <param name="Reason">A denial reason a person can act on; <see langword="null"/> when allowed.</param>
    public readonly record struct ScopedShellResult(ScopedShellVerdict Verdict, string? Segment, string? Reason)
    {
        public bool IsAllowed => Verdict == ScopedShellVerdict.Allowed;
    }

    /// <summary>
    /// The hook-side second enforcement layer for a scoped shell grant (#1459, #1461's measured
    /// hole). <see cref="IsAllowed"/> matches <paramref name="commandLine"/> as one whole string — the
    /// same thing claude's own <c>Bash(pattern)</c> matching does — so an unlisted command riding a
    /// <c>;</c>/<c>&amp;&amp;</c>/<c>||</c>/<c>|</c> chain after an allowed prefix matches too (`git
    /// diff; echo escaped` and `git diff | grep baseline` both ran, unblocked, under a
    /// <c>Bash(git diff*)</c> grant — see <c>docs/vendor-capabilities.md</c>'s #1461 subsection).
    /// This method splits the command at top-level (unquoted) chain boundaries first and requires
    /// EVERY resulting segment to independently satisfy the grant: match at least one allowed
    /// pattern, and match no denied one.
    /// </summary>
    /// <remarks>
    /// Fails closed to <see cref="ScopedShellVerdict.Unparseable"/> on anything this scanner will not
    /// guess a boundary for — backticks, <c>$(...)</c>/<c>${...}</c>/a bare <c>$</c>, <c>&lt;</c>/
    /// <c>&gt;</c> redirection, subshell parens, an embedded newline, or an unterminated quote —
    /// rather than segment around it and risk a hidden command riding through. Once split, each
    /// segment is itself checked through <see cref="IsAllowed"/>'s own quote-tracking scan, so a
    /// segment that somehow still carries a bare metacharacter denies through the same path
    /// <see cref="IsAllowed"/> already has.
    /// </remarks>
    /// <param name="commandLine">The full shell command line as claude's <c>Bash</c> tool received it.</param>
    /// <param name="allowedPatterns">The grant's allowed patterns. Never call this with an empty/null list — that is the unscoped-shell case, handled by the caller before reaching here.</param>
    /// <param name="deniedPatterns">The grant's standing-deny patterns, or empty/null when none apply.</param>
    public static ScopedShellResult EvaluateChainedCommand(
        string? commandLine, IReadOnlyList<string>? allowedPatterns, IReadOnlyList<string>? deniedPatterns)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return new ScopedShellResult(
                ScopedShellVerdict.Unparseable, null, "unparseable under scoped grant (empty command line)");
        }

        if (!TrySegmentChainedCommand(commandLine, out var segments, out var unparseableReason))
        {
            return new ScopedShellResult(ScopedShellVerdict.Unparseable, null, unparseableReason!);
        }

        foreach (var segment in segments)
        {
            if (deniedPatterns is { Count: > 0 } && IsAllowed(segment, deniedPatterns))
            {
                return new ScopedShellResult(
                    ScopedShellVerdict.DeniedSegment, segment,
                    $"segment '{segment}' matches this session's standing deny list");
            }

            if (!IsAllowed(segment, allowedPatterns))
            {
                return new ScopedShellResult(
                    ScopedShellVerdict.DeniedSegment, segment,
                    $"segment '{segment}' does not match any pattern this session's grant allows");
            }
        }

        return new ScopedShellResult(ScopedShellVerdict.Allowed, null, null);
    }

    /// <summary>
    /// Splits <paramref name="commandLine"/> at top-level (unquoted) <c>;</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>|</c> and a lone <c>&amp;</c> boundaries. Returns <see langword="false"/> the
    /// moment it meets a character it will not trust a boundary decision around; see
    /// <see cref="EvaluateChainedCommand"/>'s own remarks for the exact set and why.
    /// </summary>
    private static bool TrySegmentChainedCommand(
        string commandLine, out IReadOnlyList<string> segments, out string? unparseableReason)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        segments = Array.Empty<string>();

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (inSingleQuote)
            {
                current.Append(c);
                if (c == '\'')
                {
                    inSingleQuote = false;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < commandLine.Length)
                {
                    current.Append(c);
                    current.Append(commandLine[++i]);
                    continue;
                }

                if (c == '`' || (c == '$' && i + 1 < commandLine.Length && commandLine[i + 1] is '(' or '{'))
                {
                    unparseableReason =
                        "unparseable under scoped grant (command substitution inside a quoted segment)";
                    return false;
                }

                current.Append(c);
                if (c == '"')
                {
                    inDoubleQuote = false;
                }
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingleQuote = true;
                    current.Append(c);
                    continue;
                case '"':
                    inDoubleQuote = true;
                    current.Append(c);
                    continue;
                case '`' or '$' or '<' or '>' or '(' or ')' or '\\':
                    unparseableReason = $"unparseable under scoped grant (unsupported character '{c}')";
                    return false;
                case '\n' or '\r':
                    unparseableReason = "unparseable under scoped grant (embedded newline)";
                    return false;
                case ';':
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                case '&':
                    if (i + 1 < commandLine.Length && commandLine[i + 1] == '&')
                    {
                        i++;
                    }
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                case '|':
                    if (i + 1 < commandLine.Length && commandLine[i + 1] == '|')
                    {
                        i++;
                    }
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                default:
                    current.Append(c);
                    continue;
            }
        }

        if (inSingleQuote || inDoubleQuote)
        {
            unparseableReason = "unparseable under scoped grant (unterminated quote)";
            return false;
        }

        result.Add(current.ToString());
        var trimmed = result.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        if (trimmed.Count == 0)
        {
            unparseableReason = "unparseable under scoped grant (no command found)";
            return false;
        }

        segments = trimmed;
        unparseableReason = null;
        return true;
    }
}
