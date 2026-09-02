namespace Baton.Mutation;

/// <summary>
/// Raised when a requested <c>baton resolve</c> (#1608) is invalid against the room's current
/// <see cref="Domain.FlowState"/>: the named execution has no unresolved
/// <see cref="Domain.FlowEvent.ExecutionIndeterminate"/>, a rejection was requested with no
/// <c>--reason</c>, a declared output name failed its reserved/traversal check, or the captured
/// response file itself could not be read or (see below) written. Mirrors
/// <see cref="InvalidExternalDecisionException"/>'s role for <c>baton decide</c> — rejected, never
/// silently widened; no <see cref="Domain.FlowEvent.CaptureResolved"/> is ever appended to the log
/// when this is thrown.
/// <para>
/// Every name in a multi-name capture is validated before any of them is written
/// (<c>MutationInterface.RecordCaptureResolutionAsync</c>), so a bad name never leaves an earlier one
/// on disk. A genuine mid-write <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>
/// across multiple declared names (disk full, permissions revoked between writes) is the one case
/// this exception can still surface with an earlier name already written and no
/// <see cref="Domain.FlowEvent.CaptureResolved"/> appended — an environment failure, not a validation
/// one, and re-running <c>baton resolve</c> once the environment issue is fixed re-attempts the same
/// resolution idempotently (the write is the same content under the same name either way).
/// </para>
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
