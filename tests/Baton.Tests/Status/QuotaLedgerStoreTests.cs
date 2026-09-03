using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// spec/baton.md §7's writer/reader: <see cref="QuotaLedgerStore"/> is the fleet-level burn ledger
/// (issue #1570), sharing <see cref="MutexGuardedFileLock"/> with <c>RoomRegistryStore</c> rather than
/// a second concurrency mechanism. <see cref="QuotaLedgerStore.BuildEntries"/>'s coverage mirrors
/// <c>ExecutionUsageProjectorTests</c>' fixture style deliberately — it wraps
/// <c>ExecutionUsageProjector.BuildByExecutionId</c> rather than re-deriving usage, so this file only
/// pins the ledger-specific additions (adapter/model/outcome resolution, room/execution/at), not the
/// wall-clock/token-absence rules that file already owns.
/// </summary>
public sealed class QuotaLedgerStoreTests
{
    private static string TempLedgerPath() =>
        Path.Combine(Path.GetTempPath(), $"baton-quota-ledger-{Guid.NewGuid():N}.jsonl");

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker, string? adapter, string? model) => new(
        executionId,
        new WorkflowId("wf-ledger-test"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: adapter,
        Model: model);

    [Fact]
    public void BuildEntries_reads_adapter_model_usage_and_outcome_from_a_settled_execution()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1");
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var exit = start.AddMilliseconds(4200);

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "claude", model: "sonnet"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 111), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), exit),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(executionId)),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            var entry = Assert.Single(built);
            Assert.Equal("exec-1", entry.Execution);
            Assert.Equal(BatonPaths.RecordKey(testRoot), entry.Room);
            Assert.Equal("claude", entry.Adapter);
            Assert.Equal("sonnet", entry.Model);
            Assert.Equal(4200, entry.WallClockMs);
            Assert.Equal("Succeeded", entry.Outcome);
            Assert.Equal(exit, entry.At);
            Assert.Null(entry.TokensIn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void BuildEntries_reports_the_FailureClassification_member_name_as_outcome()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-exhausted");
            var start = DateTime.UtcNow;

            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "agy", model: "gemini-3-pro"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 1, CoreExitReason.Natural), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil)),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            var entry = Assert.Single(built);
            Assert.Equal("ExhaustedUntil", entry.Outcome);
            Assert.Equal("agy", entry.Adapter);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void BuildEntries_is_absent_for_an_execution_with_no_exit_event()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"ledger-build-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-running");
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                    AcceptedRequest(executionId, "plan", adapter: "claude", model: "sonnet"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), DateTime.UtcNow),
            };

            var built = QuotaLedgerStore.BuildEntries(entries, testRoot);

            Assert.Empty(built);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task ReadDistinctByExecutionAsync_folds_a_pre_existing_duplicate_line_to_the_last_write()
    {
        // AppendAsync's own dedupe (see its doc comment) means a duplicate line can no longer be
        // created through the store's own API -- this test writes the file directly to prove the
        // read-time fold still recovers gracefully from one anyway (a hand-edited file, or a line
        // written before that dedupe existed).
        var path = TempLedgerPath();
        try
        {
            var first = System.Text.Json.JsonSerializer.Serialize(new QuotaLedgerEntry(Execution: "exec-1", TokensIn: 100));
            var second = System.Text.Json.JsonSerializer.Serialize(new QuotaLedgerEntry(Execution: "exec-1", TokensIn: 250));
            await File.WriteAllTextAsync(path, first + "\n" + second + "\n", TestContext.Current.CancellationToken);

            var distinct = await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(distinct);
            Assert.Equal(250, entry.TokensIn);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_appends_from_many_tasks_lose_no_entries()
    {
        var path = TempLedgerPath();
        try
        {
            const int writerCount = 12;
            var executionIds = Enumerable.Range(0, writerCount).Select(i => $"exec-concurrent-{i}").ToList();

            await Task.WhenAll(executionIds.Select(id => Task.Run(() =>
                QuotaLedgerStore.AppendAsync(
                    [new QuotaLedgerEntry(Execution: id, TokensIn: 1)], path, TestContext.Current.CancellationToken))));

            var all = await QuotaLedgerStore.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(writerCount, all.Count);
            var found = all.Select(e => e.Execution).ToHashSet(StringComparer.Ordinal);
            Assert.All(executionIds, id => Assert.Contains(id, found));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task AppendAsync_throws_a_sanctioned_exception_when_the_ledger_path_is_itself_a_directory()
    {
        // The fail-open contract's own instrument: Program.cs's settle-time call site catches exactly
        // IOException/UnauthorizedAccessException/WaitHandleCannotBeOpenedException and swallows them.
        // Pointing the "file" path at a real directory forces the FileStream open to throw one of those
        // with no mock or injected writer -- proving the exception this store's contract promises is
        // one the caller's catch clause actually reaches.
        var path = Path.Combine(Path.GetTempPath(), $"baton-quota-ledger-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-1", TokensIn: 1)], path, TestContext.Current.CancellationToken));

            Assert.True(
                ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException,
                $"Expected one of the three sanctioned fail-open exceptions, got {ex.GetType()}: {ex.Message}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_preserves_a_ledger_line_whose_room_the_fresh_walk_did_not_reach()
    {
        // #1570 review (advisor pass): a rebuild sourced from the walk alone would delete every entry
        // whose room RoomRetentionSweep already pruned -- precisely the data the ledger exists to hold
        // past retention. This pins that RebuildAsync starts from the ledger's own content.
        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "gone-1", Room: "C:/rooms/pruned-already", TokensIn: 999)],
                path,
                TestContext.Current.CancellationToken);

            var freshEntries = new List<QuotaLedgerEntry> { new(Execution: "fresh-1", TokensIn: 42) };
            var result = await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);

            Assert.Equal(1, result.PreviousCount);
            Assert.Equal(2, result.TotalCount);
            Assert.Equal(1, result.RecoveredCount);

            var distinct = await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken);
            var byExecution = distinct.ToDictionary(e => e.Execution!, StringComparer.Ordinal);
            Assert.Equal(999, byExecution["gone-1"].TokensIn);
            Assert.Equal(42, byExecution["fresh-1"].TokensIn);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_run_twice_against_the_same_fleet_yields_identical_nonzero_totals()
    {
        var path = TempLedgerPath();
        try
        {
            var freshEntries = new List<QuotaLedgerEntry>
            {
                new(Execution: "exec-a", TokensIn: 100),
                new(Execution: "exec-b", TokensIn: 250),
            };

            await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);
            var firstTotal = (await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken))
                .Sum(e => e.TokensIn ?? 0);

            await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);
            var secondTotal = (await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken))
                .Sum(e => e.TokensIn ?? 0);

            Assert.Equal(350, firstTotal);
            Assert.Equal(firstTotal, secondTotal);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_a_fresh_entry_overwrites_the_ledgers_existing_entry_for_the_same_execution()
    {
        // The documented merge rule (QuotaLedgerStore.RebuildAsync's own remarks): a freshly-walked
        // entry for an execution the ledger already had OVERWRITES it, not the reverse. Pinned
        // directly, not just inferred from the "identical totals across two runs" test above.
        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10)], path, TestContext.Current.CancellationToken);

            var freshEntries = new List<QuotaLedgerEntry> { new(Execution: "exec-a", TokensIn: 999) };
            await QuotaLedgerStore.RebuildAsync(freshEntries, path, TestContext.Current.CancellationToken);

            var distinct = await QuotaLedgerStore.ReadDistinctByExecutionAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(distinct);
            Assert.Equal(999, entry.TokensIn);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task AppendAsync_never_duplicates_a_line_for_an_execution_id_already_in_the_ledger()
    {
        // #1570 review (advisor pass): Program.cs's settle-time call site fires on every command that
        // carries a room to Terminal -- a re-run, `supply`, or `resolve --reject` re-reaching Terminal
        // all re-derive the WHOLE room's executions via BuildEntries, not just what is new. Without
        // this dedupe, a room settling twice would double the line for an execution that never
        // changed.
        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10)], path, TestContext.Current.CancellationToken);
            // Simulates BuildEntries re-deriving the same, already-settled execution on a second
            // Terminal-reaching command against the same room.
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10)], path, TestContext.Current.CancellationToken);

            var all = await QuotaLedgerStore.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(all);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task AppendAsync_still_appends_an_unrelated_execution_alongside_an_already_recorded_one()
    {
        var path = TempLedgerPath();
        try
        {
            await QuotaLedgerStore.AppendAsync(
                [new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10)], path, TestContext.Current.CancellationToken);
            await QuotaLedgerStore.AppendAsync(
                [
                    new QuotaLedgerEntry(Execution: "exec-a", TokensIn: 10),
                    new QuotaLedgerEntry(Execution: "exec-b", TokensIn: 20),
                ],
                path,
                TestContext.Current.CancellationToken);

            var all = await QuotaLedgerStore.ReadAllAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Execution == "exec-a");
            Assert.Contains(all, e => e.Execution == "exec-b");
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
