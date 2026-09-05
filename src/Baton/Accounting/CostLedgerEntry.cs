using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Accounting;

/// <summary>
/// Where a cost-ledger row came from — a closed set, so "Baton-launched only" is a trivial filter
/// rather than an inference from which fields happen to be populated (#1849's own requirement).
/// </summary>
/// <remarks>
/// Only <see cref="BatonExecution"/> has a writer today. The other three are phase C's importers of
/// the vendors' own native session logs, present here from day one so a phase-A row is already
/// labelled against them rather than needing a schema migration to say what it always was.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CostSourceKind>))]
public enum CostSourceKind
{
    [JsonStringEnumMemberName("baton-execution")] BatonExecution,
    [JsonStringEnumMemberName("claude-code-session")] ClaudeCodeSession,
    [JsonStringEnumMemberName("codex-session")] CodexSession,
    [JsonStringEnumMemberName("antigravity-session")] AntigravitySession,
}

/// <summary>
/// Whether a dollar figure on a row is an estimate, and if not, why not. Never an invoice, never a
/// quota reading — the field names say "estimate" and nothing on the row says otherwise (#1849).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EstimateStatus>))]
public enum EstimateStatus
{
    /// <summary>A number was produced from the cited catalog/factor-table version.</summary>
    [JsonStringEnumMemberName("estimated")] Estimated,

    /// <summary>The catalog has no price for this model (or for a dimension this usage reports). Never borrowed from a neighbouring model.</summary>
    [JsonStringEnumMemberName("unpriced")] Unpriced,

    /// <summary>A factor that applies here exists but has no measured value — see <see cref="PlanFactorStatus.Unknown"/>.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,

    /// <summary>This vendor's plan meter has never been measured, so no plan-meter estimate is even attempted.</summary>
    [JsonStringEnumMemberName("unmeasured")] Unmeasured,
}

/// <summary>How much of the attempt this row actually accounts for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CostCompleteness>))]
public enum CostCompleteness
{
    /// <summary>The whole stream was readable; the token dimensions present are the whole attempt's.</summary>
    [JsonStringEnumMemberName("complete")] Complete,

    /// <summary>Some of the attempt's usage is provably not in this row — <see cref="CostLedgerEntry.CompletenessReason"/> says which.</summary>
    [JsonStringEnumMemberName("partial")] Partial,
}

/// <summary>
/// One immutable accounting row per <b>settled execution attempt</b> (#1849 phase A). Consumes the
/// per-execution burn ledger's own source (<c>QuotaLedgerStore</c>, spec/baton.md §7) rather than
/// replacing it: <c>quota-ledger.jsonl</c> stays the per-execution record, and this is the durable,
/// repository-keyed, price-versioned accounting substrate #1391 (drill-down) and #1848 (enforcement)
/// read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every unavailable dimension is omitted, never zero</b> — the same doctrine
/// <c>WorkerUsage</c>/<c>ExecutionUsageView</c>/<c>QuotaLedgerEntry</c> already keep, extended here
/// rather than re-argued. A reader must never be able to tell "the vendor reported nothing" apart
/// from "the vendor reported zero" by accident; absence is the only spelling of the former.
/// </para>
/// <para>
/// <b>Fields reserved with no phase-A writer.</b> <see cref="Effort"/>, <see cref="Issue"/>,
/// <see cref="PullRequest"/>, <see cref="ParentRoom"/>, <see cref="Workstream"/> and
/// <see cref="Raw"/> are named here but never populated by <see cref="CostLedgerStore.BuildEntries"/>:
/// none of them is derivable from the events a settle already has in hand, and #1849's telemetry
/// checklist wants the NAME pinned now so a later phase fills a reserved field rather than inventing
/// a competing one. Absent for the same reason every other unavailable dimension is absent.
/// <see cref="Raw"/> in particular is for the vendor's own billed/usage fields <i>verbatim</i>; the
/// vendor parsers reduce their envelope to <c>WorkerUsage</c> and discard the rest, so capturing it
/// verbatim is phase C's work (where whole session logs are read), not something phase A can fake
/// out of Baton-derived arithmetic. What Baton DID derive from the vendor's own figures is on
/// <see cref="BilledTokens"/>/<see cref="LiveBilledTokens"/>/<see cref="BilledUnderReadTokens"/>/<see cref="PeakBilledInWindow"/>,
/// under their own names, so nothing derived is ever mistaken for something raw.
/// </para>
/// </remarks>
/// <param name="Role">
/// Baton's worker name for the step (<c>ExecutionRequest.Worker</c>) — the role the telemetry
/// checklist asks for. One field, not two: Baton has no separate role concept for a workflow step.
/// </param>
/// <param name="Attempt">
/// Reserved ordinal. A retry or redispatch mints a FRESH <c>ExecutionId</c>
/// (<c>MutationInterface</c>'s <c>Guid.NewGuid</c> per dispatch), so <see cref="Execution"/> alone
/// already distinguishes attempts and is what the writer dedupes on; this field exists for a later
/// phase to record lineage ordering, and is absent until one does.
/// </param>
public sealed record CostLedgerEntry(
    [property: JsonPropertyName("sourceKind")]
    CostSourceKind SourceKind,
    [property: JsonPropertyName("repository")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Repository = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null,
    [property: JsonPropertyName("parentRoom")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentRoom = null,
    [property: JsonPropertyName("workstream")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workstream = null,
    [property: JsonPropertyName("workflow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Workflow = null,
    [property: JsonPropertyName("step")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Step = null,
    [property: JsonPropertyName("execution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Execution = null,
    [property: JsonPropertyName("attempt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Attempt = null,
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Effort = null,
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome = null,
    [property: JsonPropertyName("issue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Issue = null,
    [property: JsonPropertyName("pr")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PullRequest = null,
    [property: JsonPropertyName("startedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? StartedAt = null,
    [property: JsonPropertyName("endedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? EndedAt = null,

    // Token dimensions, exactly as QuotaLedgerEntry carries them -- same names, same nullability, so a
    // reader that already understands quota-ledger.jsonl needs no second vocabulary.
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn = null,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut = null,
    [property: JsonPropertyName("cacheRead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheCreation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens = null,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Turns = null,
    [property: JsonPropertyName("wallClockMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? WallClockMs = null,

    // The vendor-derived billed figures ExecutionUsageView already owns the definitions of -- carried
    // through under the same names rather than recomputed, so #1706's reconciliation triple means one
    // thing in both files.
    [property: JsonPropertyName("billedTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledTokens = null,
    [property: JsonPropertyName("liveBilledTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? LiveBilledTokens = null,
    [property: JsonPropertyName("billedUnderReadTokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? BilledUnderReadTokens = null,
    [property: JsonPropertyName("peakBilledInWindow")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? PeakBilledInWindow = null,
    [property: JsonPropertyName("raw")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Raw = null,

    [property: JsonPropertyName("completeness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CostCompleteness? Completeness = null,
    /// <summary>
    /// Why <see cref="Completeness"/> is <see cref="CostCompleteness.Partial"/> — the same strings
    /// <c>ExecutionUsageView.BilledReconciliationUnavailable</c> already emits
    /// (<c>stream-truncated-by-rollover</c>, <c>rollover-segment-unreadable</c>), carried through
    /// verbatim rather than re-spelled.
    /// </summary>
    [property: JsonPropertyName("completenessReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CompletenessReason = null,

    /// <summary>The vendor's API list-price equivalent. An ESTIMATE for comparison, never an invoice.</summary>
    [property: JsonPropertyName("apiEquivalentUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? ApiEquivalentUsd = null,
    [property: JsonPropertyName("estimateStatus")]
    EstimateStatus EstimateStatus = EstimateStatus.Unpriced,
    /// <summary>
    /// The same token dimensions re-weighted by the plan-factor table — what the SUBSCRIPTION meter is
    /// believed to charge, as distinct from list price. Also an estimate; also never a quota reading.
    /// </summary>
    [property: JsonPropertyName("planMeterEstimateUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? PlanMeterEstimateUsd = null,
    [property: JsonPropertyName("planMeterEstimateStatus")]
    EstimateStatus PlanMeterEstimateStatus = EstimateStatus.Unpriced,

    // The four provenance stamps that make an estimate reproducible -- PriceCatalog's own remarks state
    // the guarantee they buy, which is #1849's acceptance criterion "price-catalog changes do not
    // retroactively rewrite prior estimated totals".
    [property: JsonPropertyName("priceCatalogId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PriceCatalogId = null,
    [property: JsonPropertyName("priceCatalogVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PriceCatalogVersion = null,
    [property: JsonPropertyName("planFactorTableId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlanFactorTableId = null,
    [property: JsonPropertyName("planFactorTableVersion")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlanFactorTableVersion = null);
