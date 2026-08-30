namespace Baton.Mutation;

/// <summary>
/// Raised when <c>MutationInterface.RequestCancellationAsync</c>'s target
/// <see cref="Domain.ExecutionId"/> was never admitted — no
/// <see cref="Domain.FlowEvent.ExecutionRequestAccepted"/> for it exists anywhere in the log. A
/// <em>known but already-terminal</em> target is not this — it is a too-late no-op. Rejected, never
/// silently widened: nothing is appended to the log when this is
/// thrown.
/// </summary>
public sealed class UnknownExecutionIdException : BatonFlowException
{
    public UnknownExecutionIdException(string message)
        : base(message)
    {
    }
}
