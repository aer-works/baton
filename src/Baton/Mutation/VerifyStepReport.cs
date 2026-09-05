using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// What one command of the pre-turn verify step (#1882) did. <see cref="ExitCode"/> is null exactly
/// when <see cref="TimedOut"/> is true — see <see cref="VerifyInstrument.ExitCode"/> for why absence
/// rather than a sentinel. <see cref="Tail"/> is the last <see cref="VerifyStepReport.MaxTailLines"/> lines of the combined
/// stdout+stderr, never the whole log.
/// </summary>
public sealed record VerifyCommandResult(
    string CommandLine,
    int? ExitCode,
    long WallClockMs,
    bool TimedOut,
    string Tail);

/// <summary>
/// The durable side of the verify step (#1882): the <c>verify-results.md</c> the reviewer reads
/// first, and the <c>verify-step.json</c> sidecar the engine reads back for telemetry and for
/// <c>verdict.json</c>'s <c>instruments</c>.
/// <para>
/// <see cref="Render"/> is a pure function of already-collected results, split out from the runner on
/// purpose: it is the only thing whose exact bytes are a contract, and a renderer that also spawns
/// processes cannot be pinned byte-for-byte on a fixture.
/// </para>
/// </summary>
public static class VerifyStepReport
{
    /// <summary>The file the review prompt points at, in the room's artifacts directory.</summary>
    public const string ResultsFileName = "verify-results.md";

    /// <summary>The machine-readable sidecar beside it — never read by a worker, only by the engine.</summary>
    public const string SidecarFileName = "verify-step.json";

    /// <summary>
    /// The per-command tail bound, in lines (the issue's own figure). A bound in LINES rather than
    /// characters because the reader is a person scanning a build log's ending, and a character cut
    /// lands mid-line.
    /// </summary>
    public const int MaxTailLines = 200;

    private static readonly JsonSerializerOptions SidecarOptions = new() { WriteIndented = true };

    /// <summary>
    /// Keeps the last <see cref="MaxTailLines"/> lines of <paramref name="output"/>, trailing
    /// whitespace trimmed. Never fabricates content: empty output yields an empty string, which
    /// <see cref="Render"/> reports as "no output captured" rather than an empty code block.
    /// </summary>
    public static string BoundTail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n');
        var kept = lines.Length > MaxTailLines ? lines[^MaxTailLines..] : lines;
        return string.Join("\n", kept);
    }

    /// <summary>
    /// The exact bytes of <c>verify-results.md</c> — a pure function of <paramref name="results"/>,
    /// pinned byte-for-byte by <c>VerifyStepReportTests</c>. Newlines are <c>\n</c> regardless of
    /// platform so the pinned fixture means the same thing wherever it is read.
    /// </summary>
    public static string Render(IReadOnlyList<VerifyCommandResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sb = new StringBuilder();
        sb.Append("# Verify results\n\n");
        sb.Append(
            "The engine ran these commands before the reviewer's first turn, sequentially, with the review\n"
            + "worktree as their working directory and each wrapped in `python tools/buildlock.py`. No model\n"
            + "read their output: the exit codes and tails below are the engine's own capture.\n\n");
        sb.Append(
            "A non-zero exit does not abort the review — it is what the reviewer reads first. A command\n"
            + "reported as timed out has no exit code at all: its process tree was killed at the step's\n"
            + "wall-clock bound, which can mean a slow command OR a long wait for the shared build lock\n"
            + "(`tools/buildlock.py` waits up to `BATON_BUILDLOCK_TIMEOUT_S`, 1800s by default, before it\n"
            + "reports contention itself). Do not read a timeout as a failing build.\n\n");

        if (results.Count == 0)
        {
            sb.Append("No verify commands were requested for this review.\n");
            return sb.ToString();
        }

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            sb.Append($"## {i + 1}. `{result.CommandLine}`\n\n");
            sb.Append(result.TimedOut
                ? "- exit code: none (timed out; process tree killed)\n"
                : $"- exit code: {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}\n");
            sb.Append($"- wall clock: {result.WallClockMs.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms\n\n");

            if (result.Tail.Length == 0)
            {
                sb.Append("No output captured.\n\n");
                continue;
            }

            sb.Append($"Last {MaxTailLines} lines of combined stdout and stderr:\n\n");
            sb.Append("```text\n");
            sb.Append(result.Tail);
            sb.Append("\n```\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The engine-only sidecar: total wall clock, the rendered file's size in bytes, and one
    /// <see cref="VerifyInstrument"/> per command. Both telemetry figures
    /// (<c>ExecutionUsageView.VerifyStepMs</c>/<c>VerifyResultsBytes</c>) and <c>verdict.json</c>'s
    /// <c>instruments</c> are read back from here rather than recomputed, so the number the ledger
    /// carries and the number the verdict carries cannot drift apart.
    /// </summary>
    public sealed record Sidecar(
        [property: JsonPropertyName("totalWallClockMs")] long TotalWallClockMs,
        [property: JsonPropertyName("resultsBytes")] long ResultsBytes,
        [property: JsonPropertyName("commands")] IReadOnlyList<VerifyInstrument> Commands);

    public static string SerializeSidecar(Sidecar sidecar) => JsonSerializer.Serialize(sidecar, SidecarOptions);

    /// <summary>
    /// Reads the sidecar back, or null for anything short of a positive read — absent, unreadable, or
    /// malformed. A telemetry field is never worth failing a dispatch or a <c>baton status</c> over,
    /// so every failure here reads as "no verify step ran", which is also what is true for the vast
    /// majority of rooms.
    /// </summary>
    public static Sidecar? TryReadSidecar(string artifactsRootPath)
    {
        if (string.IsNullOrWhiteSpace(artifactsRootPath))
        {
            return null;
        }

        var path = Path.Combine(artifactsRootPath, SidecarFileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Sidecar>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
