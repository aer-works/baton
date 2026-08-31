using Baton.Domain;

namespace Baton.Cli;

/// <summary>
/// Resolves "the running lane" (#1495) from a room's own projected <see cref="FlowState"/> — the
/// shared targeting rule both <see cref="CancelCommand"/>'s room-level <c>--execution</c>-omitted path
/// and <see cref="CancelRequestPoller"/>'s <c>latest</c> literal resolve against, so the two callers
/// never drift on what "the running lane" means. Fail closed: zero or more than one
/// <see cref="StepStatus.Running"/> step is refused rather than guessed.
/// </summary>
public static class RunningExecutionResolver
{
    /// <param name="Single">
    /// The one running execution's id, or <c>null</c> when <see cref="RunningExecutionIds"/> does not
    /// contain exactly one entry.
    /// </param>
    /// <param name="RunningExecutionIds">
    /// Every currently-<see cref="StepStatus.Running"/> step's latest execution id, in
    /// <see cref="FlowState.Steps"/> order — the candidate list a refusal message names.
    /// </param>
    public sealed record Result(ExecutionId? Single, IReadOnlyList<ExecutionId> RunningExecutionIds);

    public static Result Resolve(FlowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var running = state.Steps
            .Where(s => s.Status == StepStatus.Running && s.LatestExecutionId is not null)
            .Select(s => s.LatestExecutionId!.Value)
            .ToList();

        return new Result(running.Count == 1 ? running[0] : null, running);
    }
}
