using System.Text.Json;
using Baton.Accounting;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
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

    /// <summary>
    /// The SAME five dimensions as <see cref="ClaudeTerminalLine"/>, reported the way production
    /// actually reports them — through the whole-tree <c>modelUsage</c> map that
    /// <c>ClaudeUsageParser</c> PREFERS over the top-level <c>usage</c> object (#1706). Every pricing
    /// fixture in this file used to be the fallback shape, which is why #1883 review F1 — a whole-tree,
    /// possibly multi-model sum priced at one requested model's rate — had no test that could see it.
    /// The top-level object here carries deliberately different (smaller) numbers, so an arm that
    /// silently read the fallback instead would fail on the figures rather than pass quietly.
    /// </summary>
    private const string ClaudeTerminalLineWithModelUsage =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"num_turns":3,"result":"done","session_id":"s","modelUsage":{"claude-opus-5":{"inputTokens":100,"outputTokens":50,"cacheCreationInputTokens":10,"cacheReadInputTokens":5,"thinkingTokens":7}},"usage":{"input_tokens":1,"output_tokens":1,"cache_creation_input_tokens":1,"cache_read_input_tokens":1,"output_tokens_details":{"thinking_tokens":1}}}""";

    /// <summary>The same shape with the tree's usage split across TWO models — an `implement` step whose subagent ran on a cheaper one.</summary>
    private const string ClaudeTerminalLineWithTwoModelUsage =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"num_turns":3,"result":"done","session_id":"s","modelUsage":{"claude-opus-5":{"inputTokens":100,"outputTokens":50,"cacheCreationInputTokens":10,"cacheReadInputTokens":5,"thinkingTokens":7},"claude-haiku-5":{"inputTokens":900,"outputTokens":400,"cacheCreationInputTokens":0,"cacheReadInputTokens":0,"thinkingTokens":0}}}""";

    /// <summary>One model, and it is NOT the one the step requested — a substitution or a quota-driven downgrade.</summary>
    private const string ClaudeTerminalLineWithOtherModelUsage =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"num_turns":3,"result":"done","session_id":"s","modelUsage":{"claude-haiku-5":{"inputTokens":100,"outputTokens":50,"cacheCreationInputTokens":10,"cacheReadInputTokens":5,"thinkingTokens":7}}}""";

    /// <summary>
    /// A mid-stream `"type":"assistant"` line carrying the two cache figures
    /// <c>ClaudeUsageParser.TryParseIncrementalUsage</c> reads. Without one of these in the capture, the
    /// replay <c>ExecutionUsageProjector</c> runs over the same bytes reads no usage at all and the
    /// reconciliation triple cannot complete — so a terminal-line-only fixture is not a clean stream,
    /// it is one whose live half is missing (#1883 review F2).
    /// </summary>
    private const string ClaudeAssistantUsageLine =
        """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":10,"cache_read_input_tokens":5,"output_tokens":3}}}""";

    /// <summary>An agy terminal `result` line, from <c>AgyFinalUsageParsingTests</c>. Note it reports thinking but has no cache-CREATION dimension at all — the asymmetry this file checks.</summary>
    private const string AgyTerminalLine =
        """{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"done","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}""";

    /// <summary>An agy mid-stream DONE `agent_response` step_update, the shape that vendor's incremental parser reads.</summary>
    private const string AgyStepUpdateUsageLine =
        """{"event":"step_update","step_update":{"state":"DONE","step_type":"agent_response","usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}""";

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
    /// <para>
    /// #1883 review F2: a mid-stream usage line is written BEFORE the terminal one by default, because
    /// that is what a real capture looks like and because `completeness: "complete"` now means the
    /// terminal read and the replay over the same bytes RECONCILED. A fixture that is one terminal line
    /// is a stream whose live half is missing, and pinning `complete` against it would pin the label to
    /// a shape production never produces. <paramref name="liveUsageLine"/> is null for the arms that
    /// deliberately exercise a stream with no live reading in it.
    /// </para>
    /// </summary>
    private static void WriteCapturedStream(
        string roomDirectoryPath,
        ExecutionId executionId,
        string terminalLine,
        bool truncated = false,
        string? liveUsageLine = ClaudeAssistantUsageLine)
    {
        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
        Directory.CreateDirectory(outputDirectory);
        var stream = liveUsageLine is null ? terminalLine + "\n" : liveUsageLine + "\n" + terminalLine + "\n";
        File.WriteAllText(Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName), stream);
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

            // This fixture is the top-level `usage` fallback shape, which names no model at all --
            // absent, which is what lets it be priced at the requested model's rate.
            Assert.Null(row.ModelsObserved);
            Assert.Null(row.EstimateReason);

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
            Assert.Null(row.ModelEchoed);
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
            WriteCapturedStream(room, executionId, AgyTerminalLine, liveUsageLine: AgyStepUpdateUsageLine);

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
    public void A_whole_tree_total_split_across_two_models_is_refused_rather_than_priced_at_the_requested_ones_rate()
    {
        // #1883 review F1, wider form -- CostLedgerStore.Estimate's own doc has the rule and
        // spec/baton.md §7 has the ruling behind it. What this arm fixes is the shape: a tree whose
        // subagent ran on a second model, where one rate applied to the total would charge Opus rates
        // for Haiku tokens.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-two-models");
            WriteCapturedStream(room, executionId, ClaudeTerminalLineWithTwoModelUsage);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            Assert.Equal(new[] { "claude-opus-5", "claude-haiku-5" }, row.ModelsObserved);
            Assert.Null(row.ApiEquivalentUsd);
            Assert.Null(row.PlanMeterEstimateUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.EstimateStatus);
            Assert.Equal(EstimateStatus.Unpriced, row.PlanMeterEstimateStatus);
            Assert.Equal("multi-model-usage", row.EstimateReason);

            // The row still carries the tokens -- refusing to PRICE them is not refusing to record
            // them, and phase B's per-model rows are what will price this attempt.
            Assert.Equal(1000, row.TokensIn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_whole_tree_total_naming_exactly_the_requested_model_is_still_priced()
    {
        // The polarity control for the two arms around it: the refusal above is about ATTRIBUTION, not
        // about `modelUsage` being present. Without this, a bug that refused every whole-tree reading
        // would pass both of them -- and it would silently unprice every real claude row.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-one-model");
            WriteCapturedStream(room, executionId, ClaudeTerminalLineWithModelUsage);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            Assert.Equal(new[] { "claude-opus-5" }, row.ModelsObserved);
            Assert.Null(row.EstimateReason);

            // The figures are `modelUsage`'s, not the deliberately-different top-level ones -- which is
            // what proves this arm exercised the path production prefers.
            Assert.Equal(100, row.TokensIn);
            var expected = ((100 * 15m) + (50 * 75m) + (5 * 1.5m) + (10 * 18.75m) + (7 * 75m)) / 1_000_000m;
            Assert.Equal(EstimateStatus.Estimated, row.EstimateStatus);
            Assert.Equal(expected, row.ApiEquivalentUsd);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_single_model_that_is_not_the_requested_one_is_refused_rather_than_priced_at_what_was_asked_for()
    {
        // The narrow form of F1, and the one that needs only ONE modelUsage entry: a step requested at
        // claude-opus-5 that the vendor actually served on another model. The catalog below prices
        // claude-opus-5 and nothing else, so WITHOUT the guard this row is `estimated` at Opus rates
        // for tokens no Opus call spent -- that is what makes this arm discriminating rather than
        // merely agreeing with an empty catalog.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-model-mismatch");
            WriteCapturedStream(room, executionId, ClaudeTerminalLineWithOtherModelUsage);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            Assert.Equal(new[] { "claude-haiku-5" }, row.ModelsObserved);
            Assert.Equal("claude-opus-5", row.Model);
            Assert.Null(row.ApiEquivalentUsd);
            Assert.Null(row.PlanMeterEstimateUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.EstimateStatus);
            Assert.Equal(EstimateStatus.Unpriced, row.PlanMeterEstimateStatus);
            Assert.Equal("model-mismatch", row.EstimateReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_attempt_whose_usage_was_never_read_carries_no_completeness_label_at_all()
    {
        // #1883 review F2, the never-read arm -- CostLedgerStore.ResolveCompleteness has the case.
        // `capture` is a real step type with no registered usage parser, which is what makes this a
        // production shape rather than a constructed one.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-no-parser");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "capture", "none", Start), room, Repository));

            Assert.Null(row.Completeness);
            Assert.Null(row.CompletenessReason);
            Assert.Null(row.TokensIn);
            Assert.Null(row.BilledTokens);

            // Absent means absent on the wire too, same doctrine as every token dimension.
            Assert.DoesNotContain("completeness", JsonSerializer.Serialize(row), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_attempt_killed_before_it_emitted_a_terminal_line_is_partial_not_complete()
    {
        // The other half of F2: a worker killed mid-stream leaves an unparseable last line, which the
        // stream reader reports as `no-terminal-billed-figure` -- a reason that ALSO covers "a complete
        // stream that carried no billed figure", and nothing downstream can tell the two apart. The
        // weaker claim is the honest one.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-killed");
            var artifactsRoot = Path.Combine(room, ArtifactManager.ArtifactsDirectoryName);
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(
                Path.Combine(outputDirectory, ExecutionStreamLogger.StdoutLogFileName),
                ClaudeAssistantUsageLine + "\n" + """{"type":"assistant","message":{"usage":{"input_""" + "\n");

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start), room, Repository));

            Assert.Equal(CostCompleteness.Partial, row.Completeness);
            Assert.Equal("no-terminal-billed-figure", row.CompletenessReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void Every_reason_the_stream_reader_can_emit_makes_a_row_partial()
    {
        // #1883 review F3: the store used to restate two of the producer's four reason strings and let
        // the other two fall through to `complete`. This is the drift check that failure needs -- add a
        // fifth reason to ExecutionUsageView and it is covered here the moment it is declared, because
        // the population is the producer's own set rather than a copy of it.
        Assert.NotEmpty(ExecutionUsageView.KnownUnavailableReasons);
        foreach (var reason in ExecutionUsageView.KnownUnavailableReasons)
        {
            Assert.Equal(CostCompleteness.Partial, CostLedgerStore.ResolveCompleteness(reason, billedTokens: 100));
            Assert.Equal(CostCompleteness.Partial, CostLedgerStore.ResolveCompleteness(reason, billedTokens: null));
        }

        // Both polarities of the no-reason case, which is where the third state lives.
        Assert.Equal(CostCompleteness.Complete, CostLedgerStore.ResolveCompleteness(null, billedTokens: 100));
        Assert.Null(CostLedgerStore.ResolveCompleteness(null, billedTokens: null));
    }

    [Fact]
    public void An_attempt_ending_exactly_on_a_range_boundary_matches_exactly_one_range()
    {
        // #1883 review F5. The two arms above bracket the 2026-07-01 boundary without ever landing on
        // it. This arm lands on it and pins that the SECOND range's rate is what prices the attempt:
        // it rules out a first-match-wins lookup (15) and a strict `from < at` comparison (unpriced).
        // It cannot see a closed-range flip (`at <= to` would match both ranges and last-match-wins
        // still yields 30) -- the sibling below, whose only range ENDS at the boundary, is what does.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-boundary");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var boundary = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", boundary.AddSeconds(-2)),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            Assert.Equal(boundary, row.EndedAt);
            var expected = ((100 * 30m) + (50 * 75m) + (5 * 1.5m) + (10 * 18.75m) + (7 * 75m)) / 1_000_000m;
            Assert.Equal(expected, row.ApiEquivalentUsd);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_attempt_ending_exactly_when_the_only_range_ends_is_unpriced_because_ranges_are_half_open()
    {
        // #1883 second-pass review: the discriminating half of the boundary rule. With no successor range
        // covering 2026-07-01, `from <= at < to` prices nothing at that instant; a closed `at <= to` would
        // price it at 15. Only one of those can be true of the same catalog, so this arm fails on the flip.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-closed-end");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var boundary = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var closedEnded = PriceCatalog.Parse("""
                {
                  "id": "test-prices",
                  "version": "closed-end",
                  "vendors": {
                    "claude": {
                      "claude-opus-5": {
                        "input": [{"effectiveFrom":"2026-01-01T00:00:00Z","effectiveTo":"2026-07-01T00:00:00Z","usdPerMillion":15,"source":"test"}],
                        "output": [{"effectiveFrom":"2026-01-01T00:00:00Z","effectiveTo":"2026-07-01T00:00:00Z","usdPerMillion":75,"source":"test"}],
                        "cacheRead": [{"effectiveFrom":"2026-01-01T00:00:00Z","effectiveTo":"2026-07-01T00:00:00Z","usdPerMillion":1.5,"source":"test"}],
                        "cacheCreation": [{"effectiveFrom":"2026-01-01T00:00:00Z","effectiveTo":"2026-07-01T00:00:00Z","usdPerMillion":18.75,"source":"test"}],
                        "thinking": [{"effectiveFrom":"2026-01-01T00:00:00Z","effectiveTo":"2026-07-01T00:00:00Z","usdPerMillion":75,"source":"test"}]
                      }
                    }
                  }
                }
                """);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", boundary.AddSeconds(-2)),
                room,
                Repository,
                closedEnded));

            Assert.Equal(boundary, row.EndedAt);
            Assert.Null(row.ApiEquivalentUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.EstimateStatus);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void An_attempt_ending_before_the_earliest_range_is_unpriced_rather_than_priced_at_the_earliest_rate()
    {
        // #1883 review F5, the other end: no range covers 2025, and the honest answer is no number --
        // not the first range's rate extended backwards.
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-before-catalog");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc)),
                room,
                Repository,
                TwoRangeCatalog("v1", earlyInputRate: 15)));

            Assert.Null(row.ApiEquivalentUsd);
            Assert.Equal(EstimateStatus.Unpriced, row.EstimateStatus);
            Assert.Null(row.EstimateReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_catalog_parsed_from_json_matches_a_vendor_model_and_dimension_spelled_in_another_case()
    {
        // #1883 review F9: PriceCatalog.Default is built OrdinalIgnoreCase, so a parsed catalog that
        // was not would make the same document behave differently depending on where it came from --
        // and only the outer level being fixed would leave the two that actually vary in spelling live.
        var catalog = PriceCatalog.Parse("""
            {"id":"t","version":"1","vendors":{"Claude":{"Claude-Opus-5":{"Input":[{"effectiveFrom":"2026-01-01T00:00:00Z","usdPerMillion":15,"source":"test"}]}}}}
            """);

        Assert.Equal(15m, catalog.TryRate("claude", "claude-opus-5", PriceDimension.Input, Start));
    }

    [Fact]
    public void A_price_point_with_no_citable_source_is_rejected_rather_than_parsed_with_a_null_one()
    {
        // #1883 review F6: PricePoint.Source's own doc states the rule this now enforces. It was prose
        // only, and nothing on the pricing path reads the field to notice a null one.
        Assert.Throws<JsonException>(() => PriceCatalog.Parse("""
            {"id":"t","version":"1","vendors":{"claude":{"claude-opus-5":{"input":[{"effectiveFrom":"2026-01-01T00:00:00Z","usdPerMillion":15}]}}}}
            """));

        Assert.Throws<JsonException>(() => PlanFactorTable.Parse("""
            {"id":"t","version":"1","vendors":{"claude":{"unmeasured":false,"dimensionWeights":{"cacheRead":{"factor":0.1}}}}}
            """));
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
    public void A_journalled_stream_log_loss_makes_a_row_partial_exactly_as_the_marker_file_does()
    {
        // #1885 against #1883's ledger. The loss is announced on two channels (spec/baton.md §3) and
        // this store reads neither directly -- it reads ExecutionUsageView.BilledReconciliationUnavailable
        // -- so the claim under test is that the JOURNALLED channel reaches a cost row at all, and lands
        // the same label and the same reason string the FILE channel does. Same capture in all three
        // arms; the only thing that varies is which channel (if either) announces the loss, which is
        // what makes this discriminate the announcement rather than the fixture.
        var journalled = NewRoom();
        var markered = NewRoom();
        var clean = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-loss");

            // Arm 1: the event, and NO marker file -- the case the marker channel cannot report,
            // because the marker is created in the very directory whose writes were refused.
            WriteCapturedStream(journalled, executionId, ClaudeTerminalLine);
            var withEvent = SettledExecution(executionId, "claude", "claude-opus-5", Start);
            withEvent.Add(new LogEntry.FlowLogEntry(new FlowEvent.StreamLogLossDeclared(
                executionId,
                ExecutionStreamLogger.StdoutStreamName,
                ExecutionUsageView.StreamTruncatedByWriteFailureReason,
                BytesSurrendered: 4096,
                MarkerLanded: false)));
            Assert.False(File.Exists(Path.Combine(
                ArtifactManager.ResolveOutputDirectory(
                    Path.Combine(journalled, ArtifactManager.ArtifactsDirectoryName), executionId),
                ExecutionStreamLogger.StdoutWriteFailureMarkerFileName)));

            var journalledRow = Assert.Single(CostLedgerStore.BuildEntries(withEvent, journalled, Repository));

            // Arm 2: the marker file, and NO event -- the pre-#1885 channel, unchanged.
            WriteCapturedStream(markered, executionId, ClaudeTerminalLine);
            File.WriteAllText(
                Path.Combine(
                    ArtifactManager.ResolveOutputDirectory(
                        Path.Combine(markered, ArtifactManager.ArtifactsDirectoryName), executionId),
                    ExecutionStreamLogger.StdoutWriteFailureMarkerFileName),
                "write failed");
            var markeredRow = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start), markered, Repository));

            Assert.Equal(CostCompleteness.Partial, journalledRow.Completeness);
            Assert.Equal(ExecutionUsageView.StreamTruncatedByWriteFailureReason, journalledRow.CompletenessReason);
            Assert.Equal(markeredRow.Completeness, journalledRow.Completeness);
            Assert.Equal(markeredRow.CompletenessReason, journalledRow.CompletenessReason);

            // The reconciliation triple is withheld either way -- partial is not a label pasted over a
            // Σ that was still computed and reported.
            Assert.Null(journalledRow.BilledTokens);
            Assert.Null(journalledRow.LiveBilledTokens);
            Assert.Null(journalledRow.BilledUnderReadTokens);

            // Arm 3: the control. The identical capture with NEITHER announcement reconciles, so the
            // two arms above are about the announcement rather than about this fixture being unreadable.
            WriteCapturedStream(clean, executionId, ClaudeTerminalLine);
            var cleanRow = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start), clean, Repository));
            Assert.Equal(CostCompleteness.Complete, cleanRow.Completeness);
            Assert.Null(cleanRow.CompletenessReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(journalled);
            DirectoryCleanup.DeleteRecursively(markered);
            DirectoryCleanup.DeleteRecursively(clean);
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
    public async Task This_store_is_the_cost_ledgers_own_JSONL_ledger_under_its_own_lock_name()
    {
        // This ledger's half of the pair QuotaLedgerStoreTests' own smoke test explains: a prefix that
        // must stay distinct from the burn ledger's, and a live dedupe round trip.
        Assert.Equal("baton-cost-ledger", CostLedgerStore.Ledger.LockNamePrefix);
        Assert.NotEqual(QuotaLedgerStore.Ledger.LockNamePrefix, CostLedgerStore.Ledger.LockNamePrefix);

        var path = NewLedgerPath();
        try
        {
            await CostLedgerStore.AppendAsync(
                [new CostLedgerEntry(CostSourceKind.BatonExecution, Execution: "exec-a")], path, TestContext.Current.CancellationToken);
            await CostLedgerStore.AppendAsync(
                [
                    new CostLedgerEntry(CostSourceKind.BatonExecution, Execution: "exec-a"),
                    new CostLedgerEntry(CostSourceKind.BatonExecution, Execution: "exec-b"),
                ],
                path,
                TestContext.Current.CancellationToken);

            var all = await CostLedgerStore.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Execution == "exec-a");
            Assert.Contains(all, e => e.Execution == "exec-b");
        }
        finally
        {
            FileCleanup.Delete(path);
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

    /// <summary>
    /// #1882's two non-token dimensions reach the ledger row, and are absent — not zero — on a room
    /// that ran no verify step. Both polarities in one arm on purpose: a copy that hard-coded zero, or
    /// one that dropped the fields entirely, would pass a present-only test. The projector owns the
    /// attribution; what this pins is that <c>BuildEntries</c> carries it through rather than
    /// recomputing or discarding it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_verify_step_figures_reach_the_row_when_a_step_ran_and_are_absent_when_none_did(bool verifyStepRan)
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-verify-step");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);

            if (verifyStepRan)
            {
                var artifactsRoot = Path.Combine(room, ArtifactManager.ArtifactsDirectoryName);
                Directory.CreateDirectory(artifactsRoot);
                File.WriteAllText(
                    Path.Combine(artifactsRoot, VerifyStepReport.SidecarFileName),
                    VerifyStepReport.SerializeSidecar(new VerifyStepReport.Sidecar(
                        TotalWallClockMs: 91002,
                        ResultsBytes: 4321,
                        Commands: [new VerifyInstrument("dotnet test", 0, 91002)])));
            }

            var row = Assert.Single(CostLedgerStore.BuildEntries(
                SettledExecution(executionId, "claude", "claude-opus-5", Start), room, Repository));

            Assert.Equal(verifyStepRan ? 91002L : null, row.VerifyStepMs);
            Assert.Equal(verifyStepRan ? 4321L : null, row.VerifyResultsBytes);

            // Absent on the wire, never a zero a reader would mistake for a measured figure.
            var json = JsonSerializer.Serialize(row);
            Assert.Equal(verifyStepRan, json.Contains("\"verifyStepMs\":91002", StringComparison.Ordinal));
            Assert.Equal(verifyStepRan, json.Contains("\"verifyResultsBytes\":4321", StringComparison.Ordinal));
            Assert.DoesNotContain("\"verifyStepMs\":0", json, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    /// <summary>
    /// #1848: a row whose execution was admitted only because a runway hold was overridden carries the
    /// operator's reason. Both polarities, because the field's whole value is that its ABSENCE is
    /// readable — an unsupplied map must leave it absent rather than stamping an empty string.
    /// </summary>
    [Fact]
    public void A_runway_override_reason_is_stamped_onto_the_row_for_the_worker_it_was_recorded_for()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-override");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var settled = SettledExecution(executionId, "claude", "claude-opus-5", Start);

            var stamped = Assert.Single(CostLedgerStore.BuildEntries(
                settled, room, Repository,
                runwayOverrideReasonByWorker: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["implement"] = "conductor lane, week resets in 2h",
                }));
            var unstamped = Assert.Single(CostLedgerStore.BuildEntries(settled, room, Repository));
            var otherWorker = Assert.Single(CostLedgerStore.BuildEntries(
                settled, room, Repository,
                runwayOverrideReasonByWorker: new Dictionary<string, string>(StringComparer.Ordinal) { ["review"] = "other lane" }));

            Assert.Equal("implement", stamped.Role);
            Assert.Equal("conductor lane, week resets in 2h", stamped.RunwayOverrideReason);
            Assert.Null(unstamped.RunwayOverrideReason);
            Assert.Null(otherWorker.RunwayOverrideReason);
            Assert.DoesNotContain(
                "runwayOverrideReason", JsonSerializer.Serialize(unstamped), StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void Settle_metadata_populates_issue_PR_and_diff_shape_and_omits_every_unavailable_member()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-joined");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var settled = SettledExecution(executionId, "claude", "claude-opus-5", Start);
            var populated = Assert.Single(CostLedgerStore.BuildEntries(
                settled,
                room,
                Repository,
                metadataByExecutionId: new Dictionary<string, CostLedgerExecutionMetadata>(StringComparer.Ordinal)
                {
                    [executionId.Value] = new(
                        Issue: "1901",
                        PullRequest: "2001",
                        FilesChanged: 7,
                        Additions: 42,
                        Deletions: 9,
                        TestFilesChanged: 3),
                }));
            var absent = Assert.Single(CostLedgerStore.BuildEntries(settled, room, Repository));

            Assert.Equal("1901", populated.Issue);
            Assert.Equal("2001", populated.PullRequest);
            Assert.Equal(7, populated.FilesChanged);
            Assert.Equal(42, populated.Additions);
            Assert.Equal(9, populated.Deletions);
            Assert.Equal(3, populated.TestFilesChanged);

            Assert.Null(absent.Issue);
            Assert.Null(absent.PullRequest);
            Assert.Null(absent.FilesChanged);
            Assert.Null(absent.Additions);
            Assert.Null(absent.Deletions);
            Assert.Null(absent.TestFilesChanged);

            var populatedJson = JsonSerializer.Serialize(populated);
            Assert.Contains("\"issue\":\"1901\"", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"pr\":\"2001\"", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"filesChanged\":7", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"additions\":42", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"deletions\":9", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"testFilesChanged\":3", populatedJson, StringComparison.Ordinal);

            var absentJson = JsonSerializer.Serialize(absent);
            Assert.DoesNotContain("\"issue\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"pr\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"filesChanged\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"additions\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"deletions\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"testFilesChanged\"", absentJson, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void A_valid_review_artifact_populates_verdict_counts_and_reviewed_reference_and_absence_stays_absent()
    {
        var room = NewRoom();
        try
        {
            var executionId = new ExecutionId("exec-review");
            WriteCapturedStream(room, executionId, ClaudeTerminalLine);
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                Path.Combine(room, ArtifactManager.ArtifactsDirectoryName), executionId);
            var verdictPath = Path.Combine(outputDirectory, "verdict.json");
            File.WriteAllText(
                verdictPath,
                """
                {
                  "reviewedRef": "PR #2001 @ abcdef1234567890",
                  "findings": [
                    {"severity":"high","claim":"confirmed high","status":"confirmed"},
                    {"severity":"medium","claim":"refuted medium","status":"refuted"},
                    {"severity":"medium","claim":"unverified medium","status":"unverified"},
                    {"severity":"low","claim":"confirmed low","status":"confirmed"}
                  ]
                }
                """);

            var settled = SettledExecution(executionId, "claude", "claude-opus-5", Start, worker: "review");
            var populated = Assert.Single(CostLedgerStore.BuildEntries(settled, room, Repository));

            Assert.Equal("BLOCK", populated.Verdict);
            Assert.Equal(1, populated.FindingsHigh);
            Assert.Equal(0, populated.FindingsMedium);
            Assert.Equal(1, populated.FindingsLow);
            Assert.Equal(2001, populated.ReviewedPr);
            Assert.Equal("abcdef1234567890", populated.ReviewedHead);

            var populatedJson = JsonSerializer.Serialize(populated);
            Assert.Contains("\"verdict\":\"BLOCK\"", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"findingsHigh\":1", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"findingsMedium\":0", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"findingsLow\":1", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"reviewedPr\":2001", populatedJson, StringComparison.Ordinal);
            Assert.Contains("\"reviewedHead\":\"abcdef1234567890\"", populatedJson, StringComparison.Ordinal);

            File.WriteAllText(
                verdictPath,
                """
                {
                  "reviewedRef": "abcdef1234567890",
                  "findings": []
                }
                """);
            var approved = Assert.Single(CostLedgerStore.BuildEntries(settled, room, Repository));
            Assert.Equal("APPROVE", approved.Verdict);
            Assert.Equal(0, approved.FindingsHigh);
            Assert.Equal(0, approved.FindingsMedium);
            Assert.Equal(0, approved.FindingsLow);
            Assert.Null(approved.ReviewedPr);
            Assert.Equal("abcdef1234567890", approved.ReviewedHead);

            var approvedJson = JsonSerializer.Serialize(approved);
            Assert.Contains("\"verdict\":\"APPROVE\"", approvedJson, StringComparison.Ordinal);
            Assert.Contains("\"findingsHigh\":0", approvedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"reviewedPr\"", approvedJson, StringComparison.Ordinal);

            FileCleanup.Delete(verdictPath);
            var absent = Assert.Single(CostLedgerStore.BuildEntries(settled, room, Repository));
            Assert.Null(absent.Verdict);
            Assert.Null(absent.FindingsHigh);
            Assert.Null(absent.FindingsMedium);
            Assert.Null(absent.FindingsLow);
            Assert.Null(absent.ReviewedPr);
            Assert.Null(absent.ReviewedHead);

            var absentJson = JsonSerializer.Serialize(absent);
            Assert.DoesNotContain("\"verdict\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"findingsHigh\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"findingsMedium\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"findingsLow\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"reviewedPr\"", absentJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"reviewedHead\"", absentJson, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public async Task A_resolution_appends_a_physical_correction_but_logical_reads_count_the_attempt_once()
    {
        var room = NewRoom();
        var ledgerPath = NewLedgerPath();
        try
        {
            var original = new CostLedgerEntry(
                CostSourceKind.BatonExecution,
                Room: BatonPaths.RecordKey(room),
                Execution: "exec-resolved",
                TokensIn: 12);
            await CostLedgerStore.AppendAsync([original], ledgerPath, TestContext.Current.CancellationToken);

            Assert.True(await CostLedgerStore.AppendResolutionAsync(
                room, "reject", "manual repair did not satisfy the contract", ledgerPath,
                TestContext.Current.CancellationToken));

            var physicalLines = await File.ReadAllLinesAsync(ledgerPath, TestContext.Current.CancellationToken);
            Assert.Equal(2, physicalLines.Length);
            Assert.DoesNotContain("\"resolution\"", physicalLines[0], StringComparison.Ordinal);
            Assert.Contains("\"resolution\":\"reject\"", physicalLines[1], StringComparison.Ordinal);
            Assert.Contains(
                "\"resolutionReason\":\"manual repair did not satisfy the contract\"",
                physicalLines[1],
                StringComparison.Ordinal);

            var logical = Assert.Single(await CostLedgerStore.ReadAllAsync(
                ledgerPath, TestContext.Current.CancellationToken));
            Assert.Equal("reject", logical.Resolution);
            Assert.Equal("manual repair did not satisfy the contract", logical.ResolutionReason);
            Assert.Equal(12, logical.TokensIn);
        }
        finally
        {
            FileCleanup.Delete(ledgerPath);
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

}
