using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// The single coarse outcome word for a <see cref="FlowState"/> — "Running", "Paused", or, once
/// <see cref="WorkflowStatus.Terminal"/> is reached, which of "Succeeded" / "Failed" / "Cancelled" it
/// settled into. <see cref="WorkflowStatus"/> itself only says the pump reached its fixed point, not
/// which one — every other terminal-outcome consumer (<c>StatusCommand</c>'s <c>--json</c>,
/// <c>RunExitCodeResolver</c>, the terminal sentinel) needs this same word, so it is computed here
/// once rather than re-derived per caller (#1356).
/// </summary>
public static class WorkflowOutcome
{
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// #1586 S1 (state-truth design, ratified 2026-09-01 amendment): journal facts alone cannot
    /// distinguish success from failure for this room — the two-predicate model (execution outcome vs
    /// contract completion) disagrees with itself, e.g. work-evidence contradicts contract-evidence
    /// (#1594's missing-output-with-envelope shape is the canonical instance) or a worktree fingerprint
    /// does not reconcile at settle time. A single added value, not a two-field split, per the ruling's
    /// own wording.
    /// <para>
    /// <b>First producers landed by #1623</b> (contract: <c>spec/baton.md</c> §3):
    /// <see cref="DescribeTerminal"/> reads a step's
    /// <see cref="StepState.IndeterminateReason"/> — set by <see cref="FlowEvent.VerifyFailed"/> or
    /// <see cref="FlowEvent.ExecutionArrested"/> — ahead of <see cref="Failed"/>/<see cref="Rejected"/>.
    /// #1608's own producer (the #1594 captured-response arm settling here instead of
    /// <see cref="Failed"/>) is a separate, still-unmerged PR (#1644 as of this writing) — every
    /// captured-response room's <see cref="Describe"/> reading is unchanged by #1623
    /// (<c>WorkflowOutcomeAndExitCodeTests</c> pins that; <c>spec/baton.md</c> §3 names the exact test).
    /// Whichever of #1623/#1644 merges second should fold both into a single check here
    /// rather than leaving two Indeterminate arms side by side — noted so the merge order is explicit,
    /// not silent.
    /// </para>
    /// <para>
    /// <b>Consumer obligations (ruling item 2, spelled out in full in <c>spec/baton.md</c> §3):</b>
    /// a room reading this refuses bare <c>baton redispatch</c> with a diagnosis
    /// (<c>Baton.Cli.RedispatchCommand</c>); the fleet glass renders a distinct chip; leaving this
    /// value always requires a conductor's own recorded justification — it is not a state a room
    /// exits on its own.
    /// </para>
    /// </summary>
    public const string Indeterminate = "Indeterminate";

    public static string Describe(FlowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Status switch
        {
            WorkflowStatus.Running => Running,
            WorkflowStatus.Paused => Paused,
            WorkflowStatus.Terminal => DescribeTerminal(state.Steps),
            _ => state.Status.ToString(),
        };
    }

    /// <summary>
    /// A step whose reason names a dispatch timeout (<see cref="Baton.Outcomes.OutcomeClassifier"/>'s
    /// fixed "Execution timed out." sentence) — the only signal available for that distinction today.
    /// There is no structural <see cref="FailureClassification"/> value for it (its vocabulary
    /// is <c>Retryable</c>/<c>Permanent</c>/<c>ExhaustedUntil</c>/<c>ToolDenied</c> only), so this reads
    /// the same fixed diagnostic sentence a person already reads in <c>FlowStateReporter</c>'s output
    /// rather than adding a second, parallel classification the event log does not carry.
    /// </summary>
    public static bool IsTimeoutFailure(StepState step) =>
        step.Status == StepStatus.Failed
        && step.LatestFailureReason is { } reason
        && reason.StartsWith("Execution timed out.", StringComparison.Ordinal);

    private static string DescribeTerminal(IReadOnlyList<StepState> steps)
    {
        // Vacuously Succeeded for a zero-step Terminal state (a degenerate workflow with nothing to
        // run) — the same reading `Program`'s pre-#1356 exit-code check already gave it; preserved
        // rather than reclassified so this refactor changes no observable behaviour for that case.
        if (steps.Count == 0)
        {
            return Succeeded;
        }

        if (steps.All(step => step.Status == StepStatus.Succeeded))
        {
            return Succeeded;
        }

        // #1623: checked ahead of the ordinary Failed/Rejected read below -- a verify failure or a
        // token-budget arrest is projected as an underlying StepStatus.Failed (see
        // StateProjector.ApplyIndeterminate) so RetryEngine/DeriveWorkflowStatus's existing
        // deliverability machinery needs no separate StepStatus value, but the room-level outcome must
        // read Indeterminate, not Failed, for a step carrying this reason.
        if (steps.Any(step => step.IndeterminateReason is not null))
        {
            return Indeterminate;
        }

        if (steps.Any(step => step.Status is StepStatus.Failed or StepStatus.Rejected))
        {
            return Failed;
        }

        if (steps.Any(step => step.Status == StepStatus.Cancelled))
        {
            return Cancelled;
        }

        // Reachable only by a step left Pending in a Terminal workflow with nothing else failed or
        // cancelled to explain why it was never dispatched (e.g. a DAG the Dependency Resolver could
        // never reach) — treated as Failed rather than silently reading as Succeeded.
        return Failed;
    }
}
