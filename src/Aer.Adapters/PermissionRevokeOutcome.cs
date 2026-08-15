namespace Aer.Adapters;

/// <summary>
/// What <see cref="RuntimePermissionGrantAmender.RevokeAsync"/> did, with the same honesty
/// <see cref="PermissionAmendOutcome"/> applies to the other direction: a bool would conflate "taken
/// back" with "there was nothing to take back" with "there was, and it could not be written".
/// </summary>
public enum PermissionRevokeOutcome
{
    /// <summary>The standing permission is gone from the room's binding. The next turn builds without it.</summary>
    Revoked,

    /// <summary>
    /// Nothing needed writing and that is correct: the worker never held this standing permission.
    /// Not a failure — revoking twice is the same state as revoking once, which is the property that
    /// lets a surface offer it without first proving what is held.
    /// </summary>
    NothingToRevoke,

    /// <summary>
    /// The permission is held but could NOT be taken back — no <c>bindings.json</c>, or no such
    /// worker in it. The operator asked to withdraw something and it is still granted, so the caller
    /// must surface this rather than report a revocation that did not happen. The asymmetry with
    /// <see cref="PermissionAmendOutcome.CouldNotPersist"/> is deliberate: failing to grant leaves
    /// the person with less authority than they asked for, failing to revoke leaves them with more.
    /// </summary>
    CouldNotPersist,
}
