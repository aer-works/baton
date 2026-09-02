using Baton.Domain;

namespace Baton.Cli;

/// <summary>
/// Resolves "the target lane" (#1495, widened by #1607) from a room's own projected
/// <see cref="FlowState"/> — the shared targeting rule for the two CLI callers:
/// <see cref="CancelCommand"/>'s room-level <c>--execution</c>-omitted path and
/// <see cref="CancelRequestPoller"/>'s <c>latest</c> literal. A candidate is either a currently
/// <see cref="StepStatus.Running"/> step, or a quota-parked one — <see cref="StepStatus.Failed"/>
/// with a scheduled <see cref="StepState.RetryNotBefore"/> — the identical shape
/// <c>MutationInterface.IsParkedRetryTarget</c> and <see cref="CancelRequestPoller"/>'s own
/// <c>isParked</c> check already use for "parked," reused here rather than a second definition
/// (#1607: one register for what "parked" means, not three). A parked candidate does NOT need to
/// agree with <c>CoreEventAggregation</c> or <c>NonProcessCancellationDetector</c>'s own
/// Running-only filters — those settle a live process or non-process execution in the same round;
/// a parked target is deliberately routed through the separate delivery path #1605 built
/// (<c>InFlightExecutionRegistry.MarkParkedCancelIntent</c> /
/// <c>MutationInterface.SettleParkedCancelIntentsAsync</c>) instead. Fail closed: zero or more than
/// one candidate is refused rather than guessed.
/// </summary>
public static class RunningExecutionResolver
{
    /// <param name="Single">
    /// The one candidate execution's id, or <c>null</c> when <see cref="RunningExecutionIds"/> does
    /// not contain exactly one entry.
    /// </param>
    /// <param name="RunningExecutionIds">
    /// Every currently-<see cref="StepStatus.Running"/> or quota-parked step's latest execution id,
    /// in <see cref="FlowState.Steps"/> order — the candidate list a refusal message names. The name
    /// predates #1607's widening (record-once: kept rather than renamed, to avoid touching every
    /// caller and message for a cosmetic-only change) — it no longer means "Running only."
    /// </param>
    public sealed record Result(ExecutionId? Single, IReadOnlyList<ExecutionId> RunningExecutionIds);

    public static Result Resolve(FlowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var candidates = state.Steps
            .Where(s => s.LatestExecutionId is not null
                && (s.Status == StepStatus.Running
                    || (s.Status == StepStatus.Failed && s.RetryNotBefore is not null)))
            .Select(s => s.LatestExecutionId!.Value)
            .ToList();

        return new Result(candidates.Count == 1 ? candidates[0] : null, candidates);
    }
}
