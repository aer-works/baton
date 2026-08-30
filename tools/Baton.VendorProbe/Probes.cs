using System.Text.Json;
using System.Text.RegularExpressions;

namespace Baton.VendorProbe;

/// <summary>
/// The capability checks. Each one names the surfaces it consulted, so an incomplete probe is
/// visibly incomplete rather than silently reported as an absence.
/// </summary>
public static class Probes
{
    public static IReadOnlyList<Finding> RunAll(string vendor)
    {
        var version = Cli.Version(vendor);
        if (version is null)
        {
            return [Finding.Absent(
                "the CLI itself", vendor, [Surfaces.Help],
                $"'{vendor} --version' did not succeed — the CLI is not installed or not on PATH. "
                + "Every other row for this vendor is unknown, not absent.", null)];
        }

        // Established once and shared: what this CLI does with a flag it has never heard of. Without
        // it, an exit code means nothing, and reading an exit code as though it meant something is
        // how --permission-prompt-tool came to be recorded as absent.
        var baseline = FlagProbe.Baseline(vendor);

        return
        [
            PlanUsage(vendor, version),
            PerTurnCost(vendor, version),
            StructuredOutput(vendor, version),
            PermissionPromptTool(vendor, version, baseline),
            Effort(vendor, version),
            AddDir(vendor, version),
        ];
    }

    private static string Help(string vendor) => Cli.Invoke(vendor, ["--help"], TimeSpan.FromSeconds(45)).All;

    /// <summary>
    /// The row that was wrong twice. It is absent from <c>--help</c> and from the subcommand list on
    /// both vendors, and on <c>claude</c> it works perfectly as a slash command — which is why the
    /// slash surface is probed here rather than inferred from the other two.
    /// </summary>
    private static Finding PlanUsage(string vendor, string version)
    {
        const string cap = "plan usage & reset";
        string[] surfaces = [Surfaces.Help, Surfaces.Subcommands, Surfaces.SlashCommand];
        var help = Help(vendor);

        // Ask the CLI directly. No shell, so a leading slash survives intact.
        var run = Cli.Invoke(vendor, ["-p", "/usage"], TimeSpan.FromMinutes(2));
        var text = run.All;

        // A real usage report states a percentage against a window. Prose *about* usage does not —
        // and on one vendor the model answers conversationally, which must not read as a capability.
        var percent = Regex.Match(text, @"(\d{1,3})%\s*used", RegexOptions.IgnoreCase);
        var reset = Regex.Match(text, @"resets?\s+([^\r\n·]+)", RegexOptions.IgnoreCase);

        if (percent.Success)
        {
            var detail = $"`{vendor} -p \"/usage\"` reported a live percentage"
                + (reset.Success ? $" and a reset instant (\"{reset.Groups[1].Value.Trim()}\")" : ", but no reset instant")
                + ". Note the vendor's own caveat where present: the figure may be machine-local.";
            return Finding.Seen(cap, vendor, $"`/usage` — {percent.Groups[1].Value}% used"
                + (reset.Success ? ", with reset times" : ", no reset time"), surfaces, detail, version);
        }

        return Finding.Absent(cap, vendor, surfaces,
            $"`--help` carries no usage/quota flag, no such subcommand exists, and `{vendor} -p \"/usage\"` "
            + "produced no percentage — the model answered conversationally rather than the CLI reporting. "
            + $"Help mentioned 'usage' {Regex.Matches(help, "usage", RegexOptions.IgnoreCase).Count} time(s), all of them the synopsis line.",
            version);
    }

    private static bool IsAgy(string vendor) => string.Equals(vendor, "agy", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The stream-json invocation for THIS vendor. The two CLIs disagree on the grammar, and passing
    /// claude's argv to agy is what recorded agy's structured output as absent across three versions
    /// (#1088): agy's <c>-p</c> is <b>flag-value</b> (#491) — the prompt is the value of <c>-p</c>, so
    /// <c>["-p", "--output-format", …]</c> makes agy read <c>--output-format</c> as the prompt and
    /// stream-json never engages — and agy <b>rejects</b> claude's <c>--verbose</c> (exit 2, "flags
    /// provided but not defined: -verbose"). claude's <c>-p</c> is a boolean with a positional prompt
    /// and does take <c>--verbose</c>. Measured live 2026-08-10; the corrected agy form streams
    /// <c>{"event":"init"} → {"event":"step_update"} → {"event":"result",…,"usage":{…}}</c> on stdout.
    /// </summary>
    internal static string[] StreamJsonArgs(string vendor, string prompt) =>
        IsAgy(vendor)
            ? ["-p", prompt, "--output-format", "stream-json"]
            : ["-p", "--output-format", "stream-json", "--verbose", prompt];

    /// <summary>
    /// Whether stdout is a stream-json event stream, recognising <b>either</b> vendor's envelope: the
    /// first non-empty line is a JSON object carrying a <c>type</c> (claude) or <c>event</c> (agy)
    /// discriminator. Keying only on claude's <c>type</c> is the second half of the #1088 false
    /// negative — even a correctly-invoked agy stream read as "not structured" because its lines say
    /// <c>"event"</c>, not <c>"type"</c>. Structural (parses the line) rather than a substring, so a
    /// vendor merely mentioning the word "type" in prose cannot masquerade as a stream.
    /// </summary>
    internal static bool LooksLikeStreamJson(string stdout)
    {
        var firstLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (firstLine is null || !firstLine.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(firstLine);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && (doc.RootElement.TryGetProperty("type", out _) || doc.RootElement.TryGetProperty("event", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>API-equivalent cost per turn — the "what would this have cost on a key" number.</summary>
    private static Finding PerTurnCost(string vendor, string version)
    {
        const string cap = "per-turn cost";
        string[] surfaces = [Surfaces.StructuredOutput, Surfaces.Help];

        var run = Cli.Invoke(vendor, StreamJsonArgs(vendor, "Reply with exactly: ok"), TimeSpan.FromMinutes(2));
        var cost = Regex.Match(run.StdOut, @"""total_cost_usd""\s*:\s*([0-9.]+)");
        if (cost.Success)
        {
            var tokens = Regex.Matches(run.StdOut, @"""(input_tokens|output_tokens|cache_creation_input_tokens|cache_read_input_tokens)""\s*:\s*\d+")
                .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            return Finding.Seen(cap, vendor, "`total_cost_usd` in every result event", surfaces,
                $"Observed ${cost.Groups[1].Value} on a trivial turn, alongside {string.Join(", ", tokens)}. "
                + "The CLI computes the figure, so there is no price table to maintain and no drift to chase.",
                version);
        }

        // A stream-json run that carries token usage but no dollar figure is a real, distinct state —
        // 0023/#479 spend is shown against a subscription's own limits, never faked into dollars. Say
        // which of the two absences this is, so "none" is not misread as "produced nothing".
        var streamedUsage = LooksLikeStreamJson(run.StdOut) && run.StdOut.Contains("\"usage\"", StringComparison.Ordinal);
        return Finding.Absent(cap, vendor, surfaces,
            "No `total_cost_usd` in a `stream-json` run, and no cost flag in `--help`. "
            + (run.StdOut.Length == 0
                ? "The structured-output run produced nothing at all."
                : streamedUsage
                    ? "The run streamed a `result` event carrying per-turn **token** usage (the `usage` object), but no dollar cost field — token-denominated, not dollars."
                    : "The run produced output, but carried no cost field."),
            version);
    }

    private static Finding StructuredOutput(string vendor, string version)
    {
        const string cap = "structured output";
        string[] surfaces = [Surfaces.Help, Surfaces.StructuredOutput, Surfaces.LocalServer];

        var run = Cli.Invoke(vendor, StreamJsonArgs(vendor, "Reply with exactly: ok"), TimeSpan.FromMinutes(2));
        if (LooksLikeStreamJson(run.StdOut))
        {
            var flagShown = IsAgy(vendor) ? "`--output-format stream-json`" : "`--output-format stream-json --verbose`";
            return Finding.Seen(cap, vendor, flagShown, surfaces,
                $"Emitted {run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} JSON lines on a trivial turn.",
                version);
        }

        // stdout is not the only place structured events can live. `agy` starts a local RPC server
        // on every run and prints its ports to the log — a surface the first probe never looked at,
        // which is why this row read "not found" while a reachable gRPC endpoint was running.
        var server = LocalRpcServer(vendor);
        if (server is not null)
        {
            return Finding.Read(cap, vendor, "a local RPC server (ports in `--log-file`)", surfaces,
                $"No structured stdout: `--output-format stream-json` exited {run.ExitCode}. But the CLI starts a "
                + $"local server on every run — \"{server}\" — so this is **not found on stdout**, not an absence "
                + "of structured events. The service and method names are not yet enumerated; until they are, "
                + "treat this as the highest-value open probe rather than a settled negative.",
                version);
        }

        return Finding.Absent(cap, vendor, surfaces,
            "`--output-format stream-json` was rejected or produced non-JSON, and no local RPC server was "
            + $"announced in the log. Exit {run.ExitCode}. First stderr line: "
            + (run.StdErr.Split('\n').FirstOrDefault()?.Trim() is { Length: > 0 } e ? e : "(none)"),
            version);
    }

    /// <summary>
    /// Whether a run announces a local RPC server, read out of the CLI's own log file.
    /// </summary>
    private static string? LocalRpcServer(string vendor)
    {
        string? announcement = null;

        Cli.InScratch(dir =>
        {
            var log = Path.Combine(dir, "probe.log");
            Cli.Invoke(vendor, ["--log-file", log, "-p", "Reply with exactly: ok"], TimeSpan.FromMinutes(2));
            if (!File.Exists(log))
            {
                return;
            }

            var m = Regex.Match(
                File.ReadAllText(log),
                @"listening on random port at (\d+) for (\S+)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                announcement = m.Value;
            }
        });

        return announcement;
    }

    /// <summary>
    /// Load-bearing. If this is wrong the way <c>/usage</c> was wrong, decision 0015's mechanism
    /// inverted to a blocking MCP tool on a false premise.
    /// </summary>
    private static Finding PermissionPromptTool(string vendor, string version, FlagProbe.Behaviour baseline)
    {
        const string cap = "--permission-prompt-tool";
        const string flag = "--permission-prompt-tool";
        string[] surfaces = [Surfaces.Help, Surfaces.StdErr, Surfaces.ControlFlag, Surfaces.StructuredOutput];
        var help = Help(vendor);
        var documented = help.Contains(flag, StringComparison.OrdinalIgnoreCase);

        var provenance = documented
            ? "Documented in `--help`."
            : "**Undocumented in `--help`** — which is why help text alone was never enough.";

        var (parses, parseDetail) = FlagProbe.IsAccepted(vendor, baseline, flag, "noop", "-p", "hi");
        if (parses is not true)
        {
            return Finding.Absent(cap, vendor, parses is null ? [Surfaces.Help] : surfaces,
                $"{provenance} {parseDetail}", version);
        }

        // Parsing is not honouring, and the distinction is the whole reason this row was wrong
        // before. A prompt that triggers no tool call can never reach a permission decision, so it
        // cannot tell the two apart. Force a tool call and watch for the CLI naming the tool it
        // tried to consult.
        var honoured = Finding.Absent(cap, vendor, surfaces,
            $"{provenance} {parseDetail} But no permission consultation was observed on a turn that "
            + "does require one, so the flag parses without evidence that it is honoured.", version);

        Cli.InScratch(dir =>
        {
            var run = Cli.Invoke(
                vendor,
                [flag, ProbeToolName, "-p", "--output-format", "stream-json", "--verbose",
                 "Use the Write tool to create a file named x.txt containing BANANA in the current directory."],
                TimeSpan.FromMinutes(3),
                workingDirectory: dir);

            // The tell: the CLI names the tool it was told to consult, in its own words. A name we
            // invented cannot appear in the output unless the flag reached the permission path.
            var consulted = Regex.Match(
                run.All,
                @$"{Regex.Escape(ProbeToolName)}[^""]*?\(passed via {Regex.Escape(flag)}\)",
                RegexOptions.IgnoreCase);

            if (consulted.Success)
            {
                honoured = Finding.Seen(cap, vendor,
                    "`--permission-prompt-tool <mcp-tool>` — consulted for permission decisions", surfaces,
                    $"{provenance} Honoured, not merely parsed: on a turn requiring a Write permission the CLI "
                    + $"reported \"{consulted.Value}\", naming the tool it tried to consult. The flag therefore "
                    + "routes permission decisions to an **MCP tool**, which is the same mechanism 0015 already "
                    + "chose — but as the vendor's designated entry point, consulted for every decision, rather "
                    + $"than a tool the model must elect to call. Established with a name (`{ProbeToolName}`) that "
                    + "exists nowhere, so it could not have come from anywhere but the flag.",
                    version);
            }
        });

        return honoured;
    }

    /// <summary>
    /// A tool name that exists nowhere, so seeing it echoed back proves the flag reached the
    /// permission path rather than being parsed and discarded.
    /// </summary>
    private const string ProbeToolName = "aer_probe_no_such_tool";

    private static Finding Effort(string vendor, string version)
    {
        const string cap = "effort";
        string[] surfaces = [Surfaces.Help];
        var help = Help(vendor);

        // Take the option's whole entry, not its first line. Help output wraps, and `claude` puts the
        // accepted value list on the continuation line — a single-line match reported "no values
        // documented" while "(low, medium, high, xhigh, max)" sat one line below the match.
        var m = Regex.Match(help, @"--effort(?:[^\r\n]*)(?:\r?\n[ \t]{4,}[^\r\n]*)*", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return Finding.Absent(cap, vendor, surfaces, "No `--effort` in help text.", version);
        }

        var entry = Regex.Replace(m.Value, @"\s+", " ").Trim();
        // Both separators, because the vendors disagree: `claude` writes "(low, medium, high, xhigh,
        // max)" and `agy` writes "(low|medium|high)". A comma-only pattern read agy's documented set
        // as undocumented — the same one-surface mistake in miniature.
        var values = Regex.Match(entry, @"\(([a-z]+(?:\s*[,|]\s*[a-z]+)+)\)");

        return Finding.Read(cap, vendor,
            values.Success ? $"`--effort` — {values.Groups[1].Value}" : "`--effort` (no values documented)",
            surfaces,
            $"Read from help: \"{entry}\". "
            + (values.Success
                ? "Help names the accepted values, but naming is not behaviour: 0023 declines to assert a "
                  + "mapping until each value is shown to be accepted AND to behave distinctly."
                : "The option is documented without its values, so the accepted set is unknown, not empty."),
            version);
    }

    private static Finding AddDir(string vendor, string version)
    {
        const string cap = "extra directories";
        string[] surfaces = [Surfaces.Help];
        var help = Help(vendor);
        var m = Regex.Match(help, @"--add-dir[^\r\n]*", RegexOptions.IgnoreCase);
        return m.Success
            ? Finding.Read(cap, vendor, "`--add-dir`", surfaces,
                "Read from help. On `agy` this is load-bearing rather than optional: `-p` ignores the "
                + "process working directory entirely (#491), so the room's folder must be bound explicitly.",
                version)
            : Finding.Absent(cap, vendor, surfaces, "No `--add-dir` in help text.", version);
    }
}
