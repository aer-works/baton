using Baton.Accounting;
using Baton.Status;

namespace Baton.Tests.Accounting;

/// <summary>
/// #1849 phase B: the ONE accounting projection every cost-ledger view formats. Phase A's tests own
/// stream→row; this file owns row→rollup, so its fixture is literal rows rather than synthesized
/// vendor streams — the ordering, absence and byte-identity claims here are about the arithmetic, and
/// a stream fixture would only put a second failure mode between the assertion and the thing asserted.
/// </summary>
/// <remarks>
/// The fixture is deliberately the awkward population #1849 names: three adapters (the 2026-09-04
/// clarification's claude/agy/codex, not the two the issue opened with), an agy row with no
/// cache-creation dimension at all, an unpriced row, a partial row, a retry pair sharing one step, and
/// an undated row. Every assertion below is against that one population, so a change that fixes one
/// arm by breaking another cannot pass.
/// </remarks>
public sealed class LedgerRollupTests
{
    private static readonly DateTime Sep4 = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string RoomA = BatonPaths.RecordKey(Path.Combine(Path.GetTempPath(), "baton-1849b", "room-a"));
    private static readonly string RoomB = BatonPaths.RecordKey(Path.Combine(Path.GetTempPath(), "baton-1849b", "room-b"));

    /// <summary>claude, room A, the first attempt of wf1/implement. Every dimension present, both estimates priced.</summary>
    private static readonly CostLedgerEntry Claude1 = new(
        CostSourceKind.BatonExecution,
        Repository: "github.com/aer-works/baton",
        Room: RoomA,
        Workflow: "wf1",
        Step: "s1",
        Execution: "e1",
        Role: "implement",
        Adapter: "claude",
        Model: "claude-opus-5",
        Outcome: "Succeeded",
        Issue: "1849",
        PullRequest: "1883",
        EndedAt: Sep4.AddHours(10),
        TokensIn: 100,
        TokensOut: 50,
        CacheReadTokens: 5,
        CacheCreationTokens: 10,
        ThinkingTokens: 7,
        Completeness: CostCompleteness.Complete,
        ApiEquivalentUsd: 1.5m,
        EstimateStatus: EstimateStatus.Estimated,
        PlanMeterEstimateUsd: 0.5m,
        PlanMeterEstimateStatus: EstimateStatus.Estimated);

    /// <summary>The RETRY of Claude1's step — same workflow and step, a fresh execution id, and it failed.</summary>
    private static readonly CostLedgerEntry Claude2 = Claude1 with
    {
        Execution = "e2",
        Outcome = "Failed",
        EndedAt = Sep4.AddHours(11),
        TokensIn = 200,
        TokensOut = 20,
        CacheReadTokens = null,
        CacheCreationTokens = null,
        ThinkingTokens = null,
        ApiEquivalentUsd = 0.25m,
        PlanMeterEstimateUsd = 0.1m,
    };

    /// <summary>agy: reports thinking and cache-READ but has no cache-creation dimension, and its plan meter has never been measured.</summary>
    private static readonly CostLedgerEntry Agy = Claude1 with
    {
        Room = RoomB,
        Workflow = "wf2",
        Step = "s2",
        Execution = "e3",
        Role = "review",
        Adapter = "agy",
        Model = "gemini-3-pro",
        Issue = null,
        PullRequest = null,
        EndedAt = Sep4.AddHours(12),
        TokensIn = 300,
        TokensOut = 30,
        CacheReadTokens = 40,
        CacheCreationTokens = null,
        ThinkingTokens = 9,
        ApiEquivalentUsd = 2m,
        PlanMeterEstimateUsd = null,
        PlanMeterEstimateStatus = EstimateStatus.Unmeasured,
    };

    /// <summary>codex, and UNPRICED on both halves — the row that must still be counted as an attempt.</summary>
    private static readonly CostLedgerEntry Codex = Claude1 with
    {
        Room = RoomB,
        Workflow = "wf2",
        Step = "s3",
        Execution = "e4",
        Adapter = "codex",
        Model = "gpt-5-codex",
        Issue = null,
        PullRequest = null,
        EndedAt = Sep4.AddDays(1).AddHours(9),
        TokensIn = 400,
        TokensOut = 40,
        CacheReadTokens = null,
        CacheCreationTokens = null,
        ThinkingTokens = null,
        ApiEquivalentUsd = null,
        EstimateStatus = EstimateStatus.Unpriced,
        EstimateReason = "model-mismatch",
        PlanMeterEstimateUsd = null,
        PlanMeterEstimateStatus = EstimateStatus.Unpriced,
    };

    /// <summary>A capture the stream reader could not establish as whole. Priced, but a floor rather than a measurement.</summary>
    private static readonly CostLedgerEntry Partial = Claude1 with
    {
        Execution = "e5",
        Step = "s4",
        EndedAt = Sep4.AddHours(13),
        TokensIn = 50,
        TokensOut = null,
        CacheReadTokens = null,
        CacheCreationTokens = null,
        ThinkingTokens = null,
        Completeness = CostCompleteness.Partial,
        CompletenessReason = "no-terminal-billed-figure",
        ApiEquivalentUsd = 0.1m,
        PlanMeterEstimateUsd = 0.05m,
    };

    /// <summary>No <c>endedAt</c> at all: no window can place it, and it must be disclosed rather than assumed in or silently dropped.</summary>
    private static readonly CostLedgerEntry Undated = Claude1 with
    {
        Execution = "e6",
        Step = "s5",
        EndedAt = null,
        Completeness = null,
    };

    /// <summary>Deliberately NOT in endedAt order — the ordering assertions would be vacuous over a pre-sorted file.</summary>
    private static readonly IReadOnlyList<CostLedgerEntry> Ledger =
        [Codex, Claude2, Undated, Agy, Claude1, Partial];

    [Fact]
    public void Vendor_subtotals_come_first_and_are_ordered_with_the_unknown_vendor_last()
    {
        var withNoAdapter = Claude1 with { Execution = "e7", Adapter = null };
        var rollup = LedgerRollup.Build([.. Ledger, withNoAdapter], new LedgerQuery());

        Assert.Equal(
            ["agy", "claude", "codex", LedgerRollup.UnknownVendor],
            rollup.Vendors.Select(v => v.Vendor).ToArray());
    }

    [Fact]
    public void Each_vendor_subtotal_sums_only_its_own_rows()
    {
        var rollup = LedgerRollup.Build(Ledger, new LedgerQuery());

        var claude = rollup.Vendors.Single(v => v.Vendor == "claude");
        // e1 + e2 + e5 + e6 (the undated row is still a claude row when no window is set).
        Assert.Equal(4, claude.Attempts);
        Assert.Equal(100L + 200 + 50 + 100, claude.TokensIn!.Value);
        Assert.Equal(1.5m + 0.25m + 0.1m + 1.5m, claude.ApiEquivalentUsd!.Value);

        var agy = rollup.Vendors.Single(v => v.Vendor == "agy");
        Assert.Equal(1, agy.Attempts);
        Assert.Equal(300L, agy.TokensIn!.Value);
    }

    /// <summary>
    /// The gate against the CLI (or anything else) re-deriving the total by adding the subtotals up:
    /// this asserts the total against the ROWS. The discriminating half is cache-creation — present on
    /// claude's rows, absent on every agy and codex row — where a subtotal-summing implementation and
    /// a row-summing one only agree if absence is handled identically in both.
    /// </summary>
    [Fact]
    public void The_total_is_over_the_rows_and_counts_every_attempt_priced_or_not()
    {
        var rollup = LedgerRollup.Build(Ledger, new LedgerQuery());

        Assert.Equal(6, rollup.Total.Attempts);
        Assert.Equal(Ledger.Sum(r => r.TokensIn ?? 0), rollup.Total.TokensIn!.Value);
        Assert.Equal(Ledger.Sum(r => r.CacheCreationTokens ?? 0), rollup.Total.CacheCreationTokens!.Value);
        Assert.Equal(Ledger.Sum(r => r.ApiEquivalentUsd ?? 0m), rollup.Total.ApiEquivalentUsd!.Value);

        // The unpriced row is IN the attempt count and disclosed beside the money, never dropped.
        Assert.Equal(5, rollup.Total.ApiEquivalentPriced);
        Assert.Equal(1, rollup.Total.ApiEquivalentUnpriced);
        Assert.Equal(rollup.Total.Attempts, rollup.Total.ApiEquivalentPriced + rollup.Total.ApiEquivalentUnpriced);
        Assert.Equal(2, rollup.Total.PlanMeterUnpriced);
        Assert.Equal(1, rollup.Total.Partial);
        Assert.Equal(1, rollup.Total.Unread);
    }

    /// <summary>
    /// Absence survives addition. Polarity in both directions: agy reports no cache-creation and its
    /// subtotal has none, while its cache-READ subtotal is a number — an implementation that zero-filled
    /// would pass the second assertion and fail the first.
    /// </summary>
    [Fact]
    public void A_dimension_no_row_reported_is_absent_rather_than_zero()
    {
        var rollup = LedgerRollup.Build(Ledger, new LedgerQuery(Vendor: "agy"));

        var agy = Assert.Single(rollup.Vendors);
        Assert.Null(agy.CacheCreationTokens);
        Assert.Equal(40L, agy.CacheReadTokens!.Value);
        Assert.Null(agy.PlanMeterEstimateUsd);
        Assert.Equal(1, agy.PlanMeterUnpriced);
    }

    [Fact]
    public void A_retry_is_a_second_attempt_of_the_same_step_never_collapsed_into_one()
    {
        var rollup = LedgerRollup.Build(Ledger, new LedgerQuery(Workflow: "wf1"), includeRows: true);

        Assert.Equal(["e1", "e2", "e5", "e6"], rollup.Rows!.Select(r => r.Execution!).ToArray());
        Assert.Equal(2, rollup.Rows!.Count(r => r.Step == "s1"));
    }

    [Fact]
    public void Since_is_inclusive_and_until_is_exclusive_on_the_attempts_endedAt()
    {
        // e3's own endedAt, so the two arms differ by exactly the boundary row.
        var boundary = Sep4.AddHours(12);

        var inclusive = LedgerRollup.Build(Ledger, new LedgerQuery(Since: boundary), includeRows: true);
        Assert.Contains(inclusive.Rows!, r => r.Execution == "e3");

        var exclusive = LedgerRollup.Build(Ledger, new LedgerQuery(Until: boundary), includeRows: true);
        Assert.DoesNotContain(exclusive.Rows!, r => r.Execution == "e3");

        // The controls: without them both arms would also pass on filters that matched nothing at all.
        Assert.NotEmpty(exclusive.Rows!);
        Assert.NotEmpty(inclusive.Rows!);
    }

    [Fact]
    public void A_row_with_no_endedAt_is_excluded_by_a_window_and_counted_rather_than_dropped()
    {
        var windowed = LedgerRollup.Build(
            Ledger, new LedgerQuery(Since: Sep4, Until: Sep4.AddDays(1)), includeRows: true);

        Assert.Equal(["e1", "e2", "e3", "e5"], windowed.Rows!.Select(r => r.Execution!).ToArray());
        Assert.Equal(1, windowed.Query.UndatedExcluded);

        // Not counted when no window is set: the row is IN that reading, so nothing was excluded.
        Assert.Equal(0, LedgerRollup.Build(Ledger, new LedgerQuery()).Query.UndatedExcluded);

        // Not counted when another facet already rejected it -- an undated agy-only reading excludes
        // the undated CLAUDE row on the vendor, and blaming the window for it would overstate the loss.
        Assert.Equal(
            0,
            LedgerRollup.Build(Ledger, new LedgerQuery(Since: Sep4, Vendor: "agy")).Query.UndatedExcluded);
    }

    [Theory]
    [InlineData("vendor", "codex", "e4")]
    [InlineData("model", "gemini-3-pro", "e3")]
    [InlineData("role", "review", "e3")]
    [InlineData("outcome", "Failed", "e2")]
    [InlineData("workflow", "wf2", "e3,e4")]
    [InlineData("project", "github.com/aer-works/baton", "e1,e2,e3,e5,e4,e6")]
    [InlineData("pr", "#1883", "e1,e2,e5,e6")]
    [InlineData("issue", "1849", "e1,e2,e5,e6")]
    public void Each_facet_selects_exactly_its_own_rows(string facet, string value, string expectedExecutions)
    {
        var query = facet switch
        {
            "vendor" => new LedgerQuery(Vendor: value),
            "model" => new LedgerQuery(Model: value),
            "role" => new LedgerQuery(Role: value),
            "outcome" => new LedgerQuery(Outcome: value),
            "workflow" => new LedgerQuery(Workflow: value),
            "project" => new LedgerQuery(Project: value),
            "pr" => new LedgerQuery(PullRequest: value),
            "issue" => new LedgerQuery(Issue: value),
            _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, "unmapped facet"),
        };

        var rollup = LedgerRollup.Build(Ledger, query, includeRows: true);

        Assert.Equal(expectedExecutions.Split(','), rollup.Rows!.Select(r => r.Execution!).ToArray());
        Assert.Equal(rollup.Rows!.Count, rollup.Total.Attempts);
    }

    [Fact]
    public void The_source_kind_facet_selects_on_the_closed_label_not_on_which_fields_are_populated()
    {
        var imported = Claude1 with { Execution = "e8", SourceKind = CostSourceKind.ClaudeCodeSession };
        var rollup = LedgerRollup.Build(
            [.. Ledger, imported], new LedgerQuery(SourceKind: CostSourceKind.BatonExecution), includeRows: true);

        Assert.DoesNotContain(rollup.Rows!, r => r.Execution == "e8");
        Assert.Equal(6, rollup.Total.Attempts);
    }

    /// <summary>
    /// #1849's "room and fleet surfaces use one accounting projection". Not a tautology: the room
    /// argument here is spelled with the other separator and the other casing than the rows carry, which
    /// is exactly what an ordinal comparison would silently answer "no rows" to.
    /// </summary>
    [Fact]
    public void A_room_view_is_the_fleet_view_filtered_to_that_room_however_the_room_is_spelled()
    {
        var fleet = LedgerRollup.Build(Ledger, new LedgerQuery(), includeRows: true);
        var expected = fleet.Rows!.Where(r => BatonPaths.RecordKeyComparer.Equals(r.Room, RoomA)).ToList();

        var spelledDifferently = RoomA.ToUpperInvariant().Replace(Path.DirectorySeparatorChar, '/');
        var room = LedgerRollup.Build(
            Ledger, new LedgerQuery(Room: BatonPaths.RecordKey(spelledDifferently)), includeRows: true);

        Assert.Equal(expected.Select(r => r.Execution), room.Rows!.Select(r => r.Execution));
        Assert.Equal(4, room.Total.Attempts);
        Assert.Equal(expected.Sum(r => r.TokensIn ?? 0), room.Total.TokensIn!.Value);

        // The control arm: room B is a different, non-empty answer over the same ledger.
        var roomB = LedgerRollup.Build(Ledger, new LedgerQuery(Room: RoomB));
        Assert.Equal(2, roomB.Total.Attempts);
    }

    [Fact]
    public void Rows_are_ordered_by_endedAt_then_execution_id_with_undated_rows_last()
    {
        var sameInstant = Claude1 with { Execution = "e0", EndedAt = Sep4.AddHours(10) };
        var rollup = LedgerRollup.Build([.. Ledger, sameInstant], new LedgerQuery(), includeRows: true);

        Assert.Equal(["e0", "e1", "e2", "e3", "e5", "e4", "e6"], rollup.Rows!.Select(r => r.Execution!).ToArray());
    }

    /// <summary>
    /// The determinism criterion, and the control that makes it discriminate: the same rows in a
    /// DIFFERENT file order must produce the same reading, which a build that just echoed file order
    /// would fail.
    /// </summary>
    [Fact]
    public void The_same_query_over_the_same_rows_is_the_same_reading_whatever_order_they_arrive_in()
    {
        var query = new LedgerQuery(Since: Sep4, Until: Sep4.AddDays(2));

        var first = LedgerRollup.Build(Ledger, query, includeRows: true);
        var second = LedgerRollup.Build(Ledger.Reverse().ToList(), query, includeRows: true);

        Assert.Equal(first.Rows!.Select(r => r.Execution), second.Rows!.Select(r => r.Execution));
        Assert.Equal(first.Vendors, second.Vendors);
        Assert.Equal(first.Total, second.Total);
        Assert.Equal(first.Query, second.Query);
    }
}
