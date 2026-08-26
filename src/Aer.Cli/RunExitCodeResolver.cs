using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// The exit codes <c>aer run</c>/<c>aer dispatch</c> return (#1356) — distinct per failure class so a
/// caller can branch on <c>$?</c>/<c>%ERRORLEVEL%</c> alone, without parsing <c>status --json</c>.
/// <c>aer cancel</c>/<c>aer decide</c>/<c>aer supply</c> keep their pre-existing 0/1 contract
/// (<c>Program</c> only routes here for <c>run</c>/<c>dispatch</c>) — those commands were not named in
/// #1356's scope, and folding them in was not asked for.
/// </summary>
public enum RunExitCode
{
    Succeeded = 0,
    Failed = 1,
    ValidationRefused = 2,
    Timeout = 3,
    Cancelled = 4,
}

/// <summary>
/// Classifies a <see cref="CommandResult"/> into a <see cref="RunExitCode"/>. Pure and side-effect
/// free so every class is covered by direct unit tests against hand-built <see cref="FlowState"/>s,
/// not just the handful an end-to-end shell fixture can cheaply reproduce.
/// </summary>
public static class RunExitCodeResolver
{
    public static RunExitCode Resolve(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var outcome = WorkflowOutcome.Describe(result.State);
        return outcome switch
        {
            WorkflowOutcome.Succeeded => RunExitCode.Succeeded,
            WorkflowOutcome.Cancelled => RunExitCode.Cancelled,
            WorkflowOutcome.Failed => ResolveFailed(result.State.Steps),
            // Running or Paused: the pump returned short of Terminal (no --wait, or --wait's poll
            // loop was cancelled before the room settled). Not one of #1356's four named failure
            // classes, so this stays in the general Failed bucket rather than minting a fifth code —
            // a caller that cares about "still going" reads status --json's `state` field instead.
            _ => RunExitCode.Failed,
        };
    }

    private static RunExitCode ResolveFailed(IReadOnlyList<StepState> steps)
    {
        var hasHardFailure = steps.Any(step => step.Status == StepStatus.Rejected
            || (step.Status == StepStatus.Failed && !WorkflowOutcome.IsTimeoutFailure(step)));

        return hasHardFailure ? RunExitCode.Failed
            : steps.Any(WorkflowOutcome.IsTimeoutFailure) ? RunExitCode.Timeout
            : RunExitCode.Failed;
    }
}
