namespace Aer.Adapters;

/// <summary>
/// The permission-ladder rungs 04:82-94 defines (decision 0022), as answered on
/// <c>RoomMutationInterface.AnswerPermissionAsync</c>'s <c>decisionKind</c> string. One canonical
/// definition (record-once) — the daemon's answer path and the UI's dispatch both read these rather
/// than each restating the literals.
/// </summary>
/// <remarks>
/// Every allow rung's value MUST start with <c>"Allow"</c> —
/// <c>PermissionGateTool.BuildAnswerResult</c>'s contract (<c>decisionKind.StartsWith("Allow")</c>)
/// decides whether the held worker is released or refused, and a rung spelled otherwise would silently
/// fail closed for that worker even while this vocabulary calls it a grant.
/// </remarks>
public static class PermissionDecisionKind
{
    /// <summary>Just this once — nothing persists; only the one held call is released.</summary>
    public const string AllowOnce = "AllowOnce";

    /// <summary>
    /// Any <em>this command</em> in this room — persists a <see cref="PermissionGrant.ShellCommandPatterns"/>
    /// entry scoped to the asked command's family on the room's chat-worker binding.
    /// </summary>
    public const string AllowCommandInRoom = "AllowCommandInRoom";

    /// <summary>Any command in this room — persists unscoped <see cref="PermissionGrant.RunShellCommands"/>.</summary>
    public const string AllowRoom = "AllowRoom";

    /// <summary>
    /// Any <em>this command</em> in any room — needs decision 0034's cross-room global store, which is
    /// unbuilt. Answering with this kind persists nothing (falls back to <see cref="AllowOnce"/>
    /// behavior) rather than faking per-room persistence for a cross-room promise. See
    /// <c>RuntimePermissionGrantAmender</c>.
    /// </summary>
    public const string AllowCommandAnyRoom = "AllowCommandAnyRoom";

    /// <summary>
    /// Never / always deny <em>this command</em> — the standing refusal. Persists the asked command's
    /// family to <see cref="PermissionGrant.DeniedShellCommandPatterns"/>; see that field for how each
    /// vendor enforces it.
    /// </summary>
    public const string DenyAlways = "DenyAlways";

    /// <summary>Deny once — nothing persists; the <c>Reason</c> carries the message (0022 §3).</summary>
    public const string Deny = "Deny";
}
