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
/// Every name in a multi-name capture is validated before any of them is written OR the resolution is
/// journaled (<c>MutationInterface.RecordCaptureResolutionAsync</c>), so a bad name never leaves an
/// earlier one on disk and never journals a fact this exception's own throw contradicts.
/// </para>
/// <para>
/// #1608 review finding 5 changed what "re-run it" means for a genuine mid-write
/// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> across multiple declared names
/// (disk full, permissions revoked between writes): the resolution is journaled BEFORE the writes, not
/// after, so this exception can now surface with <see cref="Domain.FlowEvent.CaptureResolved"/>
/// ALREADY appended and one or more declared names still missing. Re-running <c>baton resolve
/// --execution &lt;id&gt;</c> against that same, already-accepted execution does not re-attempt an
/// ordinary fresh resolution (the step is no longer awaiting one) — it is admitted as a repair request
/// instead and re-materializes exactly the names still missing from the still-durable captured
/// response, idempotently, without appending a second fact. See
/// <c>MutationInterface.ReconcileAcceptedCaptureAsync</c>'s own remarks. Until that re-run happens the
/// room's <c>terminal.json</c> is stale too — this throw escapes before <c>Program.cs</c> reaches its
/// sentinel step, so a file-watcher keeps reading the pre-resolution word (#1608 re-review finding 6).
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
