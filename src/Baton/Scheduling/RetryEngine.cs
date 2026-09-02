using Baton.Domain;

namespace Baton.Scheduling;

/// <summary>
/// Capability 10: decides whether a step's latest failed attempt is eligible for a
/// brand-new <see cref="ExecutionRequest"/>. A pure predicate over <see cref="StepState"/> and
/// <see cref="RetryPolicy"/> — no I/O, no dispatch — consulted by the <see cref="DependencyResolver"/>.
/// </summary>
public static class RetryEngine
{
    /// <summary>
    /// True exactly when <paramref name="stepState"/>'s latest attempt is <see cref="StepStatus.Failed"/>,
    /// its <see cref="StepState.LatestFailureClassification"/> is not <see cref="FailureClassification.Permanent"/>
    /// (absent or unrecognized defaults to <see cref="FailureClassification.Retryable"/>), and
    /// <see cref="StepState.ConsecutiveFailureCount"/> has not yet reached <paramref name="retryPolicy"/>'s
    /// <see cref="RetryPolicy.MaxAttempts"/> — the total number of attempts allowed per round.
    /// <see cref="FailureClassification.ExhaustedUntil"/> bypasses the attempts check entirely:
    /// 0026 obliges the engine to never spend retry attempts against an exhausted quota, because
    /// retrying is not what is wrong — the retry is paced to the vendor's reset instant instead
    /// (MutationInterface.GetRetryObligations), never abandoned for lack of budget.
    /// <see cref="StepStatus.Cancelled"/> is never retried regardless of policy: cancellation is a
    /// decision to stop, not a failure to route around, and this predicate only ever returns true for a
    /// step whose latest attempt is <see cref="StepStatus.Failed"/>.
    /// <see cref="StepState.RetryForeclosed"/> true bypasses everything below straight to
    /// <c>false</c> (#1586 S1): a <see cref="Domain.FlowEvent.StepRetryForeclosed"/> voided this
    /// step's scheduled retry deliberately — unconditionally, regardless of remaining
    /// <see cref="RetryPolicy.MaxAttempts"/> budget or <see cref="FailureClassification.ExhaustedUntil"/>'s
    /// own bypass of it, the same way <see cref="StepState.LinkedFromExecutionId"/> below already
    /// short-circuits for an unrelated reason.
    /// <see cref="StepState.LinkedFromExecutionId"/> not null bypasses everything above straight to
    /// <c>false</c> (issue #1359 F4): a failed resume is never auto-retried by the settling pump.
    /// <c>baton resume</c>'s own contract is "one message per resume invocation" — a MaxAttempts-driven
    /// retry would silently dispatch further, unattended vendor invocations against a budget the
    /// operator was never asked about, and <c>PrepareExecutionAsync</c>'s retry-minted
    /// <see cref="ExecutionRequest"/> carries no <see cref="ExecutionRequest.LinkedFromExecutionId"/>
    /// of its own, so a retry would also silently clear the very link this verb exists to record.
    /// </summary>
    public static bool MayRetry(StepState stepState, RetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(stepState);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        if (stepState.RetryForeclosed)
        {
            return false;
        }

        // #1623: retry-ineligible by an explicit arm, not merely a side effect of RetryForeclosed
        // above (the ruling's own wording) -- a verify failure or a token-budget arrest settles
        // Indeterminate for the conductor, and must never be retried regardless of how the two
        // producers happen to set RetryForeclosed alongside this.
        if (stepState.IndeterminateReason is not null)
        {
            return false;
        }

        if (stepState.LinkedFromExecutionId is not null)
        {
            return false;
        }

        return stepState.Status == StepStatus.Failed
            && stepState.LatestFailureClassification != FailureClassification.Permanent
            && stepState.LatestFailureClassification != FailureClassification.ToolDenied
            && (stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil
                || stepState.ConsecutiveFailureCount < retryPolicy.MaxAttempts);
    }
}
