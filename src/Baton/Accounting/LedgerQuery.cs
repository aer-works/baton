using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Accounting;

/// <summary>
/// The selection half of a cost-ledger view (#1849 phase B): which rows of a repository's ledger a
/// room or fleet reading is over. Serialized verbatim as the <c>query</c> member of
/// <see cref="LedgerRollup"/>, so a consumer reading the JSON (#1746's glass, #1848's enforcement)
/// can always tell what a total is a total OF rather than inferring it from the invocation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The time filter is on <see cref="CostLedgerEntry.EndedAt"/></b> — the instant an attempt is
/// attributed to, which is also the instant its estimate was priced at
/// (<see cref="CostLedgerStore.BuildEntries"/>). <see cref="Since"/> is inclusive and
/// <see cref="Until"/> is exclusive, so two adjacent windows partition a day rather than
/// double-counting the row on the boundary. Both are UTC; the CLI is where an operator's local-time
/// shorthand is converted.
/// </para>
/// <para>
/// <b>A row with no <c>endedAt</c> is excluded by any time filter, never assumed into the window.</b>
/// That is the ledger's own doctrine (absence is not zero) applied to time, and it is counted rather
/// than dropped silently: <see cref="UndatedExcluded"/> carries how many, so a windowed total is
/// never mistaken for a complete one.
/// </para>
/// </remarks>
/// <param name="UndatedExcluded">
/// Filled by <see cref="LedgerRollup.Build"/>, never supplied by a caller — how many rows a time
/// filter dropped for having no <see cref="CostLedgerEntry.EndedAt"/>. Always written, including as
/// <c>0</c>: "none were dropped" is a fact a consumer needs, and an absent field would be read as
/// "unknown".
/// </param>
/// <param name="Room">
/// A <see cref="BatonPaths.RecordKey"/>, not a raw command-line path — the caller normalizes, because
/// that method throws on a malformed path and this type is pure comparison. Matched with
/// <see cref="BatonPaths.RecordKeyComparer"/>, so a room view is exactly the fleet view filtered here.
/// </param>
/// <param name="Project">
/// <c>--project</c>, matched against a row's <see cref="CostLedgerEntry.Repository"/> — the only
/// field on a row that means "which project this work belongs to". <b>Degenerate today, deliberately
/// kept:</b> one ledger file holds exactly one repository identity by construction, so this facet
/// currently matches either every row or none. It earns its keep once phase C imports native session
/// logs and #1848 reads across repositories; shipping the name now means neither invents a second one.
/// </param>
public sealed record LedgerQuery(
    [property: JsonPropertyName("since")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? Since = null,
    [property: JsonPropertyName("until")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? Until = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Vendor = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role = null,
    [property: JsonPropertyName("repository")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Project = null,
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome = null,
    [property: JsonPropertyName("workflow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workflow = null,
    [property: JsonPropertyName("pr")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PullRequest = null,
    [property: JsonPropertyName("issue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Issue = null,
    [property: JsonPropertyName("sourceKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CostSourceKind? SourceKind = null,
    [property: JsonPropertyName("undatedExcluded")]
    int UndatedExcluded = 0)
{
    /// <summary>Whether <paramref name="entry"/> is in this view — every facet ANDed, an unset facet matching everything.</summary>
    public bool Matches(CostLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (SourceKind is { } kind && entry.SourceKind != kind)
        {
            return false;
        }

        // BatonPaths.RecordKeyComparer, never Ordinal: a row records BatonPaths.RecordKey(path), and a
        // room named on the command line in another casing (or with a trailing separator) is the SAME
        // room. Ordinal here would silently return an empty room view instead.
        if (Room is { Length: > 0 } room && !BatonPaths.RecordKeyComparer.Equals(entry.Room ?? string.Empty, room))
        {
            return false;
        }

        return FacetMatches(Vendor, entry.Adapter)
            && FacetMatches(Model, entry.Model)
            && FacetMatches(Role, entry.Role)
            && FacetMatches(Project, entry.Repository)
            && FacetMatches(Outcome, entry.Outcome)
            && FacetMatches(Workflow, entry.Workflow)
            && FacetMatches(NormalizeNumberReference(PullRequest), NormalizeNumberReference(entry.PullRequest))
            && FacetMatches(NormalizeNumberReference(Issue), NormalizeNumberReference(entry.Issue))
            && TimeMatches(entry);
    }

    /// <summary>
    /// Whether the time window admits <paramref name="entry"/>. A row with no <c>endedAt</c> fails
    /// this whenever a bound is set — see the type remarks; <see cref="LedgerRollup.Build"/> counts
    /// those separately rather than letting them vanish.
    /// </summary>
    public bool TimeMatches(CostLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (Since is null && Until is null)
        {
            return true;
        }

        if (entry.EndedAt is not { } endedAt)
        {
            return false;
        }

        var utc = ToUtc(endedAt);
        return (Since is not { } since || utc >= ToUtc(since))
            && (Until is not { } until || utc < ToUtc(until));
    }

    /// <summary>
    /// A <c>Kind.Unspecified</c> instant is read as UTC, not as local time: every instant this ledger
    /// writes is <c>WriterUtcTimestamp</c>, and a JSON round-trip is the one place the kind can be
    /// lost. Guessing "local" there would shift a whole window by the machine's offset.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static bool FacetMatches(string? filter, string? value) =>
        filter is not { Length: > 0 } || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>#1849</c>, <c>1849</c> and <c>1849</c>-with-whitespace are one issue. The ledger's own
    /// <c>issue</c>/<c>pr</c> fields are strings with no phase-A writer, so no spelling is settled yet
    /// and both sides are normalized rather than one side being declared canonical.
    /// </summary>
    private static string? NormalizeNumberReference(string? value) =>
        value is { Length: > 0 } ? value.Trim().TrimStart('#') : value;
}
