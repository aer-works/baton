namespace Aer.Adapters;

/// <summary>
/// A structured, vendor-neutral permission grant (M21 Phase 1) — the builder-UI alternative to a
/// hand-typed <see cref="WorkerInvocation.PermissionScope"/> string. Composable categories; each
/// <see cref="IPermissionGrantTranslator"/> translates the requested set into its own vendor's
/// actual flag syntax, refusing rather than approximating whenever it cannot express the request
/// exactly in either direction — granting more than asked is exactly as wrong as granting less.
/// <para>
/// Precedence when both are set on the same <see cref="WorkerInvocation"/>/<see cref="WorkerBindingConfigEntry"/>:
/// a non-null <see cref="WorkerInvocation.PermissionGrant"/> always wins over
/// <see cref="WorkerInvocation.PermissionScope"/> — see those types' own docs. The bindings editor
/// UI never authors both on the same entry; this only matters for a hand-edited config file.
/// </para>
/// </summary>
/// <param name="ReadFiles">Grants reading files beyond the worker's declared contract inputs.</param>
/// <param name="WriteFiles">Grants creating and editing files.</param>
/// <param name="RunShellCommands">
/// Grants shell/tool command execution. When <paramref name="ShellCommandPatterns"/> is non-empty,
/// vendors that support pattern-scoped shell grants (e.g. Claude's <c>Bash(git:*)</c>) restrict to
/// those patterns; an empty list means "any command" — not every vendor can express the
/// pattern-scoped form (see each <see cref="IPermissionGrantTranslator"/>'s own notes).
/// </param>
/// <param name="ShellCommandPatterns">Command-pattern allowlist (e.g. <c>"git:*"</c>) — only meaningful when <see cref="RunShellCommands"/> is set.</param>
/// <param name="NetworkAccess">Grants outbound network access (web fetch/search tools).</param>
/// <param name="DeniedShellCommandPatterns">
/// 0022's <c>DenyAlways</c> rung (M-Phase-6 #390) — a standing "never" list in the same
/// <c>ShellCommandPatternMatcher</c> glob form as <see cref="ShellCommandPatterns"/>, but subtractive:
/// a match here is refused regardless of <see cref="RunShellCommands"/> or a matching entry in
/// <see cref="ShellCommandPatterns"/>. A closed "no" is not reopened by a wider later grant. Enforced
/// next turn on both vendors — claude via <c>--disallowedTools Bash(pattern)</c>
/// (<c>ClaudeWorkerAdapter.BuildDisallowedTools</c>, which the CLI applies with precedence over
/// <c>--allowedTools</c>), agy via its <c>PreToolUse</c> hook's <c>IsDenied</c> check (agy has no
/// vendor flag that can express a command family). Written only by <c>RuntimePermissionGrantAmender</c>
/// when the operator answers the DenyAlways rung.
/// </param>
public sealed record PermissionGrant(
    bool ReadFiles = false,
    bool WriteFiles = false,
    bool RunShellCommands = false,
    IReadOnlyList<string>? ShellCommandPatterns = null,
    bool NetworkAccess = false,
    IReadOnlyList<string>? DeniedShellCommandPatterns = null)
{
    /// <summary>
    /// True when every category is unset — the structured equivalent of a blank
    /// <see cref="WorkerInvocation.PermissionScope"/> string, which callers collapse to
    /// <see langword="null"/> rather than persisting an explicit "nothing" record (see
    /// <c>WorkerBindingEntryViewModel.TryBuildEntry</c>'s decision of record).
    /// </summary>
    public bool IsEmpty => !ReadFiles && !WriteFiles && !RunShellCommands && !NetworkAccess
        && (ShellCommandPatterns is null || ShellCommandPatterns.Count == 0)
        && (DeniedShellCommandPatterns is null || DeniedShellCommandPatterns.Count == 0);

    /// <summary>
    /// The categories this grant WITHHOLDS that a granted shell reaches anyway — empty when the
    /// grant is coherent. #529.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A granted shell reaches three of the four categories, and both places AER enforces a grant
    /// decide by tool <em>name</em>: <c>ClaudeWorkerAdapter.BuildDisallowedTools</c> emits
    /// <c>--disallowedTools</c>, and the <c>PreToolUse</c> hook check inspects the tool name. Neither
    /// can tell <c>Bash("cat x")</c> from <c>Read("x")</c>. The hook additionally reads a write's
    /// target path (#649), which exempts the outbox and reaches nothing inside a shell command. So a
    /// grant withholding any of these while granting the shell does not actually withhold it.
    /// </para>
    /// <para>
    /// <see cref="ShellCommandPatterns"/> is deliberately <em>not</em> an exemption. A pattern list
    /// only reaches the <c>--allowedTools</c> string, and
    /// <c>gate.allowedtools-is-preapproval-not-ceiling</c> measured that list to be pre-approval
    /// rather than a ceiling; the <c>--disallowedTools</c> side has no narrowed <c>Bash(…)</c> form
    /// at all. A pattern list changes what is pre-approved, never what is reachable.
    /// </para>
    /// <para>
    /// <b>This lives here, on the grant, because three surfaces need the same answer and #645 was
    /// filed about two of them getting it late or not at all.</b> The engine refuses at bind time
    /// (<c>WorkerBindingResolver</c>), which is the right choke point for execution and the wrong one
    /// for learning: an operator authoring in the bindings editor found out only when a workflow they
    /// had already committed to failed to start. A rule restated per surface is a rule that drifts on
    /// all but one of them, so every surface calls this and none re-derives the conditions.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CategoriesDefeatedByTheShell
    {
        get
        {
            if (!RunShellCommands)
            {
                return [];
            }

            List<string> withheld = [];
            if (!ReadFiles)
            {
                withheld.Add(nameof(ReadFiles));
            }

            if (!WriteFiles)
            {
                withheld.Add(nameof(WriteFiles));
            }

            if (!NetworkAccess)
            {
                withheld.Add(nameof(NetworkAccess));
            }

            return withheld;
        }
    }

}
