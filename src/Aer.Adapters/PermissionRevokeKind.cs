namespace Aer.Adapters;

/// <summary>
/// What a revocation takes back — the counterpart vocabulary to <see cref="PermissionDecisionKind"/>'s
/// persisting rungs, one canonical definition rather than literals restated at each caller.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately <b>no kind for lifting a standing refusal</b>. Removing an allow and removing
/// a deny are not the same operation and must not share one undifferentiated "remove": a
/// <see cref="PermissionGrant.DeniedShellCommandPatterns"/> entry is subtractive and its own field
/// documents that "a closed 'no' is not reopened by a wider later grant". If revocation could clear it,
/// revocation would become exactly the route back in that the deny exists to close. Lifting a refusal
/// is a separate operation with its own deliberate gesture, and it is not built here.
/// </para>
/// <para>
/// Nor is there a kind for <see cref="PermissionDecisionKind.AllowOnce"/> or
/// <see cref="PermissionDecisionKind.Deny"/>: nothing persists for those, so there is nothing to take
/// back. <see cref="PermissionDecisionKind.AllowCommandAnyRoom"/> is the same — it persists nothing
/// today, and giving it a revoke kind would imply a cross-room grant this codebase does not have.
/// </para>
/// </remarks>
public static class PermissionRevokeKind
{
    /// <summary>
    /// Takes back <see cref="PermissionDecisionKind.AllowRoom"/> — the room-wide shell permission.
    /// Clears <see cref="PermissionGrant.RunShellCommands"/> <em>and</em>
    /// <see cref="PermissionGrant.ShellCommandPatterns"/>: with the shell withdrawn a leftover
    /// allowlist grants nothing, and leaving it would have it silently spring back into force the day
    /// anything set the boolean again.
    /// </summary>
    public const string RoomShell = "RoomShell";

    /// <summary>
    /// Takes back one <see cref="PermissionDecisionKind.AllowCommandInRoom"/> — a single family from
    /// <see cref="PermissionGrant.ShellCommandPatterns"/>, named by its pattern. The rest of the list,
    /// and <see cref="PermissionGrant.RunShellCommands"/> itself, are left exactly as they were.
    /// </summary>
    public const string CommandInRoom = "CommandInRoom";
}
