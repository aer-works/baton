using System.Linq;
using System.Text.Json;

namespace Baton.Vendors;


/// <summary>
/// Evaluates a shell command line against a pattern allowlist using claude-compatible
/// <c>Bash(pattern)</c> glob semantics, enforcing strict shell metacharacter rejection (#659)
/// and word-boundary matching for trailing-wildcard patterns (#1679).
/// <para>
/// <b>A trailing-<c>*</c> pattern <c>P*</c> matches a command line in exactly three cases</b> — the
/// full accepting set, not a summary of it. #1683's review found the previous "iff … followed by
/// whitespace" wording false in both copies of this rule (here and spec/baton.md §9): it omitted the
/// two branches below it, so a reader who trusted it reasoned wrongly about what a pattern admits,
/// which is the very class of defect #1679 is:
/// <list type="number">
/// <item>the trimmed line <b>equals</b> <c>P</c> (with <c>P</c>'s own trailing whitespace trimmed
/// when it has any);</item>
/// <item>the line starts with <c>P</c> and the next character is <b>whitespace</b> — the word
/// boundary (<c>git diff*</c> matches <c>git diff --stat</c>, never <c>git difftool</c> or
/// <c>git diff-index</c>; <c>git merge*</c> never matches <c>git merge-base</c>);</item>
/// <item>the line starts with <c>P</c>, <c>P</c>'s last space-delimited token is <b>flag-shaped</b>
/// (starts with <c>-</c>), and the next character is anything at all — the attached-argument branch
/// (<c>git grep -O*</c> matches <c>git grep -Ocalc</c>, <c>git grep --open-files-in-pager*</c>
/// matches <c>…-pager=calc</c>).</item>
/// </list>
/// Case 3 is what makes <c>=</c> accept. Before #1683 the <c>=</c> accept sat <em>above</em> the
/// flag-shape test and applied to every non-whitespace-terminated prefix, so <c>git log*</c> matched
/// <c>git log=x</c> — a widening nothing documented and nothing gated. It is now inside case 3.
/// </para>
/// <para>
/// <b>Prefix matching is anchored at the start of the line, so it cannot bound an option that can
/// move.</b> A deny pattern only ever catches the spelling and position it was written in, and
/// <c>git</c> accepts neither constraint (short-flag clustering <c>-nOcalc</c>, reordering, unambiguous
/// long-option abbreviation on any <c>parse-options</c> subcommand, doubled spaces). Bounding an
/// <em>option</em> therefore needs <see cref="IsDeniedByOptionToken"/>, not a deny pattern — see that
/// method (#1683 F1/F2).
/// </para>
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
                if (prefix.Length > 0 && char.IsWhiteSpace(prefix[^1]))
                {
                    if (trimmed.Equals(prefix.TrimEnd(), StringComparison.Ordinal) ||
                        trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else
                {
                    if (trimmed.Equals(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        if (trimmed.Length > prefix.Length)
                        {
                            char next = trimmed[prefix.Length];
                            if (char.IsWhiteSpace(next))
                            {
                                return true;
                            }

                            // Flag-driven prefixes (e.g. "git grep -O*" or "git grep --open-files-in-pager*")
                            // where the last whitespace-delimited token in the prefix starts with '-'
                            // match option arguments attached directly without whitespace -- both the
                            // bare-attached form (-Ocalc) and the '=' form (--open-files-in-pager=calc).
                            //
                            // #1683 F6: '=' used to accept ABOVE this test, ungated by flag shape, so
                            // every trailing-'*' pattern whose prefix did not end in whitespace also
                            // matched an '='-suffixed continuation -- `git log*` matched `git log=x`.
                            // Nothing in the current lists is exploitable through that, but it was an
                            // unstated widening on the branch a future allow pattern would trip over, so
                            // the accept now sits under the same flag-shape gate as the branch it
                            // belongs to. A non-flag prefix accepts on the word boundary alone.
                            var lastSpace = prefix.LastIndexOf(' ');
                            var lastToken = lastSpace >= 0 ? prefix[(lastSpace + 1)..] : prefix;
                            if (lastToken.StartsWith('-'))
                            {
                                return true;
                            }
                        }
                    }
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
    /// The <b>position-independent</b> half of the deny side (#1683 F2): returns <see langword="true"/>
    /// iff any whitespace-separated token of <paramref name="commandLine"/> starts with any entry in
    /// <paramref name="deniedOptionTokens"/> (<see cref="PermissionGrant.DeniedShellOptionTokens"/>).
    /// Entries are literal token <em>prefixes</em>, so <c>"--output"</c> catches <c>--output=C:/x</c>,
    /// the separated <c>--output C:/x</c>, and <c>--output-indicator-new=x</c> alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a deny pattern cannot do this job.</b> <see cref="IsDenied"/> prefix-matches the line
    /// anchored at its start, so a deny entry binds one option in one position and one spelling.
    /// <c>git log --output=&lt;file&gt; --format=format:&lt;bytes&gt;</c> is an arbitrary file write
    /// admitted by the <c>review</c> role's own <c>git log*</c> allow pattern — no metacharacter, no
    /// redirection, so #659's scan never sees it — and adding <c>git log --output*</c> to the deny list
    /// would be walked past by reordering the option, doubling a space, or (on any <c>parse-options</c>
    /// subcommand) abbreviating it. Matching every token instead binds the option wherever it sits.
    /// </para>
    /// <para>
    /// <b>It over-matches, deliberately, in the fail-closed direction.</b> A denied prefix appearing as
    /// a token of a quoted argument (<c>git log --format="x --output=y"</c> splits to a token starting
    /// <c>--output</c>) denies, and so does a read-only sibling option sharing the prefix
    /// (<c>--output-indicator-new</c>). Both cost a reviewer a formatting flag; the alternative — a full
    /// argv parse per vendor subcommand — is the sort of thing that is wrong quietly.
    /// </para>
    /// <para>
    /// <b>Every quote character is removed from a token before the prefix test, not just a leading
    /// one.</b> A shell splits words BEFORE removing quotes, so a quote can sit anywhere inside an
    /// option name and the command still arrives at <c>git</c> as one unquoted word:
    /// <c>git log --outpu"t"=C:/x</c> and <c>git log -"-"output=C:/x</c> both reach it as
    /// <c>--output=C:/x</c>. Stripping only the leading quote left both matching nothing — the same
    /// "walked past by another spelling" defect this method exists to fix, inside the fix (found by
    /// this PR's second reader). Removing them all is safe for exactly the reason the caller contract
    /// below states: the metacharacter scan has already run, so no substitution can be hiding in the
    /// quotes, and dropping them is precisely what the shell itself does. It does not widen the deny
    /// to a quoted VALUE — <c>git log --grep="--output"</c> normalizes to <c>--grep=--output</c>, which
    /// does not START with the entry and stays allowed.
    /// </para>
    /// <para>
    /// <b>Not expressible on <c>--disallowedTools</c> — this channel is hook-only, on both vendors.</b>
    /// claude's <c>Bash(pattern)</c> matching is against the whole command line and anchored
    /// (<c>docs/vendor-capabilities.md</c>'s #1461 subsection measured <c>Bash(git log*)</c> denying
    /// <c>git log</c>, and measured nothing about a mid-line token), so what could be written there is
    /// another positional pattern — the defect F1/F2 document, not the fix. Whether that flag can
    /// express a mid-line token deny at all is <b>unmeasured</b>, and this states the gap rather than
    /// asserting claude cannot. <c>ClaudeWorkerAdapter.BuildDisallowedTools</c> therefore emits nothing
    /// from this field, and both hooks enforce it themselves.
    /// </para>
    /// <para>
    /// Caller contract: run this <b>after</b> the deny/allow pattern pass, which is what has already
    /// applied <see cref="IsAllowed"/>'s metacharacter scan to the line. Deny wins over any allow.
    /// </para>
    /// </remarks>
    public static bool IsDeniedByOptionToken(string? commandLine, IReadOnlyList<string>? deniedOptionTokens)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || deniedOptionTokens is null ||
            deniedOptionTokens.Count == 0)
        {
            return false;
        }

        foreach (var rawToken in commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace("'", string.Empty, StringComparison.Ordinal);
            foreach (var deniedToken in deniedOptionTokens)
            {
                if (string.IsNullOrWhiteSpace(deniedToken))
                {
                    continue;
                }

                if (token.StartsWith(deniedToken, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

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
