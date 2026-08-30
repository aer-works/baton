namespace Baton.Cli;

/// <summary>
/// What a <c>PreToolUse</c> hook makes of <c>BATON_HOOK_DENIED_TOOLS</c> (#600).
/// </summary>
/// <remarks>
/// <para>
/// The value is vendor-tagged — <c>claude:Bash,Edit</c>, <c>agy:run_command</c> — so that three
/// situations the channel used to collapse into one are told apart. Before this, <c>null</c>,
/// <c>""</c> and <c>"   "</c> all parsed to an empty set and every one of them allowed, so
/// <em>"AER set this and nothing is withheld"</em> was indistinguishable from
/// <em>"the variable never arrived"</em>. Both failed open, and the second is exactly the failure
/// <c>agy.hook-env-inherited</c> is a sentinel for: if a vendor stopped inheriting the environment,
/// the gate degraded to a total allow that looked identical to a working one.
/// </para>
/// <para>
/// The tag also makes a wrong-vendor list loud instead of silent. agy names tools
/// <c>run_command</c>/<c>view_file</c> where claude names them <c>Bash</c>/<c>Read</c>; comparing one
/// vendor's names against the other's matches nothing, which reads as "allow everything". No path
/// produces that today — both adapters set the variable unconditionally in <c>Resolve</c> and
/// <c>CoreDispatcher</c> applies the target's environment last — but the failure mode if one ever did
/// is a total allow, so it is worth being unable to happen rather than merely unlikely.
/// </para>
/// <para>
/// An <em>empty tagged</em> list withholds no <em>category</em> — <c>BuildDeniedTools</c> returns
/// empty whenever <c>PermissionGrant</c> is null, the raw <c>PermissionScope</c> escape hatch, which
/// carries no categories at all. It is not a licence to skip the rest of the check: since #679 both
/// gates go on to bound a granted <em>write</em>, and this type says only what was withheld by name,
/// never what may be done with what was granted. An earlier version of this paragraph said an empty
/// list "still allows, and must" — which is the early return #679 removed, and restoring it on this
/// paragraph's authority would undo that fix silently.
/// </para>
/// </remarks>
public sealed record DeniedToolList(DeniedToolListStatus Status, IReadOnlySet<string> Tools)
{
    /// <summary>
    /// Splits a vendor-tagged value, judged from the point of view of <paramref name="ownVendorTag"/>.
    /// </summary>
    public static DeniedToolList Parse(string? raw, string ownVendorTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownVendorTag);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new DeniedToolList(DeniedToolListStatus.Absent, new HashSet<string>(StringComparer.Ordinal));
        }

        var separator = raw.IndexOf(':');
        // An untagged value is treated as a foreign one rather than as this vendor's list. It can only
        // come from a worker spawned by an AER older than this hook binary; guessing it belongs to us
        // would resurrect the very ambiguity the tag exists to remove.
        if (separator < 0 || !raw[..separator].Trim().Equals(ownVendorTag, StringComparison.Ordinal))
        {
            return new DeniedToolList(DeniedToolListStatus.WrongVendor, new HashSet<string>(StringComparer.Ordinal));
        }

        var tools = raw[(separator + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return new DeniedToolList(DeniedToolListStatus.Present, tools);
    }
}

/// <summary>Which of #600's three cases a parsed <c>BATON_HOOK_DENIED_TOOLS</c> falls into.</summary>
public enum DeniedToolListStatus
{
    /// <summary>AER said what is withheld. An empty <see cref="DeniedToolList.Tools"/> means "nothing".</summary>
    Present,

    /// <summary>Nothing arrived, so this gate cannot know what is withheld. Deny.</summary>
    Absent,

    /// <summary>Another vendor's list, whose names this gate cannot judge. Deny.</summary>
    WrongVendor,
}
