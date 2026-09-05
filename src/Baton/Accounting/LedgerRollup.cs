using System.Text.Json.Serialization;

namespace Baton.Accounting;

/// <summary>
/// One vendor's — or, with a <see langword="null"/> <see cref="Vendor"/>, the whole selection's —
/// arithmetic over a set of cost-ledger rows (#1849 phase B).
/// </summary>
/// <remarks>
/// <b>A token dimension no row reported is ABSENT, not zero.</b> The sum is over the rows that
/// carried the dimension, and when none did there is no number — the same doctrine
/// <see cref="CostLedgerEntry"/> keeps per row, which would otherwise be destroyed by the first
/// addition: agy reports no cache-creation at all, and a <c>0</c> there would read as "agy created no
/// cache" rather than "agy does not report it".
/// </remarks>
/// <param name="Vendor">
/// The row's <see cref="CostLedgerEntry.Adapter"/>. <see langword="null"/> on the all-vendor total,
/// and also the grouping key for rows carrying no adapter at all — <see cref="LedgerRollup.UnknownVendor"/>
/// is what those group under, so "we do not know which vendor" is never silently merged into a named one.
/// </param>
/// <param name="Attempts">
/// Rows in this subtotal — <b>every</b> row, priced or not. An unpriced row is counted here and
/// disclosed in <see cref="ApiEquivalentUnpriced"/>; it is never dropped to make a cost total look
/// tidy. <see cref="ApiEquivalentPriced"/> + <see cref="ApiEquivalentUnpriced"/> equals this, always.
/// </param>
/// <param name="Partial">
/// How many of <paramref name="Attempts"/> carry <see cref="CostCompleteness.Partial"/> — i.e. the
/// stream reader could not establish that the row holds the whole attempt's usage. Read a subtotal
/// with a nonzero count here as a floor on what was spent, not a measurement of it.
/// </param>
/// <param name="Unread">
/// How many of <paramref name="Attempts"/> carry NO completeness label at all: nothing was read for
/// them (no parser for the adapter, no captured stream). Distinct from <paramref name="Partial"/> on
/// purpose — <see cref="CostLedgerStore.ResolveCompleteness"/> states the three-state split.
/// </param>
public sealed record LedgerSubtotal(
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Vendor,
    [property: JsonPropertyName("attempts")]
    int Attempts,
    [property: JsonPropertyName("partial")]
    int Partial,
    [property: JsonPropertyName("unread")]
    int Unread,
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut,
    [property: JsonPropertyName("cacheRead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens,
    [property: JsonPropertyName("cacheCreation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens,
    /// <summary>Sum of the rows that HAVE an API-equivalent estimate. An estimate at list price, never an invoice and never subscription spend.</summary>
    [property: JsonPropertyName("apiEquivalentUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? ApiEquivalentUsd,
    [property: JsonPropertyName("apiEquivalentPriced")]
    int ApiEquivalentPriced,
    [property: JsonPropertyName("apiEquivalentUnpriced")]
    int ApiEquivalentUnpriced,
    /// <summary>Sum of the rows that HAVE a plan-meter estimate. Also an estimate; also never a quota reading.</summary>
    [property: JsonPropertyName("planMeterEstimateUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? PlanMeterEstimateUsd,
    [property: JsonPropertyName("planMeterPriced")]
    int PlanMeterPriced,
    [property: JsonPropertyName("planMeterUnpriced")]
    int PlanMeterUnpriced);

/// <summary>
/// <b>The one accounting projection</b> (#1849 phase B, operator ruling 2026-09-05): the arithmetic
/// behind every cost-ledger view — room and fleet, text, JSON and CSV — lives here, and each surface
/// formats what this returns rather than summing rows of its own. A room view IS the fleet view with
/// <see cref="LedgerQuery.Room"/> set; there is no second code path for it, which is what makes the
/// two answers incapable of disagreeing.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Total"/> is computed over the rows, never by adding <see cref="Vendors"/> up.</b>
/// Summing subtotals would have to invent an answer for "absent in one vendor, present in another",
/// and the two arithmetics would then be free to drift — the exact thing this type exists to prevent.
/// </para>
/// <para>
/// <b>Determinism is a promise of this type, not of its callers</b> (#1849's acceptance criterion:
/// the same window over the same file yields the same totals). <see cref="Rows"/> is ordered by
/// <c>endedAt</c>, then execution id, then the row's position in the file — a total order even for
/// two undated rows with no execution id, which <see cref="CostLedgerStore.AppendAsync"/> explicitly
/// permits. Undated rows sort last rather than first, which is <see cref="DateTime.MaxValue"/>'s job
/// below: LINQ's ordering puts a null FIRST, and "unknown when" reading as "earliest" would put it at
/// the top of every drill-down.
/// </para>
/// </remarks>
/// <param name="Query">
/// The selection these totals are over, echoed back — including
/// <see cref="LedgerQuery.UndatedExcluded"/>, which this method fills.
/// </param>
/// <param name="Vendors">
/// Per-vendor subtotals, ordered by vendor name; the unknown-vendor group sorts last so a named
/// vendor's position never depends on whether an unlabelled row happened to be in the window.
/// </param>
/// <param name="Rows">
/// The contributing rows, in the order above — <see langword="null"/> unless the caller asked for
/// them (<c>--drill</c>). Absent rather than empty, so "not requested" and "none matched" stay
/// distinguishable in the JSON.
/// </param>
public sealed record LedgerRollup(
    [property: JsonPropertyName("query")] LedgerQuery Query,
    [property: JsonPropertyName("vendors")] IReadOnlyList<LedgerSubtotal> Vendors,
    [property: JsonPropertyName("total")] LedgerSubtotal Total,
    [property: JsonPropertyName("rows")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CostLedgerEntry>? Rows)
{
    /// <summary>The <see cref="LedgerSubtotal.Vendor"/> rows with no adapter group under. A literal, so a vendor that ever calls itself this cannot collide silently — no adapter is named this.</summary>
    public const string UnknownVendor = "(unknown)";

    /// <summary>
    /// Filters <paramref name="entries"/> by <paramref name="query"/>, orders what survives, and rolls
    /// it up per vendor and once overall.
    /// </summary>
    /// <param name="includeRows">Whether to carry the contributing rows in <see cref="Rows"/> (<c>--drill</c>).</param>
    public static LedgerRollup Build(
        IReadOnlyList<CostLedgerEntry> entries, LedgerQuery query, bool includeRows = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);

        var matched = new List<(CostLedgerEntry Entry, int FileOrder)>();
        var undatedExcluded = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (query.Matches(entry))
            {
                matched.Add((entry, i));
                continue;
            }

            // Counted, not silently dropped: a row this window could not place is the difference
            // between a windowed total and a complete one, and only this branch can see it.
            if (entry.EndedAt is null && !query.TimeMatches(entry) && MatchesIgnoringTime(query, entry))
            {
                undatedExcluded++;
            }
        }

        var ordered = matched
            .OrderBy(m => m.Entry.EndedAt ?? DateTime.MaxValue)
            .ThenBy(m => m.Entry.Execution ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.FileOrder)
            .Select(m => m.Entry)
            .ToList();

        var vendors = ordered
            .GroupBy(e => e.Adapter is { Length: > 0 } adapter ? adapter : UnknownVendor, StringComparer.Ordinal)
            .OrderBy(g => g.Key == UnknownVendor ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Summarize(g.Key, g.ToList()))
            .ToList();

        return new LedgerRollup(
            query with { UndatedExcluded = undatedExcluded },
            vendors,
            Summarize(null, ordered),
            includeRows ? ordered : null);
    }

    /// <summary>
    /// Every facet except the time window. Used only to decide whether an undated row was excluded
    /// BY THE WINDOW — a row a facet already rejected is not a casualty of the time filter and must
    /// not inflate <see cref="LedgerQuery.UndatedExcluded"/>.
    /// </summary>
    private static bool MatchesIgnoringTime(LedgerQuery query, CostLedgerEntry entry) =>
        (query with { Since = null, Until = null }).Matches(entry);

    private static LedgerSubtotal Summarize(string? vendor, IReadOnlyList<CostLedgerEntry> rows) =>
        new(
            Vendor: vendor,
            Attempts: rows.Count,
            Partial: rows.Count(r => r.Completeness == CostCompleteness.Partial),
            Unread: rows.Count(r => r.Completeness is null),
            TokensIn: SumPresent(rows, r => r.TokensIn),
            TokensOut: SumPresent(rows, r => r.TokensOut),
            CacheReadTokens: SumPresent(rows, r => r.CacheReadTokens),
            CacheCreationTokens: SumPresent(rows, r => r.CacheCreationTokens),
            ThinkingTokens: SumPresent(rows, r => r.ThinkingTokens),
            ApiEquivalentUsd: SumPresent(rows, r => r.ApiEquivalentUsd),
            ApiEquivalentPriced: rows.Count(r => r.ApiEquivalentUsd is not null),
            ApiEquivalentUnpriced: rows.Count(r => r.ApiEquivalentUsd is null),
            PlanMeterEstimateUsd: SumPresent(rows, r => r.PlanMeterEstimateUsd),
            PlanMeterPriced: rows.Count(r => r.PlanMeterEstimateUsd is not null),
            PlanMeterUnpriced: rows.Count(r => r.PlanMeterEstimateUsd is null));

    /// <summary>Sum over the rows that HAVE the value, or <see langword="null"/> when none does — see the type remarks for why that is not zero.</summary>
    private static long? SumPresent(IReadOnlyList<CostLedgerEntry> rows, Func<CostLedgerEntry, long?> select)
    {
        long total = 0;
        var any = false;
        foreach (var row in rows)
        {
            if (select(row) is { } value)
            {
                total += value;
                any = true;
            }
        }

        return any ? total : null;
    }

    private static decimal? SumPresent(IReadOnlyList<CostLedgerEntry> rows, Func<CostLedgerEntry, decimal?> select)
    {
        decimal total = 0m;
        var any = false;
        foreach (var row in rows)
        {
            if (select(row) is { } value)
            {
                total += value;
                any = true;
            }
        }

        return any ? total : null;
    }
}
