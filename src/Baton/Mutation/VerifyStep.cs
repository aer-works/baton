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
    /// is replaced, never merged with or appended to. Unconditionally, which is why
    /// <paramref name="instruments"/> is nullable: null means NO verify step ran, and then the key is
    /// REMOVED rather than left as the model wrote it. Skipping the whole call in that case would have
    /// left a fabricated instruments array on disk and in <c>--notify</c>'s payload on the majority of
    /// review lanes — the ones dispatched without <c>--verify-cmd</c> — which is the exact claim this
    /// field exists to make impossible. Removal rather than an empty array so that "absent" keeps its
    /// one meaning (spec/baton.md §9): no step ran.
    /// </description></item>
    /// <item><description>
    /// Every failure (absent file, unparseable JSON, a top-level array or scalar, an unwritable path)
    /// returns false rather than throwing. This runs after the review has already completed and its
    /// outputs are already durable; losing the instruments annotation must not turn a finished review
    /// into a failed dispatch.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="instruments">
    /// The step's own rows, or null when no step ran — see the third bullet above for why null is a
    /// removal rather than a skip.
    /// </param>
    /// <returns>
    /// True when the verdict on disk ends up saying what the engine says: the instruments written, or
    /// the key gone. Also true when null was passed and the file never carried the key — nothing to
    /// remove is not a failure, and rewriting the file to prove it would be a write for no reader.
    /// </returns>
    public static async Task<bool> InjectInstrumentsAsync(
        string verdictFilePath, IReadOnlyList<VerifyInstrument>? instruments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(verdictFilePath);

        string? temporaryPath = null;
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

            if (instruments is null)
            {
                if (!verdict.Remove("instruments"))
                {
                    return true;
                }
            }
            else
            {
                verdict["instruments"] = JsonSerializer.SerializeToNode(instruments);
            }

            // Write-then-rename, the same atomic-write discipline TerminalSentinelWriter and
            // CancelRequestFile already use, and for the same reason: this rewrites a file that is
            // ALREADY on disk and already advertised as an execution output, so a concurrent reader
            // (`baton status`, fleet_status, `--notify`'s payload builder) can hit it at any moment.
            // A truncate-in-place would let one of them read half a verdict and report the whole
            // document as unparseable, which is a strictly worse outcome than the annotation this is
            // adding is worth.
            temporaryPath = verdictFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath, verdict.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, verdictFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-written sibling left behind by a failed rename would sit in the execution's own
            // output directory forever, so it is swept here. Best-effort by necessity: this is already
            // the failure path, and a failure to clean up must not become the exception this method
            // exists not to throw.
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Nothing further to do — the verdict itself is untouched, which is what matters.
                }
            }

            return false;
        }
    }
}
