namespace Baton.Vendors;

/// <summary>
/// The permission-ladder rungs 04:82-94 defines (decision 0022) — the vocabulary a historical
/// <c>RoomEvent.RuntimePermissionAnswered</c>/<see cref="Baton.Projection.PermissionAnswer"/>'s
/// <c>decisionKind</c> string was drawn from, back when a mid-lane runtime ask could be answered.
/// That machinery was retired (#1417) — see spec/baton.md §5 for the ruling. This vocabulary
/// survives only to make sense of the historical string values already recorded in an existing
/// room's journal.
/// </summary>
public static class PermissionDecisionKind
{
    /// <summary>Just this once — nothing persisted; only the one held call was released.</summary>
    public const string AllowOnce = "AllowOnce";

    /// <summary>
    /// Any <em>this command</em> in this room — historically persisted a
    /// <see cref="PermissionGrant.ShellCommandPatterns"/> entry scoped to the asked command's family
    /// on the room's chat-worker binding.
    /// </summary>
    public const string AllowCommandInRoom = "AllowCommandInRoom";

    /// <summary>Any command in this room — historically persisted unscoped <see cref="PermissionGrant.RunShellCommands"/>.</summary>
    public const string AllowRoom = "AllowRoom";

    /// <summary>
    /// Any <em>this command</em> in any room — needed decision 0034's cross-room global store, which
    /// was never built. Answering with this kind persisted nothing (fell back to <see cref="AllowOnce"/>
    /// behavior) rather than faking per-room persistence for a cross-room promise.
    /// </summary>
    public const string AllowCommandAnyRoom = "AllowCommandAnyRoom";

    /// <summary>
    /// Never / always deny <em>this command</em> — the standing refusal. Historically persisted the
    /// asked command's family to <see cref="PermissionGrant.DeniedShellCommandPatterns"/>; see that
    /// field for how each vendor enforces it.
    /// </summary>
    public const string DenyAlways = "DenyAlways";

    /// <summary>Deny once — nothing persisted; the <c>Reason</c> carried the message (0022 §3).</summary>
    public const string Deny = "Deny";
}
