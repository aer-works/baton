namespace Aer.Adapters;

/// <summary>
/// The result of <see cref="RuntimePermissionGrantAmender.AmendAsync"/>, distinguishing the three
/// outcomes a bool conflates — the middle one (a scoped answer that <em>meant</em> to persist but
/// could not) is a silent narrowing the daemon must surface, not swallow.
/// </summary>
public enum PermissionAmendOutcome
{
    /// <summary>A standing permission was written to the room's chat-worker binding.</summary>
    Persisted,

    /// <summary>
    /// Nothing needed writing and that is correct: the rung does not persist by design (AllowOnce, Deny,
    /// or the held cross-room AllowCommandAnyRoom), or the grant already covered the asked command (the
    /// family is already allowed, already denied, or the room is already unscoped). Not a failure — no
    /// operator-facing signal.
    /// </summary>
    NoChangeNeeded,

    /// <summary>
    /// A persisting rung (AllowCommandInRoom/AllowRoom) could NOT be persisted — no <c>bindings.json</c>,
    /// no such worker, or the asked command could not be parsed into a pattern (fail-closed). The answer
    /// therefore applies once only despite the operator picking a standing rung, so the caller must
    /// surface it rather than let the narrowing pass silently.
    /// </summary>
    CouldNotPersist,
}
