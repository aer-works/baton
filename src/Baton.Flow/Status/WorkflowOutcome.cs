using Baton.Flow.Domain;

namespace Baton.Flow.Status;

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
    /// A step whose reason names a dispatch timeout (<see cref="Baton.Flow.Outcomes.OutcomeClassifier"/>'s
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
