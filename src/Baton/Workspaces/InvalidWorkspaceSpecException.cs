namespace Baton.Workspaces;

/// <summary>
/// Raised by <see cref="WorktreeProvisioner.ValidateSpec"/> when a worktree workspace names a
/// repository that is not an absolute, fully qualified path, or an empty ref — the bind-time refusal
/// that keeps a bad spec from being discovered only at dispatch, after the run has paid in full
/// (#668's class). Mirrors <c>UnknownWorkingDirectoryProfileException</c>'s role for the existing
/// referential workspace, one workspace kind over.
/// </summary>
public sealed class InvalidWorkspaceSpecException : BatonFlowException
{
    public InvalidWorkspaceSpecException(string message)
        : base(message)
    {
    }
}
