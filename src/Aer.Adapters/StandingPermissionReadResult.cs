namespace Aer.Adapters;

/// <summary>
/// What <see cref="RuntimePermissionGrantAmender.GetStandingPermissionsAsync"/> found, and the grant it
/// found if it found one. The three outcomes and the record live in one file because they are one
/// answer: which of the three it is decides whether <see cref="Grant"/> means anything.
/// </summary>
/// <remarks>
/// The distinction that earns three cases rather than two is between a room with no worker set up and a
/// room whose worker holds nothing. Collapsed to one, a caller would tell someone their room grants
/// nothing when the truth is that nothing has been set up to grant anything yet — and #1238's revoke
/// path already keeps the same two apart, in the two sentences it picks between.
/// </remarks>
public sealed record StandingPermissionReadResult(
    StandingPermissionReadOutcome Outcome,
    PermissionGrant? Grant)
{
    /// <summary>The room has no <c>bindings.json</c> at all.</summary>
    public static StandingPermissionReadResult NoWorkerSetup() =>
        new(StandingPermissionReadOutcome.NoWorkerSetup, null);

    /// <summary>The room has one, and it names no such worker.</summary>
    public static StandingPermissionReadResult WorkerNotConfigured() =>
        new(StandingPermissionReadOutcome.WorkerNotConfigured, null);

    /// <summary>The worker is there, with <paramref name="grant"/> standing.</summary>
    public static StandingPermissionReadResult Configured(PermissionGrant grant) =>
        new(StandingPermissionReadOutcome.Configured, grant);
}

/// <summary>The three cases <see cref="StandingPermissionReadResult"/> distinguishes; see its remarks.</summary>
public enum StandingPermissionReadOutcome
{
    /// <summary>No worker setup in this room.</summary>
    NoWorkerSetup,

    /// <summary>Worker setup exists, but not for the requested worker.</summary>
    WorkerNotConfigured,

    /// <summary>The requested worker was found.</summary>
    Configured,
}
