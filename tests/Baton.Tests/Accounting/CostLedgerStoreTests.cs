using System.Text.Json;
using Baton.Accounting;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Accounting;

/// <summary>
/// #1849 phase A: the repository-keyed cost ledger. This file pins what the cost ledger ADDS over
/// <c>QuotaLedgerStoreTests</c>' burn ledger — the two labelled estimates and their provenance stamps,
/// the closed source-kind label, completeness, and the per-attempt/no-double-count semantics. Token
/// extraction itself is <c>ExecutionUsageProjector</c>'s, shared rather than re-derived, so it is not
/// re-pinned here beyond the cache/thinking asymmetry the two vendors actually differ on.
/// </summary>
public sealed class CostLedgerStoreTests
{
    private static readonly RepositoryIdentity Repository =
        RepositoryIdentity.From("https://github.com/aer-works/baton.git", null)!;

    private static readonly DateTime Start = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A real claude terminal `result` line's shape, copied from <c>ClaudeFinalUsageParsingTests</c>: all five dimensions plus turns.</summary>
    private const string ClaudeTerminalLine =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"num_turns":3,"result":"done","session_id":"s","total_cost_usd":0.0021,"usage":{"input_tokens":100,"output_tokens":50,"cache_creation_input_tokens":10,"cache_read_input_tokens":5,"output_tokens_details":{"thinking_tokens":7}}}""";

    /// <summary>An agy terminal `result` line, from <c>AgyFinalUsageParsingTests</c>. Note it reports thinking but has no cache-CREATION dimension at all — the asymmetry this file checks.</summary>
    private const string AgyTerminalLine =
        """{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"done","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}""";

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker, string? adapter, string? model) => new(
        executionId,
        new WorkflowId("wf-cost-ledger"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: adapter,
        Model: model);

    /// <summary>
    /// Writes the captured stdout the projector reads its usage out of, at exactly the path
    /// <see cref="ArtifactManager.ResolveOutputDirectory"/> addresses.
    /// </summary>
    private static void WriteCapturedStream(string roomDirectoryPath, ExecutionId executionId, string terminalLine, bool truncated = false)
    {
        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), terminalLine + "\n");
        if (truncated)
        {
            File.WriteAllText(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutTruncationMarkerFileName), "rolled over");
        }
    }

    private static List<LogEntry> SettledExecution(
        ExecutionId executionId, string adapter, string model, DateTime start, string worker = "implement") =>
    [
        new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, worker, adapter, model))),
        new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
        new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(2)),
        new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(executionId)),
    ];

    private static string NewRoom() => Path.Combine(Path.GetTempPath(), $"cost-ledger-{Guid.NewGuid():N}");

    private static string NewLedgerPath() =>
        Path.Combine(Path.GetTempPath(), $"cost-ledger-{Guid.NewGuid():N}.jsonl");

    /// <summary>
    /// A catalog that prices claude's five dimensions from 2026-01-01, with a SECOND range opening
    /// 2026-07-01 at a different input rate. Deliberately not the shipped catalog: <see cref="PriceCatalog.Default"/>
    /// prices nothing (see its remarks), which is what makes every production row unpriced today.
    /// </summary>
    private static PriceCatalog TwoRangeCatalog(string version, decimal earlyInputRate) => PriceCatalog.Parse($$"""
        {
          "id": "test-prices",
          "version": "{{version}}",
          "vendors": {
            "claude": {
              "claude-opus-5": {
                "input": [
                  {"effectiveFrom":"2026-01-01T00:00:00Z","effectiveTo":"2026-07-01T00:00:00Z","usdPerMillion":{{earlyInputRate}},"source":"test"},
                  {"effectiveFrom":"2026-07-01T00:00:00Z","usdPerMillion":30,"source":"test"}
                ],
                "output": [{"effectiveFrom":"2026-01-01T00:00:00Z","usdPerMillion":75,"source":"test"}],
                "cacheRead": [{"effectiveFrom":"2026-01-01T00:00:00Z","usdPerMillion":1.5,"source":"test"}],
                "cacheCreation": [{"effectiveFrom":"2026-01-01T00:00:00Z","usdPerMillion":18.75,"source":"test"}],
                "thinking": [{"effectiveFrom":"2026-01-01T00:00:00Z","usdPerMillion":75,"source":"test"}]
              }
            }
          }
        }
        """);

    [Fact]
    public void A_claude_shaped_attempt_becomes_one_labelled_row_with_every_reported_dimension()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-claude");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start), room, Repository));

            Assert.Equal(CostSourceKind.BatonExecution, row.SourceKind);
            Assert.Equal("github.com/aer-works/baton", row.Repository);
            Assert.Equal(BatonPaths.RecordKey(room), row.Room);
            Assert.Equal("wf-cost-ledger", row.Workflow);
            Assert.Equal("implement", row.Step);
            Assert.Equal("implement", row.Role);
            Assert.Equal("exec-claude", row.Execution);
            Assert.Equal("claude", row.Adapter);
            Assert.Equal("claude-opus-5", row.Model);
            Assert.Equal("Succeeded", row.Outcome);
            Assert.Equal(Start, row.StartedAt);
            Assert.Equal(Start.AddSeconds(2), row.EndedAt);
            Assert.Equal(2000, row.WallClockMs);

            Assert.Equal(100, row.TokensIn);
            Assert.Equal(50, row.TokensOut);
            Assert.Equal(5, row.CacheReadTokens);
            Assert.Equal(10, row.CacheCreationTokens);
            Assert.Equal(7, row.ThinkingTokens);
            Assert.Equal(3, row.Turns);

            Assert.Equal(CostCompleteness.Complete, row.Completeness);
            Assert.Null(row.CompletenessReason);

            // The shipped catalog prices nothing citable, so a real row is unpriced rather than
            // borrowing a neighbouring model's number.
            Assert.Null(row.ApiEquivalentUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.EstimateStatus);
            Assert.Equal(PriceCatalog.Default.Id, row.PriceCatalogId);
            Assert.Equal(PriceCatalog.Default.Version, row.PriceCatalogVersion);
            Assert.Equal(PlanFactorTable.Default.Id, row.PlanFactorTableId);
            Assert.Equal(PlanFactorTable.Default.Version, row.PlanFactorTableVersion);

            // Phase-A reserved fields: named in the schema, no writer yet.
            Assert.Null(row.Effort);
            Assert.Null(row.Issue);
            Assert.Null(row.PullRequest);
            Assert.Null(row.Attempt);
            Assert.Null(row.Raw);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_agy_shaped_attempt_omits_the_dimension_that_vendor_never_reports_rather_than_zeroing_it()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-agy");
            WriteCapturedStream(room, executionId, AgyTerminalLine);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "agy", "gemini-3-pro", Start), room, Repository));

            Assert.Equal(14407, row.TokensIn);
            Assert.Equal(1173, row.TokensOut);
            Assert.Equal(40765, row.CacheReadTokens);
            Assert.Equal(992, row.ThinkingTokens);
            // The asymmetry: agy's envelope has no cache-creation field at all. Absent, never 0.
            Assert.Null(row.CacheCreationTokens);

            // And absent means ABSENT ON THE WIRE too -- a JSON `"cacheCreation":0` would read to every
            // consumer as a measured zero, which is the exact confusion #1849 forbids.
            var json = JsonSerializer.Serialize(row);
            Assert.DoesNotContain("cacheCreation", json, StringComparison.Ordinal);
            Assert.Contains("\"cacheRead\":40765", json, StringComparison.Ordinal);

            // agy's plan meter has never been measured, so its plan estimate says so rather than
            // flattening into the same "unpriced" an absent list price produces.
            Assert.Equal(EstimateStatus.Unmeasured, row.PlanMeterEstimateStatus);
            Assert.Null(row.PlanMeterEstimateUsd);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_unknown_model_is_unpriced_on_both_estimates_and_never_borrows_a_neighbours_price()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-unknown-model");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            // The catalog prices claude-opus-5 and nothing else; this attempt ran on a sibling.
            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5-mini", Start),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            Assert.Null(row.ApiEquivalentUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.EstimateStatus);
            Assert.Null(row.PlanMeterEstimateUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.PlanMeterEstimateStatus);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_priced_model_estimates_both_numbers_and_the_plan_meter_weights_cache_read_below_list()
    {
        // The positive control for the unpriced arms above: the same code path DOES produce numbers
        // when the catalog covers the model, so "unpriced" is a finding about the catalog rather than
        // a pricing path that never works.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-priced");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            // Ends 2026-06-01, so the FIRST range's input rate ($15/M) applies. $30/M is the range that
            // opens 2026-07-01, and picking it would be the silent-reprice failure this ledger exists
            // to prevent -- the two rates are what make that distinguishable here.
            var expectedApi = ((100 * 15m) + (50 * 75m) + (5 * 1.5m) + (10 * 18.75m) + (7 * 75m)) / 1_000_000m;
            Assert.Equal(EstimateStatus.Estimated, row.EstimateStatus);
            Assert.Equal(expectedApi, row.ApiEquivalentUsd);

            var expectedPlan = ((100 * 15m) + (50 * 75m) + (5 * 1.5m * 0.10m) + (10 * 18.75m) + (7 * 75m)) / 1_000_000m;
            Assert.Equal(EstimateStatus.Estimated, row.PlanMeterEstimateStatus);
            Assert.Equal(expectedPlan, row.PlanMeterEstimateUsd);
            Assert.True(row.PlanMeterEstimateUsd < row.ApiEquivalentUsd, "The 0.10 cache-read weight must move the plan estimate.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_attempt_is_priced_by_the_range_in_force_when_it_ended_not_by_the_latest_one()
    {
        // The polarity partner of the arm above: same catalog, same tokens, an attempt on the OTHER
        // side of the range boundary. Without this, a bug that always picked the first (or the last)
        // range would still pass one of the two.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-later-range");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var afterBoundary = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", afterBoundary),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            var expected = ((100 * 30m) + (50 * 75m) + (5 * 1.5m) + (10 * 18.75m) + (7 * 75m)) / 1_000_000m;
            Assert.Equal(expected, row.ApiEquivalentUsd);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_live_discount_window_with_no_measured_percent_reads_unknown_rather_than_full_price()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-sonnet");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var insideWindow = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-sonnet-5", insideWindow), room, Repository));

            // The operator knows the window exists and not its size -- PlanFactorStatus.Unknown's own
            // remarks state what the alternative fallback would produce here.
            Assert.Equal(EstimateStatus.Unknown, row.PlanMeterEstimateStatus);
            Assert.Null(row.PlanMeterEstimateUsd);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void Outside_that_window_the_same_model_is_not_unknown()
    {
        // The polarity control for the arm above: without it, an always-Unknown bug passes.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-sonnet-later");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var afterWindow = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-sonnet-5", afterWindow), room, Repository));

            Assert.NotEqual(EstimateStatus.Unknown, row.PlanMeterEstimateStatus);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_later_catalog_edit_does_not_alter_a_row_already_written()
    {
        // #1849's acceptance criterion: price-catalog changes never retroactively rewrite prior
        // estimated totals, and every estimate identifies the catalog version that produced it.
        var room = NewRoom();
        var ledgerPath = NewLedgerPath();
        try
        {
            var executionId = new ExecutionId("exec-repriced");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var events = SettledExecution(executionId, "claude", "claude-opus-5", Start);

            var v1 = TwoRangeCatalog("v1", earlyInputRate: 15);
            await CostLedgerStore.AppendAsync(
                CostLedgerStore.BuildEntries(events, room, Repository, v1), ledgerPath, TestContext.Current.CancellationToken);

            // The catalog is corrected: the SAME effective range now carries a different input rate.
            var v2 = TwoRangeCatalog("v2", earlyInputRate: 45);
            var recomputed = Assert.Single(CostLedgerStore.BuildEntries(events, room, Repository, v2));

            var stored = Assert.Single(await CostLedgerStore.ReadAllAsync(ledgerPath, TestContext.Current.CancellationToken));
            Assert.Equal("v1", stored.PriceCatalogVersion);
            Assert.Equal(v1.TryEstimateUsd("claude", "claude-opus-5", new TokenDimensions(100, 50, 5, 10, 7), Start), stored.ApiEquivalentUsd);

            // The recomputation genuinely differs -- otherwise this test would pass against a catalog
            // whose edit changed nothing, proving only that two identical numbers are equal.
            Assert.Equal("v2", recomputed.PriceCatalogVersion);
            Assert.NotEqual(stored.ApiEquivalentUsd, recomputed.ApiEquivalentUsd);
        }
        finally
        {
            FileCleanup.Delete(ledgerPath);
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_provably_truncated_capture_is_marked_partial_with_the_reason_the_stream_reader_emits()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-truncated");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine, truncated: true);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start), room, Repository));

            Assert.Equal(CostCompleteness.Partial, row.Completeness);
            Assert.Equal("stream-truncated-by-rollover", row.CompletenessReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_retry_is_a_second_attempt_and_therefore_a_second_row()
    {
        var room = NewRoom();
        var ledgerPath = NewLedgerPath();
        try
        {
            // Two ids for one step, which is what a retry produces -- CostLedgerEntry.Attempt's own doc
            // states why. The dedupe below must not collapse them.
            var first = new ExecutionId("exec-attempt-1");
            var second = new ExecutionId("exec-attempt-2");
            WriteCapturedStream(room, first, ClaudeTerminalLine);
            WriteCapturedStream(room, second, ClaudeTerminalLine);

            var events = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(first, "implement", "claude", "claude-opus-5"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(first, Pid: 1), Start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(first, 1, CoreExitReason.Natural), Start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionFailed(first, FailureClassification.Retryable)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(second, "implement", "claude", "claude-opus-5"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(second, Pid: 2), Start.AddSeconds(10)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(second, 0, CoreExitReason.Natural), Start.AddSeconds(12)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(second)),
            };

            await CostLedgerStore.AppendAsync(CostLedgerStore.BuildEntries(events, room, Repository), ledgerPath, TestContext.Current.CancellationToken);
            var rows = await CostLedgerStore.ReadAllAsync(ledgerPath, TestContext.Current.CancellationToken);

            Assert.Equal(2, rows.Count);
            Assert.Equal(
                new[] { "Retryable", "Succeeded" },
                rows.Select(r => r.Outcome).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            FileCleanup.Delete(ledgerPath);
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Settling_the_same_room_twice_writes_each_execution_exactly_once()
    {
        var room = NewRoom();
        var ledgerPath = NewLedgerPath();
        try
        {
            var executionId = new ExecutionId("exec-settled-twice");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var events = SettledExecution(executionId, "claude", "claude-opus-5", Start);

            // `baton run` on an already-terminal room, `supply`, and `resolve --reject` -> re-Terminal
            // all re-derive the whole room at the settle site. Without the skip, the ledger inflates.
            await CostLedgerStore.AppendAsync(CostLedgerStore.BuildEntries(events, room, Repository), ledgerPath, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(CostLedgerStore.BuildEntries(events, room, Repository), ledgerPath, TestContext.Current.CancellationToken);

            var rows = await CostLedgerStore.ReadAllAsync(ledgerPath, TestContext.Current.CancellationToken);
            Assert.Equal("exec-settled-twice", Assert.Single(rows).Execution);
        }
        finally
        {
            FileCleanup.Delete(ledgerPath);
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_execution_that_never_settled_writes_no_row_at_all()
    {
        // The accepted loss, stated rather than hidden (spec/baton.md §7): a lane that dies before
        // settling contributes nothing here -- it must not appear as a zero-cost attempt.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-still-running");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            var built = CostLedgerStore.BuildEntries(
                [
                    new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "implement", "claude", "claude-opus-5"))),
                    new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), Start),
                ],
                room,
                Repository);

            Assert.Empty(built);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task Two_repositories_keep_two_ledgers()
    {
        // The point of keying by repository identity rather than by room: a fleet spanning projects
        // must not pool them into one file, and one project's worktrees must not split into several.
        var room = NewRoom();
        var mine = NewLedgerPath();
        var other = NewLedgerPath();
        try
        {
            var executionId = new ExecutionId("exec-two-repos");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var events = SettledExecution(executionId, "claude", "claude-opus-5", Start);
            var otherRepository = RepositoryIdentity.From("https://github.com/aer-works/other.git", null)!;

            await CostLedgerStore.AppendAsync(CostLedgerStore.BuildEntries(events, room, Repository), mine, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(CostLedgerStore.BuildEntries(events, room, otherRepository), other, TestContext.Current.CancellationToken);

            Assert.Equal("github.com/aer-works/baton", Assert.Single(await CostLedgerStore.ReadAllAsync(mine, TestContext.Current.CancellationToken)).Repository);
            Assert.Equal("github.com/aer-works/other", Assert.Single(await CostLedgerStore.ReadAllAsync(other, TestContext.Current.CancellationToken)).Repository);
            Assert.NotEqual(BatonPaths.CostLedgerFile(Repository.FileSlug), BatonPaths.CostLedgerFile(otherRepository.FileSlug));
        }
        finally
        {
            FileCleanup.Delete(mine);
            FileCleanup.Delete(other);
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void The_source_kind_label_survives_a_round_trip_as_the_wire_name_phase_C_will_filter_on()
    {
        var json = JsonSerializer.Serialize(new CostLedgerEntry(CostSourceKind.BatonExecution, Execution: "e"));

        Assert.Contains("\"sourceKind\":\"baton-execution\"", json, StringComparison.Ordinal);
        Assert.Equal(CostSourceKind.BatonExecution, JsonSerializer.Deserialize<CostLedgerEntry>(json)!.SourceKind);

        // The three reserved kinds exist in the enum with no writer, so phase C adds an importer
        // rather than a schema migration.
        Assert.Contains(
            "\"sourceKind\":\"claude-code-session\"",
            JsonSerializer.Serialize(new CostLedgerEntry(CostSourceKind.ClaudeCodeSession)),
            StringComparison.Ordinal);
    }
}
