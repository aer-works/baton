using System.Text.Json;

namespace Aer.Adapters;

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
}
