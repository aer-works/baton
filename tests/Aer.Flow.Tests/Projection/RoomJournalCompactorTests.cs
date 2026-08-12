using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Projection;

public class RoomJournalCompactorTests
{
    private static async Task<string> CreateTestRoomAsync(params RoomEvent[] events)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_compactor_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);

        var roomLogPath = Path.Combine(tempDir, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            foreach (var evt in events)
            {
                await writer.AppendAsync(evt, TestContext.Current.CancellationToken);
            }
        }

        return tempDir;
    }

    [Fact]
    public async Task CompactAsync_shrinks_journal_carrying_completed_runs()
    {
        var refCompleted = new HeldWorkRef("lane-completed");
        var refLive = new HeldWorkRef("lane-live");

        var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
        var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
        var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

        var roomDir = await CreateTestRoomAsync(dispatchCompleted, resolveCompleted, dispatchLive);
        try
        {
            var readerInitial = new RoomEventLogReader(Path.Combine(roomDir, "room.jsonl"));
            var initialEvents = await readerInitial.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, initialEvents.Count);

            var compacted = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.True(compacted);

            var readerCompacted = new RoomEventLogReader(Path.Combine(roomDir, "room.jsonl"));
            var compactedEvents = await readerCompacted.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Single(compactedEvents);
            Assert.IsType<RoomEvent.HeldWorkDispatched>(compactedEvents[0]);
            Assert.Equal(refLive, ((RoomEvent.HeldWorkDispatched)compactedEvents[0]).Ref);

            var roomState = RoomProjector.Project(compactedEvents);
            Assert.True(roomState.HeldWork.ContainsKey(refLive));
            Assert.False(roomState.HeldWork.ContainsKey(refCompleted));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task CompactAsync_leaves_journal_with_only_live_runs_untouched()
    {
        var refLive = new HeldWorkRef("lane-live");
        var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");
        var escalatedLive = new RoomEvent.HeldWorkEscalated(refLive, "operator");

        var roomDir = await CreateTestRoomAsync(dispatchLive, escalatedLive);
        try
        {
            var compacted = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.False(compacted);

            var reader = new RoomEventLogReader(Path.Combine(roomDir, "room.jsonl"));
            var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task CompactAsync_is_noop_run_twice()
    {
        var refCompleted = new HeldWorkRef("lane-completed");
        var refLive = new HeldWorkRef("lane-live");

        var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
        var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));
        var dispatchLive = new RoomEvent.HeldWorkDispatched(refLive, "shape", TimeSpan.FromMinutes(5), "human");

        var roomDir = await CreateTestRoomAsync(dispatchCompleted, resolveCompleted, dispatchLive);
        try
        {
            var firstRun = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.True(firstRun);

            var secondRun = await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.False(secondRun);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// A cancelled compaction leaves the journal intact AND leaves no temp file behind. The first
    /// half is what "crash-safe" means; the second is what the write-failure path costs if nothing
    /// collects it. Cancellation is the one interruption a test can actually inject — a kill between
    /// write and rename is NOT simulated here, and that half of the claim rests on the temp-then-
    /// rename mechanism rather than on this test.
    /// </summary>
    [Fact]
    public async Task A_cancelled_compaction_leaves_the_journal_intact_and_no_temp_behind()
    {
        var refCompleted = new HeldWorkRef("lane-completed");
        var dispatchCompleted = new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(5), "human");
        var resolveCompleted = new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok"));

        var roomDir = await CreateTestRoomAsync(dispatchCompleted, resolveCompleted);
        try
        {
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            var before = await File.ReadAllTextAsync(roomLogPath, TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RoomJournalCompactor.CompactAsync(roomDir, cancelled.Token));

            Assert.Equal(before, await File.ReadAllTextAsync(roomLogPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(roomDir, "room.jsonl.tmp.*"));

            // The control: uncancelled, the same call really does rewrite this journal, so the
            // assertions above are about the cancellation and not about a no-op input.
            Assert.True(await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));
            Assert.NotEqual(before, await File.ReadAllTextAsync(roomLogPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
    /// <summary>
    /// The compaction lock is load-bearing, not decorative — <see cref="RoomJournalCompactor"/>'s
    /// own comment says what it protects. Serialised against room-event appenders (room-events lock),
    /// not against the flow engine's flow lock.
    /// </summary>
    [Fact]
    public async Task CompactAsync_refuses_while_the_room_lock_is_held_by_someone_else()
    {
        var refCompleted = new HeldWorkRef("lane-locked");
        var roomDir = await CreateTestRoomAsync(
            new RoomEvent.HeldWorkDispatched(refCompleted, "shape", TimeSpan.FromMinutes(1), "decider"),
            new RoomEvent.HeldWorkResolved(refCompleted, new HeldWorkCitation("Resolved", "ok")));

        var before = await File.ReadAllTextAsync(Path.Combine(roomDir, "room.jsonl"), TestContext.Current.CancellationToken);

        // Arm 1: Blocked by room-events lock
        using (Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test holder"))
        {
            await Assert.ThrowsAsync<Aer.Flow.Concurrency.WorkflowLockedException>(
                () => RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));
        }

        // Arm 2: NOT blocked by flow lock
        using (Aer.Flow.Concurrency.ConcurrencyGuard.Acquire(roomDir, "test flow holder"))
        {
            Assert.True(await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));
        }

        var after = await File.ReadAllTextAsync(Path.Combine(roomDir, "room.jsonl"), TestContext.Current.CancellationToken);
        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Generic-shape (non-memory-proposal) invariant: journal compaction must not break
    /// <see cref="RoomMutationInterface"/>'s own dispatch dedup. After a resolved run is compacted away,
    /// a genuinely fresh dispatch is admitted and re-dispatching that same fresh ref is still rejected.
    /// The memory-proposal path's compaction safety is pinned separately in
    /// <c>MemoryProposalEscalationTests.A_resolved_proposal_is_not_re_dispatched_after_the_journal_is_compacted</c>
    /// via the capture-file consume; this arm keeps coverage of the plain dispatch guard through compaction.
    /// </summary>
    [Fact]
    public async Task After_compaction_a_fresh_generic_dispatch_is_admitted_and_its_duplicate_still_rejected()
    {
        var resolvedDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aer_wf_resolved_" + Guid.NewGuid().ToString("n")));
        var refResolved = new HeldWorkRef(resolvedDir);
        var roomDir = await CreateTestRoomAsync(
            new RoomEvent.HeldWorkDispatched(refResolved, "generic-shape", TimeSpan.FromMinutes(5), "human"),
            new RoomEvent.HeldWorkResolved(refResolved, new HeldWorkCitation("Resolved", "ok")));
        try
        {
            Assert.True(await RoomJournalCompactor.CompactAsync(roomDir, TestContext.Current.CancellationToken));

            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);

            var freshDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aer_wf_fresh_" + Guid.NewGuid().ToString("n")));
            var refFresh = new HeldWorkRef(freshDir);

            var afterDispatch = await RoomMutationInterface.DispatchHeldWorkAsync(
                roomDir, refFresh, "generic-shape", TimeSpan.FromMinutes(5), "human",
                reader, writer, TestContext.Current.CancellationToken);
            Assert.True(afterDispatch.HeldWork.ContainsKey(refFresh));
            Assert.False(afterDispatch.HeldWork.ContainsKey(refResolved));

            await Assert.ThrowsAsync<InvalidRoomMutationException>(() =>
                RoomMutationInterface.DispatchHeldWorkAsync(
                    roomDir, refFresh, "generic-shape", TimeSpan.FromMinutes(5), "human",
                    reader, writer, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
}
