namespace Baton.Mutation;

/// <summary>
/// Raised when a requested <c>baton resolve</c> (#1608) is invalid against the room's current
/// <see cref="Domain.FlowState"/>: the named execution has no unresolved
/// <see cref="Domain.FlowEvent.ExecutionIndeterminate"/>, a rejection was requested with no
/// <c>--reason</c>, or the captured-response file could not be read/written. Mirrors
/// <see cref="InvalidExternalDecisionException"/>'s role for <c>baton decide</c> — rejected, never
/// silently widened; nothing is appended to the log and no file is written when this is thrown.
/// </summary>
public sealed class InvalidCaptureResolutionException : BatonFlowException
{
    public InvalidCaptureResolutionException(string message)
        : base(message)
    {
    }

    public InvalidCaptureResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
