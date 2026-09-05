namespace Baton.Mutation;

/// <summary>
/// One allowlisted command the pre-turn verify step (#1882) will spawn: the operator's raw
/// <c>--verify-cmd</c> text, plus the argv it tokenizes to. The argv is what actually runs — through
/// the shell-less launcher, never <c>cmd /c</c>, which is the difference between this and
/// <see cref="VerifyCommandResolver"/>'s override arm (that one deliberately spawns the platform
/// shell, spec/baton.md §3). <see cref="CommandLine"/> is kept verbatim so the results file and
/// <c>verdict.json</c>'s <c>instruments</c> name what the operator asked for, not a re-quoted
/// reconstruction of it.
/// </summary>
public sealed record VerifyStepCommand(string CommandLine, IReadOnlyList<string> Argv);

/// <summary>
/// The parse-time gate on <c>--verify-cmd</c> (#1882, spec/baton.md §3): a fixed set of command
/// SHAPES, refused by name when they do not match. The step runs with no model in the loop and with
/// the review worktree as its cwd, so "read-only, deterministic, nothing that writes the tree or
/// calls a vendor CLI" has to be decided here rather than trusted from the brief — the same spirit as
/// the review role's own shell allowlist (<c>WorkerRoles.json</c>), which this deliberately does not
/// widen.
/// <para>
/// Three shapes only, from the operator's 2026-09-05 trigger ruling: <c>dotnet build*</c>,
/// <c>dotnet test*</c>, and <c>python &lt;script under tools/ or benchmarks/&gt;</c> carrying a
/// <c>--check*</c> or <c>--selftest*</c> flag. Anything else is refused, including anything carrying
/// a shell metacharacter — there is no shell here to interpret one, so its presence means the caller
/// expected a shell and would otherwise get a literal argument silently.
/// </para>
/// </summary>
public static class VerifyStepCommandParser
{
    /// <summary>
    /// Characters that only mean something to a shell. Refused rather than passed through as literal
    /// argv text: a caller writing <c>dotnet build &amp;&amp; dotnet test</c> means two commands, and
    /// silently handing <c>&amp;&amp;</c> to <c>dotnet build</c> as an argument would run neither of
    /// the things they asked for while still reporting an exit code.
    /// </summary>
    private static readonly char[] ShellMetacharacters = ['&', '|', ';', '<', '>', '^', '`', '\n', '\r'];

    /// <summary>The two directories a <c>python</c> verify script may live under — repo-relative, no escape.</summary>
    private static readonly string[] ScriptRoots = ["tools/", "benchmarks/"];

    /// <summary>
    /// True with a non-null <paramref name="command"/> when <paramref name="rawCommandLine"/> is an
    /// allowlisted shape; false with a one-sentence <paramref name="error"/> that NAMES the offending
    /// command, so a refusal identifies which of several repeated <c>--verify-cmd</c> flags was wrong.
    /// </summary>
    public static bool TryParse(string rawCommandLine, out VerifyStepCommand? command, out string? error)
    {
        command = null;

        var commandLine = (rawCommandLine ?? string.Empty).Trim();
        if (commandLine.Length == 0)
        {
            error = "'--verify-cmd' is blank — pass the command to run, e.g. --verify-cmd \"dotnet build -warnaserror\".";
            return false;
        }

        if (commandLine.IndexOfAny(ShellMetacharacters) >= 0)
        {
            error = $"'--verify-cmd {commandLine}' contains a shell metacharacter. The verify step spawns "
                + "each command directly (no cmd /c), so nothing would interpret it — pass one command per "
                + "--verify-cmd flag instead.";
            return false;
        }

        if (!TryTokenize(commandLine, out var argv, out var tokenizeError))
        {
            error = $"'--verify-cmd {commandLine}' could not be read as a command: {tokenizeError}";
            return false;
        }

        if (!IsAllowedShape(argv))
        {
            error = $"'--verify-cmd {commandLine}' is not an allowlisted verify command shape. Allowed: "
                + "'dotnet build ...', 'dotnet test ...', or 'python <script under tools/ or benchmarks/> "
                + "--check.../--selftest...'.";
            return false;
        }

        command = new VerifyStepCommand(commandLine, argv);
        error = null;
        return true;
    }

    /// <summary>
    /// The shape check itself, over the already-tokenized argv — never over the raw string, so a
    /// quoted argument cannot smuggle a different program past a prefix match on the text.
    /// </summary>
    private static bool IsAllowedShape(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2)
        {
            return false;
        }

        var program = argv[0];
        if (IsProgram(program, "dotnet"))
        {
            return string.Equals(argv[1], "build", StringComparison.Ordinal)
                || string.Equals(argv[1], "test", StringComparison.Ordinal);
        }

        if (IsProgram(program, "python"))
        {
            return argv.Count >= 3
                && IsRepoScriptPath(argv[1])
                && argv.Skip(2).Any(arg =>
                    arg.StartsWith("--check", StringComparison.Ordinal)
                    || arg.StartsWith("--selftest", StringComparison.Ordinal));
        }

        return false;
    }

    /// <summary>
    /// Matches the bare program name only — <c>dotnet</c>, not <c>C:\tools\dotnet.exe</c> or
    /// <c>..\dotnet</c>. An absolute or relative path would let the workspace decide which binary the
    /// allowlisted name resolves to, which is the same hole <see cref="VerifyCommandResolver"/>'s
    /// scrubbed PATH closes for its own git spawns.
    /// </summary>
    private static bool IsProgram(string token, string expected) =>
        string.Equals(token, expected, StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, expected + ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A repo-relative path under <c>tools/</c> or <c>benchmarks/</c>, with no <c>..</c> segment, no
    /// rooted path, and no drive letter — the script has to be one this repo ships and review, not an
    /// arbitrary file the workspace happens to contain.
    /// </summary>
    private static bool IsRepoScriptPath(string token)
    {
        var normalized = token.Replace('\\', '/');
        if (normalized.Contains("../", StringComparison.Ordinal)
            || normalized.StartsWith('/')
            || normalized.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return ScriptRoots.Any(root => normalized.StartsWith(root, StringComparison.Ordinal))
            && normalized.EndsWith(".py", StringComparison.Ordinal);
    }

    /// <summary>
    /// Splits a command line into argv on whitespace, honouring double quotes so a path with a space
    /// stays one argument. Deliberately minimal: there is no shell here, the allowlist above rejects
    /// every metacharacter, and a fuller grammar would only create ways for the string the operator
    /// read and the argv that ran to differ.
    /// </summary>
    private static bool TryTokenize(string commandLine, out IReadOnlyList<string> argv, out string? error)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(c);
            started = true;
        }

        if (inQuotes)
        {
            argv = [];
            error = "it has an unclosed double quote.";
            return false;
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        if (tokens.Count == 0)
        {
            argv = [];
            error = "it has no words in it.";
            return false;
        }

        argv = tokens;
        error = null;
        return true;
    }
}
