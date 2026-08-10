using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <param name="HeldWork">Every held-work reference this room has dispatched, by ref.</param>
/// <param name="UnmatchedEntries">
/// Journal entries that name a ref the projection does not know — an escalation or resolution
/// with no preceding dispatch (out-of-order, corrupted, or externally appended). Surfaced in
/// append order rather than silently dropped: the sole writer validates before appending, so a
/// non-empty list means the journal itself disagrees with its own history, and that is a state a
/// reader must be able to see (the 798 review's medium finding).
/// </param>
public sealed record RoomState(
    IReadOnlyDictionary<HeldWorkRef, HeldWorkState> HeldWork,
    IReadOnlyList<string> UnmatchedEntries,
    IReadOnlyDictionary<GrantId, GrantState>? ActiveGrants = null,
    IReadOnlyList<RoomEvent.EscalationRaised>? OpenEscalations = null,
    bool IsDormant = false,
    PendingPermission? PendingPermission = null)
{
    public IReadOnlyDictionary<GrantId, GrantState> ActiveGrants { get; init; } = ActiveGrants ?? new Dictionary<GrantId, GrantState>();

    /// <summary>
    /// Escalations raised and not yet resolved. NOTHING closes one yet — this slice is shapes
    /// only (#778 §D), and the resolution path (a decision answering the escalation) is future
    /// work; until it lands, "open" means "ever raised", not a live open/closed status.
    /// </summary>
    public IReadOnlyList<RoomEvent.EscalationRaised> OpenEscalations { get; init; } = OpenEscalations ?? [];

    public bool Equals(RoomState? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (HeldWork.Count != other.HeldWork.Count ||
            ActiveGrants.Count != other.ActiveGrants.Count ||
            OpenEscalations.Count != other.OpenEscalations.Count ||
            IsDormant != other.IsDormant ||
            !Equals(PendingPermission, other.PendingPermission) ||
            !UnmatchedEntries.SequenceEqual(other.UnmatchedEntries) ||
            !OpenEscalations.SequenceEqual(other.OpenEscalations))
        {
            return false;
        }

        foreach (var (key, value) in HeldWork)
        {
            if (!other.HeldWork.TryGetValue(key, out var otherValue) || !value.Equals(otherValue))
            {
                return false;
            }
        }

        foreach (var (key, value) in ActiveGrants)
        {
            if (!other.ActiveGrants.TryGetValue(key, out var otherValue) || !value.Equals(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsDormant);
        hash.Add(PendingPermission);

        foreach (var (key, value) in HeldWork.OrderBy(kv => kv.Key.Value))
        {
            hash.Add(key);
            hash.Add(value);
        }

        foreach (var (key, value) in ActiveGrants.OrderBy(kv => kv.Key.Value))
        {
            hash.Add(key);
            hash.Add(value);
        }

        foreach (var escalation in OpenEscalations)
        {
            hash.Add(escalation);
        }

        foreach (var entry in UnmatchedEntries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }
}

