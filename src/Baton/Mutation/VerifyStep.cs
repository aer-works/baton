using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// The room-level entry point for the pre-turn verify step (#1882): run the allowlisted commands,
/// write <c>verify-results.md</c> and its sidecar into the room's artifacts directory, and hand back
/// the instrument rows the engine later copies onto <c>verdict.json</c>.
/// </summary>
public static class VerifyStep
{
    /// <param name="Results">One per command, in the order they ran.</param>
    /// <param name="Instruments">The narrowed rows <see cref="InjectInstrumentsAsync"/> writes onto a verdict.</param>
    /// <param name="ResultsFilePath">Where <c>verify-results.md</c> landed — the path the review prompt names.</param>
    public sealed record Outcome(
        IReadOnlyList<VerifyCommandResult> Results,
        IReadOnlyList<VerifyInstrument> Instruments,
        string ResultsFilePath,
        long TotalWallClockMs,
        long ResultsBytes);

    /// <summary>
    /// Runs <paramref name="commands"/> and records both artifacts. Called before the worker is
    /// dispatched, so the reviewer's very first read can be the results file.
    /// </summary>
    public static async Task<Outcome> RunAndRecordAsync(
        IReadOnlyList<VerifyStepCommand> commands,
        string workingDirectory,
        string artifactsRootPath,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        VerifyProcessLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var results = await VerifyStepRunner
            .RunAsync(commands, workingDirectory, timeout, cancellationToken, launcher)
            .ConfigureAwait(false);

        var markdown = VerifyStepReport.Render(results);
        var bytes = Encoding.UTF8.GetBytes(markdown);

        Directory.CreateDirectory(artifactsRootPath);
        var resultsPath = Path.Combine(artifactsRootPath, VerifyStepReport.ResultsFileName);
        // CancellationToken.None: the results are the whole product of work that has already been
        // spent, and a cancellation arriving between the last command exiting and this write would
        // otherwise discard minutes of build output that nothing will reproduce.
        await File.WriteAllBytesAsync(resultsPath, bytes, CancellationToken.None).ConfigureAwait(false);

        var instruments = results
            .Select(r => new VerifyInstrument(r.CommandLine, r.ExitCode, r.WallClockMs))
            .ToList();
        var totalMs = results.Sum(r => r.WallClockMs);

        var sidecar = new VerifyStepReport.Sidecar(totalMs, bytes.LongLength, instruments);
        await File.WriteAllTextAsync(
            Path.Combine(artifactsRootPath, VerifyStepReport.SidecarFileName),
            VerifyStepReport.SerializeSidecar(sidecar),
            CancellationToken.None).ConfigureAwait(false);

        return new Outcome(results, instruments, resultsPath, totalMs, bytes.LongLength);
    }

    /// <summary>
    /// Copies <paramref name="instruments"/> onto the worker-written <c>verdict.json</c> at
    /// <paramref name="verdictFilePath"/> (#1882). Three properties of this, all load-bearing:
    /// <list type="bullet">
    /// <item><description>
    /// It edits the parsed <see cref="JsonObject"/> in place rather than round-tripping through the
    /// <c>ReviewVerdict</c> record. That schema explicitly tolerates unknown extra fields at every
    /// level, and a record round-trip would silently delete every annotation the worker wrote.
    /// </description></item>
    /// <item><description>
    /// It OVERWRITES any <c>instruments</c> the model wrote itself. That is the point of the field —
    /// a reviewer must not be able to claim an instrument it did not have — so a model-authored value
    /// is replaced, never merged with or appended to.
    /// </description></item>
    /// <item><description>
    /// Every failure (absent file, unparseable JSON, a top-level array or scalar, an unwritable path)
    /// returns false rather than throwing. This runs after the review has already completed and its
    /// outputs are already durable; losing the instruments annotation must not turn a finished review
    /// into a failed dispatch.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <returns>True when the file was rewritten with the instruments in place.</returns>
    public static async Task<bool> InjectInstrumentsAsync(
        string verdictFilePath, IReadOnlyList<VerifyInstrument> instruments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(verdictFilePath);
        ArgumentNullException.ThrowIfNull(instruments);

        try
        {
            if (!File.Exists(verdictFilePath))
            {
                return false;
            }

            var bytes = await File.ReadAllBytesAsync(verdictFilePath, cancellationToken).ConfigureAwait(false);

            // A UTF-8 BOM is a JsonException to System.Text.Json, and these bytes were written by a
            // vendor CLI worker -- exactly the population that cannot be assumed to have written them
            // without one. Skipping it here keeps a real verdict from losing its instruments over a
            // three-byte preamble.
            var json = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes.AsSpan(3)
                : bytes.AsSpan();

            if (JsonNode.Parse(json) is not JsonObject verdict)
            {
                return false;
            }

            verdict["instruments"] = JsonSerializer.SerializeToNode(instruments);
            await File.WriteAllTextAsync(
                verdictFilePath, verdict.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
