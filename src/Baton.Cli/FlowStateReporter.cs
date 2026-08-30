using Baton.Flow.Artifacts;
using Baton.Flow.Domain;
using Baton.Flow.Status;

namespace Baton.Cli;

/// <summary>
/// The pause-aware reporting M12 Phase 3 requires: without a paused step's <see cref="ExecutionId"/>
/// and declared <c>PausePoint.SupersedeTargets</c> printed somewhere, a terminal user has no way to
/// know what to pass to <c>baton decide --execution</c>/<c>--target-step</c>. Shared by every command
/// <see cref="Program"/> dispatches to, so <c>baton run</c> and <c>baton decide</c> report paused state
/// identically.
/// </summary>
public static class FlowStateReporter
{
    public static void Report(TextWriter output, CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);

        var pausePointByStepId = result.Snapshot.Steps.ToDictionary(step => step.StepId, step => step.PausePoint);
        var stepDefByStepId = result.Snapshot.Steps.ToDictionary(step => step.StepId);

        // #628: an already-terminal room directory reports the prior run's status, writes no new
        // events, and exits non-zero — with nothing to distinguish it from a fresh failure. Say
        // which template actually ran, since it is not necessarily the file named on the command line.
        if (result.ResumedFromSnapshot)
        {
            output.WriteLine(
                $"Resumed the snapshot already bound in this room directory " +
                $"(template '{result.Snapshot.WorkflowTemplateId.Value}').");
        }

        output.WriteLine($"Workflow status: {result.State.Status}");
        var artifactsRootPath = result.RoomDirectoryPath is not null
            ? Path.Combine(result.RoomDirectoryPath, ArtifactManager.ArtifactsDirectoryName)
            : null;

        foreach (var step in result.State.Steps)
        {
            if (step.Status == StepStatus.Paused)
            {
                var pausePoint = pausePointByStepId[step.StepId]!;
                var supersedeTargets = pausePoint.SupersedeTargets;
                var supersedeText = supersedeTargets.Count == 0
                    ? "none"
                    : string.Join(", ", supersedeTargets.Select(target => target.Value));

                // #334: report which human act the pause demands, not just "Paused" — a terminal user
                // triaging pauses needs the same needs-input/ready-for-review distinction the clients show.
                var pausedLabel = pausePoint.Kind switch
                {
                    PausePointKind.NeedsInput => "Paused — awaiting input",
                    _ => "Paused — awaiting review",
                };

                output.WriteLine(
                    $"  {step.StepId}: {pausedLabel} (execution={step.LatestExecutionId}, outcome={step.PausedOutcome}, " +
                    $"supersede-targets: {supersedeText})");
            }
            else
            {
                // #597: `worker: Failed` beside `ExitCode: 0` reads as an AER bug rather than a
                // worker one, sending diagnosis in the wrong direction. The reason is a fact Flow
                // already holds at classification time, so naming it here costs nothing and is what
                // the issue's acceptance criteria ask for by name — "in flow.jsonl *and* in baton
                // run's own terminal output". Null for a success, and for a failure recorded before
                // the field existed.
                var reasonSuffix = string.IsNullOrWhiteSpace(step.LatestFailureReason)
                    ? string.Empty
                    : $" — {step.LatestFailureReason}";

                output.WriteLine($"  {step.StepId}: {step.Status}{reasonSuffix}");
            }

            // #740: At settle, baton run prints one line per produced output of each succeeded execution:
            // output name -> absolute path. A Paused step whose underlying outcome Succeeded — the
            // ready-for-review approval gate — prints them too: that is exactly the moment a person
            // wants to open what the worker produced.
            if (artifactsRootPath is not null && stepDefByStepId.TryGetValue(step.StepId, out var stepDef))
            {
                foreach (var (outputName, outputPath) in StepOutputResolver.Resolve(step, stepDef, artifactsRootPath))
                {
                    output.WriteLine($"  {outputName} -> {outputPath}");
                }
            }
        }

        foreach (var stepLess in result.State.StepLessExecutions)
        {
            output.WriteLine($"  (supplementary) {stepLess.Worker}: execution={stepLess.ExecutionId} pending");
        }
    }
}

