namespace Baton.Domain;

/// <summary>
/// A room's worker as an identity of its own, decoupled from vendor/model/effort (0054 §1, #1305).
/// Room-scoped and unique; auto-named on join ("claude", then "claude-2" for a second worker on the
/// same vendor) and user-renamable thereafter. Vendor, <see cref="Model"/>, and <see cref="Effort"/>
/// are mutable properties of this identity, not the identity itself — swapping a participant's model
/// does not change who the transcript says was talking. <see cref="IsOrchestrator"/> is 0054 §6's
/// room-object property, carried here rather than as a separate room-level field, since exactly one
/// participant holds it at a time.
/// </summary>
public sealed record Participant(
    WorkerId Id,
    string Name,
    string Vendor,
    string? Model,
    string? Effort,
    bool IsOrchestrator);

/// <summary>
/// 0054 §1's auto-naming rule: the first worker of a vendor is named after the vendor; a second
/// worker of the same vendor becomes "vendor-2", a third "vendor-3", and so on — the same room can
/// hold two claude-sonnets as two distinct participants because their names, not their vendor
/// strings, are the identity.
/// </summary>
public static class ParticipantNaming
{
    /// <summary>The next auto-generated name for a new participant of <paramref name="vendor"/>, given the names already taken in this room.</summary>
    public static string NextName(string vendor, IEnumerable<string> existingNames)
    {
        ArgumentException.ThrowIfNullOrEmpty(vendor);
        ArgumentNullException.ThrowIfNull(existingNames);

        var taken = new HashSet<string>(existingNames, StringComparer.Ordinal);
        if (!taken.Contains(vendor))
        {
            return vendor;
        }

        var suffix = 2;
        while (taken.Contains($"{vendor}-{suffix}"))
        {
            suffix++;
        }

        return $"{vendor}-{suffix}";
    }
}
