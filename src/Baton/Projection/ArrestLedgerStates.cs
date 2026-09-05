namespace Baton.Projection;

/// <summary>
/// The closed vocabulary for one room-side <c>cancel.request</c> record. Keep predicates over
/// arrest outcomes on these named sets rather than spelling status literals at call sites.
/// </summary>
public static class ArrestLedgerStates
{
    public const string Requested = "requested";
    public const string Delivered = "delivered";
    public const string Rejected = "rejected";
    public const string Expired = "expired";

    public static IReadOnlySet<string> Terminal { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered,
        Rejected,
        Expired,
    };

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Requested,
        Delivered,
        Rejected,
        Expired,
    };

    public static bool IsTerminal(string state) => Terminal.Contains(state);
}
