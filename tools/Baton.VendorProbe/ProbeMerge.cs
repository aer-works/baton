namespace Baton.VendorProbe;

/// <summary>
/// Carries the findings a narrowed run did not re-probe (#647).
/// </summary>
/// <remarks>
/// <para>
/// <c>--vendor agy</c> probes one vendor, and the generated matrix is written from that run's
/// findings alone — so before this existed, narrowing dropped every row for the other vendor. The
/// lock file already merged, which made the truncation easy to miss: the free staleness check went
/// on reporting <c>claude</c> as current while the evidence matrix no longer mentioned it.
/// </para>
/// <para>
/// That is the suite's own founding error turned on itself. A row absent from the matrix reads as
/// <em>this vendor cannot do it</em>, not as <em>this vendor was not looked at</em>, which is exactly
/// the reading <see cref="Finding.Absent"/> throws to prevent everywhere else.
/// </para>
/// <para>
/// Carried rows are <b>not</b> re-verified by being carried. They keep the version they were
/// established against, which is what makes a merged matrix readable: a column header naming an
/// older version is the signal that those rows are older, and the staleness check is what decides
/// whether that matters.
/// </para>
/// </remarks>
public static class ProbeMerge
{
    /// <summary>
    /// <paramref name="fresh"/> plus every finding in <paramref name="previous"/> whose vendor this
    /// run did not probe. A vendor present in <paramref name="probedVendors"/> is replaced wholesale
    /// rather than row-merged: a capability the probe no longer emits has been deliberately removed,
    /// and resurrecting it from a stale file would republish a finding nothing established.
    /// </summary>
    /// <param name="previous">Findings already on disk, or empty when there is no prior file.</param>
    /// <param name="fresh">What this run established.</param>
    /// <param name="probedVendors">The vendors this run actually probed.</param>
    public static IReadOnlyList<Finding> Carry(
        IReadOnlyList<Finding> previous,
        IReadOnlyList<Finding> fresh,
        IReadOnlyCollection<string> probedVendors)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(probedVendors);

        // Carried first, so a vendor that was in the file before keeps its column position. The
        // matrix takes both its column order and its row order from this sequence, and a re-probe of
        // one vendor reordering the whole table would make every diff unreadable.
        var carried = previous.Where(f => !probedVendors.Contains(f.Vendor));
        return [.. carried, .. fresh];
    }
}
