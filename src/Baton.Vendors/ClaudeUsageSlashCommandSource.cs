using System.Globalization;
using System.Text.RegularExpressions;
using Baton.Core;

namespace Baton.Vendors;

/// <summary>
/// Harvests claude's headless <c>/usage</c> report (issue #1391; measured shape:
/// docs/vendor-capabilities.md "Usage, cost and quota" §"claude — everything needed, headlessly").
/// Spawns <c>claude -p "/usage" --output-format text</c> directly through <see cref="BatonTask"/> —
/// the same shell-less launcher <c>ClaudeWorkerAdapter</c>'s dispatch target bottoms out into
/// (<c>src/Baton/Core/Internal/BatonProcessRunner.cs</c>) — never through
/// <c>ClaudeWorkerAdapter.Resolve</c> itself, which builds a full gated worker dispatch (hook
/// liveness probe, settings/MCP config files) this read-only, no-tool-execution status query has no
/// use for. Registered in <c>tests/Baton.Architecture.Tests/VendorSpawnGateTests.cs</c>'s
/// <c>ApprovedSpawnSites</c> alongside the same rationale <c>AgyWorkerAdapter</c>'s own read-only
/// registry queries already carry: no tool execution is possible from a slash command's own output,
/// so decision 0029's mandatory <c>PreToolUse</c> gate has nothing to guard here.
/// </summary>
/// <remarks>
/// <b>A second claude source is a follow-up, not this slice.</b> The account-wide usage endpoint
/// (rather than this machine-local CLI report) is a distinct future <see cref="IVendorUsageSource"/>
/// implementation behind the same seam — not built here, and it must not read a credential file to
/// get there (Architecture Rule 4).
/// </remarks>
public sealed class ClaudeUsageSlashCommandSource : IVendorUsageSource
{
    public string Vendor => "claude";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    private static readonly string[] DefaultArgs = ["-p", "/usage", "--output-format", "text"];

    private readonly string _program;
    private readonly string[] _args;

    public ClaudeUsageSlashCommandSource()
        : this("claude", DefaultArgs)
    {
    }

    /// <summary>
    /// Test-only seam (Baton.Vendors.Tests, via <c>InternalsVisibleTo</c>): substitutes the program
    /// and arguments so a test can drive a REAL process to a chosen exit code and stdout, rather than
    /// faking away the exit-code read that #1869's review found missing.
    /// </summary>
    internal ClaudeUsageSlashCommandSource(string program, IReadOnlyList<string> args)
    {
        _program = program;
        _args = [.. args];
    }

    public async Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        using var task = new BatonTask(_program, _args)
            .WithCaptureOutput(true)
            .WithTimeout(CommandTimeout);

        // Spawn failure, timeout, cancellation, a non-zero exit, or empty stdout all land here as
        // null -- no snapshot, never a fabricated one (VendorUsageCommandRun's own doc comment).
        var stdout = await VendorUsageCommandRun.CaptureStdoutOrNullAsync(task, Vendor, cancellationToken)
            .ConfigureAwait(false);

        return stdout is null ? null : Parse(stdout, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Pure parse of claude's <c>/usage</c> stdout — no process, no clock dependency beyond the
    /// caller-supplied <paramref name="harvestedAt"/>, so every fixture in
    /// <c>tests/Baton.Vendors.Tests</c> exercises this directly rather than a process double.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Window lines.</b> Matches every line shaped <c>"Current &lt;name&gt;: N% used"</c>, with an
    /// optional trailing <c>" · resets &lt;text&gt;"</c> clause — measured live against claude 2.1.258
    /// on 2026-09-04 (this issue's own step 5 live check) to carry the clause on <b>all three</b>
    /// windows, though docs/vendor-capabilities.md's earlier 2026-08-28 capture shows the
    /// week(Fable) line WITHOUT one (<c>"Current week (Fable): 0% used"</c>, no resets clause) — so
    /// the clause is optional per-line, not per-report. <see cref="VendorUsageWindow.Name"/> is the
    /// text between "Current " and the colon (<c>"session"</c>, <c>"week (all models)"</c>,
    /// <c>"week (Fable)"</c>), matching the vendor's own wording rather than AER inventing shorter
    /// names.
    /// </para>
    /// <para>
    /// <b>Reset instant.</b> claude's own format has no year, 12-hour time with optional minutes
    /// (#1898) and no space before am/pm, and an IANA zone id in parens
    /// (<c>"Sep 7, 5:59am (America/New_York)"</c>) — not ISO 8601. <see cref="TryParseResetInstant"/> resolves it against <paramref name="harvestedAt"/>'s
    /// year (rolling to next year when the parsed instant would otherwise land more than three days in
    /// the past, since every observed reset is near-future); a failed parse leaves
    /// <see cref="VendorUsageWindow.ResetsAt"/> null while <see cref="VendorUsageWindow.RawLine"/>
    /// still carries the vendor's own text — "unparsed → unknown, never a number" applies to the
    /// instant, not to the whole window.
    /// </para>
    /// <para>
    /// <b>Caveat.</b> The first line starting with <c>"Approximate,"</c> — claude's own machine-local
    /// disclaimer (docs/vendor-capabilities.md: <i>"Approximate, based on local sessions on this
    /// machine — does not include other devices or claude.ai."</i>) — kept verbatim, including any
    /// trailing sentence the live 2026-09-04 capture carries beyond that quoted text
    /// (<i>"Behaviors are independent characteristics, not a breakdown."</i>) that the doc's
    /// 2026-08-28 quote predates.
    /// </para>
    /// <para>
    /// <b>Everything else is ignored</b> — the "Last 24h"/"Last 7d" behavioural-attribution breakdown
    /// (subagent/context/parallelism percentages) is not part of the settled #1391 design (three
    /// windows plus caveat only) and is deliberately not surfaced as a window.
    /// </para>
    /// </remarks>
    public static VendorUsageSnapshot Parse(string stdout, DateTimeOffset harvestedAt)
    {
        List<VendorUsageWindow> windows = [];
        string? caveat = null;

        foreach (var rawLine in (stdout ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var windowMatch = WindowLinePattern.Match(line);
            if (windowMatch.Success)
            {
                var name = windowMatch.Groups["name"].Value.Trim();
                var percentUsed = int.Parse(windowMatch.Groups["pct"].Value, CultureInfo.InvariantCulture);
                DateTimeOffset? resetsAt = null;
                if (windowMatch.Groups["reset"].Success &&
                    TryParseResetInstant(windowMatch.Groups["reset"].Value.Trim(), harvestedAt, out var parsed))
                {
                    resetsAt = parsed;
                }

                windows.Add(new VendorUsageWindow(name, percentUsed, resetsAt, line));
                continue;
            }

            if (caveat is null && line.StartsWith("Approximate,", StringComparison.Ordinal))
            {
                caveat = line;
            }
        }

        return new VendorUsageSnapshot("claude", harvestedAt, caveat, windows);
    }

    private static readonly Regex WindowLinePattern = new(
        @"^Current (?<name>[^:]+):\s*(?<pct>\d+)%\s*used(?:\s*·\s*resets\s*(?<reset>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResetInstantPattern = new(
        @"^(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2}),\s*(?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?<ampm>am|pm)\s*\((?<tz>[^)]+)\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> MonthAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jan"] = 1,
        ["Feb"] = 2,
        ["Mar"] = 3,
        ["Apr"] = 4,
        ["May"] = 5,
        ["Jun"] = 6,
        ["Jul"] = 7,
        ["Aug"] = 8,
        ["Sep"] = 9,
        ["Oct"] = 10,
        ["Nov"] = 11,
        ["Dec"] = 12,
    };

    private static bool TryParseResetInstant(string text, DateTimeOffset harvestedAt, out DateTimeOffset resetsAt)
    {
        resetsAt = default;
        var match = ResetInstantPattern.Match(text);
        if (!match.Success || !MonthAbbreviations.TryGetValue(match.Groups["month"].Value, out var month))
        {
            return false;
        }

        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture) % 12;
        if (string.Equals(match.Groups["ampm"].Value, "pm", StringComparison.OrdinalIgnoreCase))
        {
            hour += 12;
        }

        var minute = match.Groups["minute"].Value.Length == 0
            ? 0
            : int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["tz"].Value);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }

        // No year in the vendor's own text -- resolve against harvestedAt's year, rolling forward one
        // year if that lands more than three days in the past (every observed reset instant is
        // near-future; a hit more than three days behind the harvest means the current year already
        // rolled past it).
        for (var yearOffset = 0; yearOffset < 2; yearOffset++)
        {
            DateTime naive;
            try
            {
                naive = new DateTime(harvestedAt.Year + yearOffset, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            DateTime utc;
            try
            {
                utc = TimeZoneInfo.ConvertTimeToUtc(naive, tz);
            }
            catch (ArgumentException)
            {
                return false;
            }

            var candidate = new DateTimeOffset(utc, TimeSpan.Zero);
            if (candidate >= harvestedAt.AddDays(-3))
            {
                resetsAt = candidate;
                return true;
            }
        }

        return false;
    }
}
