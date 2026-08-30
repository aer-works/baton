using Baton.Flow.Domain;

namespace Baton.Flow.Mutation;

/// <summary>
/// Capability 11's validation half for on-demand cancellation: a pure function — no I/O —
/// deciding whether a candidate <see cref="FlowEvent.CancellationRequested"/> is admissible. The
/// only rule Phase 1 enforces is admission: the target must have been accepted at some point in the
/// log. A target that has already reached a terminal outcome is not rejected here — the too-late
/// request is still recorded, and the projector (not this validator) derives that it
/// changes nothing.
/// </summary>
public static class CancellationValidator
{
    /// <exception cref="UnknownExecutionIdException">
    /// <paramref name="targetExecutionId"/> was never admitted via
    /// <see cref="FlowEvent.ExecutionRequestAccepted"/>.
    /// </exception>
    public static void Validate(IReadOnlySet<ExecutionId> knownExecutionIds, ExecutionId targetExecutionId)
    {
        ArgumentNullException.ThrowIfNull(knownExecutionIds);

        if (!knownExecutionIds.Contains(targetExecutionId))
        {
            throw new UnknownExecutionIdException($"Execution '{targetExecutionId}' was never admitted for execution.");
        }
    }
}
