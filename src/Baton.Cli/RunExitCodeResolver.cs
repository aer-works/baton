using Baton.Domain;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// The exit codes <c>baton run</c>/<c>baton dispatch</c> return (#1356) — distinct per failure class so a
/// caller can branch on <c>$?</c>/<c>%ERRORLEVEL%</c> alone, without parsing <c>status --json</c>.
/// <c>baton resume</c> (#1359) also routes here, on its own design ruling that it gets the same
/// truthful completion contract. <c>baton cancel</c>/<c>baton decide</c>/<c>baton supply</c> keep their
/// pre-existing 0/1 contract (<c>Program</c> only routes here for <c>run</c>/<c>dispatch</c>/<c>resume</c>)
/// — those commands were not named in #1356's scope, and folding them in was not asked for.
/// </summary>
public enum RunExitCode
{
    Succeeded = 0,
    Failed = 1,
    ValidationRefused = 2,

    /// <summary>
    /// Either a step's own failure was a binding timeout (<see cref="RunExitCodeResolver.ResolveFailed"/>),
    /// or — #1378 — <c>baton run --wait --wait-timeout &lt;minutes&gt;</c>'s poll loop hit that bound
    /// before the room reached Terminal (<see cref="CommandResult.WaitTimedOut"/>). The room's own
    /// ledger state differs between the two: the first is a genuinely Terminal, Failed room; the
    /// second is still Paused/Running — read <c>baton status</c> to tell them apart.
    /// </summary>
    Timeout = 3,
    Cancelled = 4,

    /// <summary>
    /// #1374 F1: <see cref="Baton.Concurrency.WorkflowLockedException"/> or
    /// <see cref="Baton.Store.FlowJournalHeldException"/> reached <c>Program</c>'s catch —
    /// another Flow instance already holds this room. Distinct from <see cref="ValidationRefused"/>
    /// on purpose: this room may be perfectly healthy (a live pump, or a background sweep's brief
    /// lock), so nothing here is refused and no terminal sentinel is written. The caller's answer is
    /// "retry later", not "this room is done" — check <c>baton status</c> or the room's own ledger
    /// rather than treating this exit code as a terminal outcome.
    /// </summary>
    RoomHeld = 5,
}

/// <summary>
/// Classifies a <see cref="CommandResult"/> into a <see cref="RunExitCode"/>. Pure and side-effect
/// free so every class is covered by direct unit tests against hand-built <see cref="FlowState"/>s,
/// not just the handful an end-to-end shell fixture can cheaply reproduce.
/// <para>
/// #1388 review F9: for <c>baton resume</c>, this still classifies the WHOLE room's <see cref="FlowState"/>,
/// not "did the resumed step itself succeed" — a successful resume of one step in a room where a
/// different step already Failed exits <see cref="RunExitCode.Failed"/>, consistent with #1356's
/// room-scoped table rather than a per-verb verdict. Read the resumed step's own
/// <see cref="StepState.Status"/> (via <c>status --json</c>) for that.
/// </para>
/// </summary>
public static class RunExitCodeResolver
{
    public static RunExitCode Resolve(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // #1378: baton run --wait --wait-timeout <minutes> expired before the room reached Terminal.
        // Checked ahead of the state-based classification below because the room itself is still
        // Paused/Running -- nothing in the ledger says "timeout", only this call's own poll loop does.
        if (result.WaitTimedOut)
        {
            return RunExitCode.Timeout;
        }

        var outcome = WorkflowOutcome.Describe(result.State);
        return outcome switch
        {
            WorkflowOutcome.Succeeded => RunExitCode.Succeeded,
            WorkflowOutcome.Cancelled => RunExitCode.Cancelled,
            WorkflowOutcome.Failed => ResolveFailed(result.State.Steps),
            // Running or Paused: the pump returned short of Terminal (no --wait, or --wait's poll
            // loop was cancelled -- e.g. Ctrl-C -- before the room settled; a --wait-timeout expiry
            // is handled above, ahead of this switch, and never reaches here). Not one of #1356's
            // four named failure classes, so this stays in the general Failed bucket rather than
            // minting a fifth code — a caller that cares about "still going" reads status --json's
            // `state` field instead.
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
