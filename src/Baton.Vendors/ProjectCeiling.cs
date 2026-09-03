namespace Baton.Vendors;

/// <summary>
/// #1166 — decision 0004's project scope ("the ceiling"): the operator's own outer bound on what any
/// worker-binding config can grant against a given project path, expressed in the same four-category
/// vocabulary <see cref="PermissionGrant"/> already carries. 0004 names the ceiling's existence and
/// where it composes (project ∩ room ∩ step, always narrowing) but not a closed set of named levels —
/// this record is the fallback the issue's own scope ruling calls for: reuse the vocabulary
/// <see cref="ClaudeWorkerAdapter.TryTranslatePermissionGrant"/> already maps, rather than inventing a
/// second one.
/// </summary>
public sealed record ProjectCeiling(
    bool ReadFiles,
    bool WriteFiles,
    bool RunShellCommands,
    bool NetworkAccess)
{
    /// <summary>
    /// The ceiling a first-use "anything goes" trust decision produces — every category open, so it
    /// caps nothing. The only shape under which a role binding's raw <see cref="WorkerInvocation.PermissionScope"/>
    /// escape hatch (no structured <see cref="PermissionGrant"/> to intersect against) is still
    /// dispatchable — see <see cref="ProjectCeilingGate"/>.
    /// </summary>
    public static readonly ProjectCeiling Unrestricted = new(true, true, true, true);

    /// <summary>True when every category is open — the ceiling caps nothing a role grant could ask for.</summary>
    public bool IsUnrestricted => ReadFiles && WriteFiles && RunShellCommands && NetworkAccess;

    /// <summary>
    /// Decision 0004's intersection rule (spec/baton.md §9 states it canonically) applied as a
    /// per-category logical AND, boolean by boolean below.
    /// </summary>
    public PermissionGrant Cap(PermissionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant with
        {
            ReadFiles = grant.ReadFiles && ReadFiles,
            WriteFiles = grant.WriteFiles && WriteFiles,
            RunShellCommands = grant.RunShellCommands && RunShellCommands,
            NetworkAccess = grant.NetworkAccess && NetworkAccess,
        };
    }
}
