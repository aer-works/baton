using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Outcomes;

/// <summary>
/// #1594's safety net: when a worker finished naturally, exit 0, but a declared output is simply
/// absent — the measured agy failure mode, worker completes the real work and never writes its
/// contract file — this calls <see cref="IWorkerResponseParser"/> (see that interface's own doc
/// comment for what it recovers and why) and writes the result in the output's place, so a genuinely
/// completed lane does not settle <c>Failed</c> with <c>outputs: []</c> for a report the worker
/// already gave.
/// </summary>
/// <remarks>
/// Deliberately narrow. Only fires when <b>every</b> unsatisfied output is
/// <see cref="UnsatisfiedOutputReason.Missing"/> — a present-but-wrong file
/// (<see cref="UnsatisfiedOutputReason.NotJson"/>, <see cref="UnsatisfiedOutputReason.SchemaViolation"/>,
/// a failed <see cref="UnsatisfiedOutputReason.ConditionFailed"/>) is a different failure than "never
/// written", and materializing over a real, wrong output would be exactly the clobbering this method
/// must not do — AND every missing name looks like prose (see <see cref="IsProseSafeName"/>). Nothing
/// here is silent: <see cref="OutcomeClassifier"/> logs a loud line for every materialization it
/// accepts, and a successful one is carried onto the durable <see cref="Domain.FlowEvent.ExecutionSucceeded"/>
/// record — the room fact — so "the worker wrote its report" stays falsifiable from the room record alone.
/// </remarks>
public static class OutputMaterializer
{
    /// <summary>
    /// Prepended to every materialized file, so a reader who opens it — or greps the room's outputs —
    /// can tell at a glance this is not the worker's own write.
    /// </summary>
    public const string MaterializedHeader =
        "<!-- materialized from the worker's result envelope by baton (#1594); the worker did not write this file itself -->";

    /// <summary>
    /// Extensions a free-text response can honestly stand in for. Deliberately a narrow allowlist
    /// rather than "anything without a declared <see cref="OutputSchema"/>/<see cref="OutputCondition"/>"
    /// (second-reader review, #1594): several shipped roles declare a structured-by-*extension*, not
    /// by-schema, output — <c>orchestrate</c>'s <c>turn-actions.json</c> and <c>janitor</c>'s
    /// <c>branch.diff</c> both carry <c>Schema: None</c> in <c>WorkerRoles.json</c> today, yet neither
    /// can honestly hold prose. A bare name (no extension) is treated as prose-safe.
    /// </summary>
    private static readonly HashSet<string> ProseSafeExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt" };

    private static bool IsProseSafeName(string outputName)
    {
        var extension = Path.GetExtension(outputName);
        return extension.Length == 0 || ProseSafeExtensions.Contains(extension);
    }

    /// <summary>
    /// Attempts to recover every missing declared output in <paramref name="validation"/> from the
    /// execution's captured <c>.stdout.log</c>. Returns the output names actually written; empty when
    /// nothing qualified (no parser, a mixed failure population, a declared <see cref="OutputSchema"/>
    /// or <see cref="OutputCondition"/> in the missing set, a missing name that isn't prose-safe, no
    /// stream log, the vendor's terminal line carried no usable response, or every write failed) —
    /// every empty return leaves the output directory exactly as it was.
    /// </summary>
    public static IReadOnlyList<string> TryMaterializeMissingOutputs(
        ContractValidationResult validation,
        WorkerContract contract,
        string outputDirectory,
        IWorkerResponseParser? responseParser)
    {
        if (validation.IsSatisfied || responseParser is null)
        {
            return [];
        }

        // Mixed failure population (some Missing, some NotJson/ConditionFailed/SchemaViolation/
        // MalformedCondition): a present-but-wrong output is a different, real failure this method
        // must never paper over, so nothing is materialized rather than guessing which half to fix.
        if (validation.UnsatisfiedOutputs.Any(u => u.Reason != UnsatisfiedOutputReason.Missing))
        {
            return [];
        }

        // A missing output declaring a Schema or a Condition, or whose own name isn't prose-safe, can
        // never be honestly satisfied by prose -- writing the response there is guaranteed loss, not a
        // recovery. (Second-reader review, #1594: a multi-output contract, e.g. `review`'s report.md +
        // verdict.json, would otherwise leave a bogus verdict.json on disk with no room fact recording
        // it, since the overall classification stays Failed and only a Succeeded verdict carries
        // MaterializedOutputs.) If even one missing output can never be satisfied this way, satisfying
        // the rest cannot flip the verdict either, so nothing is written at all rather than a partial,
        // unrecorded write. GroupBy/First rather than ToDictionary: a duplicate declared output name
        // is a pre-existing authoring defect nothing upstream rejects (ContractValidator.cs's own
        // FormatException precedent), and this method must not be the one that turns it into a crash
        // between the worker exiting and the outcome being appended.
        var outputByName = contract.ProducedOutputs
            .GroupBy(o => o.Name)
            .ToDictionary(g => g.Key, g => g.First());
        if (validation.UnsatisfiedOutputs.Any(u =>
                !IsProseSafeName(u.Name)
                || (outputByName.TryGetValue(u.Name, out var declared)
                    && (declared.Schema != OutputSchema.None || declared.Condition is not null))))
        {
            return [];
        }

        var response = TryReadFinalResponse(outputDirectory, responseParser);
        if (string.IsNullOrWhiteSpace(response))
        {
            return [];
        }

        var content = MaterializedHeader + "\n\n" + response;
        var written = new List<string>();
        foreach (var unsatisfied in validation.UnsatisfiedOutputs)
        {
            var path = Path.Combine(outputDirectory, unsatisfied.Name);
            try
            {
                File.WriteAllText(path, content);
                written.Add(unsatisfied.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort, same posture as ExecutionStreamLogger's own chunk writer: a transient
                // sharing failure here must not corrupt the classification that follows. Whatever
                // names ARE in `written` still had a real file land; the rest fall through to today's
                // Missing failure, exactly as if materialization had never been attempted for them.
                // Logged, never swallowed silently (CLAUDE.md's error-handling rule): if every write
                // in this loop fails this way, `written` comes back empty and OutcomeClassifier prints
                // no loud line -- this is the only trace that baton tried at all.
                Console.Error.WriteLine(
                    $"Warning: #1594 materialization failed to write '{unsatisfied.Name}' in '{outputDirectory}': {ex.Message}.");
            }
        }

        return written;
    }

    /// <summary>
    /// Reads the execution's own <c>.stdout.log</c> (<see cref="ExecutionStreamLogger"/>) — the same,
    /// full-fidelity, per-execution capture <see cref="Status.ExecutionUsageProjector"/> already reads
    /// for token attribution — and hands the last non-blank line to <paramref name="responseParser"/>.
    /// Never <see cref="CoreDispatchResult.StdoutTail"/>: that field is capped at
    /// <see cref="CoreDispatcher.MaxRetainedStderrLength"/> (2000 characters), far short of a worker's
    /// real final report.
    /// </summary>
    private static string? TryReadFinalResponse(string outputDirectory, IWorkerResponseParser responseParser)
    {
        var stdoutPath = Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName);
        if (!File.Exists(stdoutPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(stdoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return responseParser.TryParseFinalResponse(line, out var response) ? response : null;
        }

        return null;
    }
}
