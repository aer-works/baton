namespace Baton.Cli;

/// <summary>
/// #1882: makes every <c>verdict.json</c> a room produced say what the ENGINE knows about the
/// instruments — the verify step's rows when one ran, and the key removed when none did. See
/// <c>Mutation.VerifyStep.InjectInstrumentsAsync</c> for why the engine, and not the model, is the
/// writer.
/// <para>
/// #1895 made this shared rather than a private of <see cref="DispatchCommand"/>: <c>baton
/// redispatch</c> drives the same pump and produced the one review verdict whose <c>instruments</c>
/// was still whatever the model wrote. It calls <see cref="ApplyAsync"/> with a null outcome — no
/// verify step can run on that path — so the removal arm reaches a redispatched review too, which is
/// what stops a model-written array riding verbatim into a <c>--notify</c> payload
/// (<see cref="WatchFireService.BuildPayload"/> deserializes this file off disk with no schema in
/// between).
/// </para>
/// <para>
/// Called after the run rather than from inside the engine's contract check, for the same reason
/// <see cref="DispatchCommand.CopyPrimaryOutputToOverride"/> is: the execution-scoped artifact
/// directory is not known until the run has produced one. Every step with an execution is visited,
/// not just the first: a composed template's review phase is not necessarily its first step, and the
/// removal arm has to reach a model-written <c>instruments</c> wherever a verdict was written. A step
/// whose execution wrote no <c>verdict.json</c> — every non-verdict role — is silently skipped, which
/// is why no role check is needed here: the file's existence is the population.
/// </para>
/// </summary>
internal static class VerdictInstrumentStamp
{
    /// <summary>
    /// The review role's structured output (<c>WorkerRoles.json</c>) — the one file this type
    /// annotates. Named here rather than derived from the role's output list because the annotation is
    /// specific to the ReviewVerdict schema, not to "whatever the first output happens to be called".
    /// </summary>
    internal const string VerdictOutputName = "verdict.json";

    /// <param name="verifyStep">Null when no verify step ran for this room.</param>
    internal static async Task ApplyAsync(
        string roomDirectoryPath, CommandResult result, Baton.Mutation.VerifyStep.Outcome? verifyStep)
    {
        foreach (var step in result.State.Steps)
        {
            if (step.LatestExecutionId is not { } execId)
            {
                continue;
            }

            var verdictPath = Path.Combine(
                roomDirectoryPath,
                Baton.Artifacts.ArtifactManager.ArtifactsDirectoryName,
                $"execution_{execId}",
                VerdictOutputName);
            if (!File.Exists(verdictPath))
            {
                continue;
            }

            // CancellationToken.None: the review is already finished and its outputs are already durable.
            var stamped = await Baton.Mutation.VerifyStep
                .InjectInstrumentsAsync(verdictPath, verifyStep?.Instruments, CancellationToken.None)
                .ConfigureAwait(false);
            if (stamped)
            {
                continue;
            }

            Console.Error.WriteLine(verifyStep is null
                ? $"Warning: could not check '{verdictPath}' for a model-written 'instruments' field. No "
                    + "verify step ran for this room, so any value under that key is the worker's own "
                    + "claim rather than an engine record."
                : $"Warning: could not record the verify step's instruments on '{verdictPath}'. The verify "
                    + $"results themselves are unaffected, at '{verifyStep.ResultsFilePath}'.");
        }
    }
}
