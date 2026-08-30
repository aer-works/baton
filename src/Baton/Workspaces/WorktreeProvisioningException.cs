namespace Baton.Workspaces;

/// <summary>
/// Raised by <see cref="WorktreeProvisioner.Provision"/> when <c>git worktree add</c> itself fails —
/// an unknown ref, a ref already checked out in another tree, or git not being present. Distinct from
/// <see cref="InvalidWorkspaceSpecException"/>, which refuses a malformed spec before any git call;
/// this one carries a real git failure. Teardown never throws (a fault there must not fail a completed
/// room), so there is no teardown counterpart.
/// </summary>
public sealed class WorktreeProvisioningException : BatonFlowException
{
    public WorktreeProvisioningException(string message)
        : base(message)
    {
    }
}
