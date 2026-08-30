using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Projection;
using Baton.Flow.Store;

namespace Baton.Flow.Tests.Mutation;

public class MemoryProposalEscalationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;
    private readonly string _captureDirectory;

    public MemoryProposalEscalationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_memory_proposal_esc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
        _captureDirectory = Path.Combine(_tempDirectory, "memory-proposals");
    }

    /// <summary>#833. <see cref="MemoryProposalEscalation.CaptureDirectoryName"/>'s own remarks explain the cross-boundary duplication this pins.</summary>
    [Fact]
    public void CaptureDirectoryName_is_the_literal_mirrored_on_the_Baton_Mcp_Host_side()
    {
        Assert.Equal("memory-proposals", MemoryProposalEscalation.CaptureDirectoryName);
    }

    [Fact]
    public async Task A_captured_proposal_becomes_visible_held_work_in_the_room()
    {
        Directory.CreateDirectory(_captureDirectory);
        var captureFile = Path.Combine(_captureDirectory, "proposal-abc.json");
        await File.WriteAllTextAsync(
            captureFile,
            """{"Operation":"add","TargetPath":"new-fact.md","Content":"the fact","Rationale":"learned it"}""",
            TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var state = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);

        var @ref = new HeldWorkRef(Path.GetFullPath(captureFile));
        Assert.Single(state.HeldWork);
        Assert.Equal(HeldWorkStatus.Dispatched, state.HeldWork[@ref].Status);
        Assert.Equal(MemoryProposalEscalation.MemoryProposalShape, state.HeldWork[@ref].Shape);
        Assert.Equal("operator", state.HeldWork[@ref].DeciderIdentity);
    }

    [Fact]
    public async Task Running_twice_against_the_same_capture_does_not_re_dispatch()
    {
        Directory.CreateDirectory(_captureDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_captureDirectory, "proposal-abc.json"),
            """{"Operation":"delete","TargetPath":"stale.md","Content":null,"Rationale":"superseded"}""",
            TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var first = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);
        var second = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);

        Assert.Single(first.HeldWork);
        Assert.Single(second.HeldWork);
    }

    [Fact]
    public async Task No_capture_directory_yields_no_held_work_and_does_not_throw()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var state = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken);

        Assert.Empty(state.HeldWork);
    }

    /// <summary>
    /// #851 (#833 review finding): a lost dispatch race must not abort the rest of the sweep. The
    /// idempotency projection is read before the room lock, so a concurrent sweeper can dispatch
    /// the same ref between that read and the mutation's own re-read under the lock — the stale
    /// first read below IS that window, made deterministic. The colliding file's dispatch throws
    /// inside <see cref="RoomMutationInterface"/>; the sweep must treat "already dispatched" as
    /// the goal state and keep going, so the remaining capture files still land this pass rather
    /// than waiting for the next one.
    /// </summary>
    [Fact]
    public async Task A_lost_dispatch_race_on_one_capture_does_not_abort_the_rest_of_the_sweep()
    {
        Directory.CreateDirectory(_captureDirectory);
        var collidingFile = Path.Combine(_captureDirectory, "proposal-aaa.json");
        var freshFile = Path.Combine(_captureDirectory, "proposal-bbb.json");
        await File.WriteAllTextAsync(
            collidingFile,
            """{"Operation":"add","TargetPath":"raced.md","Content":"x","Rationale":"y"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            freshFile,
            """{"Operation":"add","TargetPath":"fresh.md","Content":"x","Rationale":"y"}""",
            TestContext.Current.CancellationToken);

        var realReader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        // The concurrent sweeper wins the race: the colliding ref is already in the journal.
        await RoomMutationInterface.DispatchHeldWorkAsync(
            _tempDirectory, new HeldWorkRef(Path.GetFullPath(collidingFile)),
            MemoryProposalEscalation.MemoryProposalShape, MemoryProposalEscalation.NoBudget,
            "operator", realReader, writer, TestContext.Current.CancellationToken);

        var staleReader = new StaleFirstReadRoomEventLogReader(realReader);

        var state = await MemoryProposalEscalation.EscalateNewProposalsAsync(
            _captureDirectory, _tempDirectory, "operator", staleReader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(2, state.HeldWork.Count);
        Assert.Equal(
            HeldWorkStatus.Dispatched,
            state.HeldWork[new HeldWorkRef(Path.GetFullPath(freshFile))].Status);
    }

    /// <summary>
    /// Simulates the race window for the test above: the first projection read returns nothing
    /// (stale), every later read sees the journal as it really is.
    /// </summary>
    private sealed class StaleFirstReadRoomEventLogReader(IRoomEventLogReader inner) : IRoomEventLogReader
    {
        private bool _first = true;

        public async Task<IReadOnlyList<RoomEvent>> ReadAllRoomEventsAsync(CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                return [];
            }

            return await inner.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task A_relative_capture_directory_is_refused_because_the_full_path_is_the_idempotency_key()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => MemoryProposalEscalation.EscalateNewProposalsAsync(
            "relative/captures", _tempDirectory, "operator", reader, writer, TestContext.Current.CancellationToken));

        Assert.Contains("rooted", ex.Message);
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_tempDirectory);
    }
}

/// <summary>
/// #833: per-execution capture, and the room-attribution polarity it exists to prove. Uses its own
/// temp directory pair rather than <see cref="MemoryProposalEscalationTests"/>'s single-directory
/// fixture, because the whole point here is two SEPARATE room directories.
/// </summary>
public class MemoryProposalEscalationForRoomTests : IDisposable
{
    private readonly string _tempDirectory;

    public MemoryProposalEscalationForRoomTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_memory_proposal_room_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    private static void WriteCapture(string roomDirectory, string executionId, string fileName)
    {
        var captureDir = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId}", "memory-proposals");
        Directory.CreateDirectory(captureDir);
        File.WriteAllText(
            Path.Combine(captureDir, fileName),
            """{"Operation":"add","TargetPath":"fact.md","Content":"x","Rationale":"y"}""");
    }

    /// <summary>
    /// THE defect this issue fixes, proven directly: two rooms, one execution captured under each.
    /// Sweeping room A must dispatch only room A's proposal into room A's journal — room B's capture
    /// must be invisible to it, and vice versa. This is impossible to state, let alone pass, against
    /// #801's static shared directory (every room's captures lived in the exact same directory, with
    /// no room-scoped subtree to sweep) -- there was no way to even express "room A's captures" as
    /// distinct from "room B's". Per-execution capture under each room's own room directory is what
    /// makes the two directory trees physically disjoint, which is what this test actually exercises.
    /// </summary>
    [Fact]
    public async Task Two_rooms_each_see_only_their_own_proposals_when_swept()
    {
        var roomA = Path.Combine(_tempDirectory, "room-a");
        var roomB = Path.Combine(_tempDirectory, "room-b");
        Directory.CreateDirectory(roomA);
        Directory.CreateDirectory(roomB);

        WriteCapture(roomA, "aaaa", "proposal-a1.json");
        WriteCapture(roomB, "bbbb", "proposal-b1.json");

        var readerA = new RoomEventLogReader(Path.Combine(roomA, "room.jsonl"));
        await using var writerA = new RoomEventLogWriter(Path.Combine(roomA, "room.jsonl"));
        var readerB = new RoomEventLogReader(Path.Combine(roomB, "room.jsonl"));
        await using var writerB = new RoomEventLogWriter(Path.Combine(roomB, "room.jsonl"));

        var stateA = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
            roomA, "operator", readerA, writerA, TestContext.Current.CancellationToken);
        var stateB = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
            roomB, "operator", readerB, writerB, TestContext.Current.CancellationToken);

        Assert.Single(stateA.HeldWork);
        Assert.Contains("proposal-a1.json", stateA.HeldWork.Keys.Single().Value);
        Assert.Single(stateB.HeldWork);
        Assert.Contains("proposal-b1.json", stateB.HeldWork.Keys.Single().Value);

        // Explicit both-directions assertion (the gate's "assert polarity in both directions" —
        // one condition apart is exactly where a subtle attribution bug would hide): room A's ref
        // set must not contain room B's file, and vice versa.
        Assert.DoesNotContain(stateA.HeldWork.Keys, @ref => @ref.Value.Contains("proposal-b1.json"));
        Assert.DoesNotContain(stateB.HeldWork.Keys, @ref => @ref.Value.Contains("proposal-a1.json"));
    }

    /// <summary>
    /// Multiple executions under the SAME room all contribute their captures to that one room —
    /// the sweep is not limited to a single execution directory.
    /// </summary>
    [Fact]
    public async Task Multiple_executions_in_one_room_all_contribute_captures()
    {
        var room = Path.Combine(_tempDirectory, "room-multi");
        Directory.CreateDirectory(room);
        WriteCapture(room, "exec1", "proposal-1.json");
        WriteCapture(room, "exec2", "proposal-2.json");

        var reader = new RoomEventLogReader(Path.Combine(room, "room.jsonl"));
        await using var writer = new RoomEventLogWriter(Path.Combine(room, "room.jsonl"));

        var state = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
            room, "operator", reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(2, state.HeldWork.Count);
    }

    /// <summary>No artifacts directory at all (a brand-new room) sweeps to no-op, never throws.</summary>
    [Fact]
    public async Task No_artifacts_directory_yields_no_held_work_and_does_not_throw()
    {
        var room = Path.Combine(_tempDirectory, "room-empty");
        Directory.CreateDirectory(room);

        var reader = new RoomEventLogReader(Path.Combine(room, "room.jsonl"));
        await using var writer = new RoomEventLogWriter(Path.Combine(room, "room.jsonl"));

        var state = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
            room, "operator", reader, writer, TestContext.Current.CancellationToken);

        Assert.Empty(state.HeldWork);
    }

    /// <summary>
    /// #1039 / #1025 (the load-bearing invariant the retention sweep rests on). The escalation loop
    /// dedups on <c>HeldWork.ContainsKey(@ref)</c>, and the projector only holds a resolved ref while
    /// its resolve event is still in the journal. Journal compaction (what #1025 automates) drops that
    /// event — so the dedup key vanishes. Resolving a proposal therefore now CONSUMES its capture file
    /// (<see cref="MemoryProposalResolution"/>), making the path-derived ref genuinely one-shot: after
    /// compaction there is no file left to re-escalate.
    /// <para>
    /// The weaker "re-dispatch a fresh ref after compaction" test this replaces passed whether or not
    /// the file was consumed, because it never touched the resolved ref's own path. The positive-control
    /// arm here restores the capture file and asserts the same post-compaction sweep DOES re-dispatch it,
    /// so the primary arm's "no re-dispatch" is a real observation and not a vacuous pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_resolved_proposal_is_not_re_dispatched_after_the_journal_is_compacted()
    {
        var room = Path.Combine(_tempDirectory, "room-compact");
        Directory.CreateDirectory(room);
        WriteCapture(room, "exec1", "proposal-1.json");
        var captureFile = Path.GetFullPath(
            Path.Combine(room, "artifacts", "execution_exec1", "memory-proposals", "proposal-1.json"));
        var @ref = new HeldWorkRef(captureFile);
        var roomLogPath = Path.Combine(room, "room.jsonl");

        // Dispatch it, then resolve it. Reject (approve: false) skips the memory apply but still
        // consumes the capture file — exactly the path whose durability we are pinning.
        {
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);
            var dispatched = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
                room, "operator", reader, writer, TestContext.Current.CancellationToken);
            Assert.Equal(HeldWorkStatus.Dispatched, dispatched.HeldWork[@ref].Status);

            var resolved = await MemoryProposalResolution.ResolveAsync(
                room, @ref, approve: false, reader, writer, TestContext.Current.CancellationToken);
            Assert.Equal(HeldWorkStatus.Resolved, resolved.HeldWork[@ref].Status);
        }

        // The consume-on-resolve fix removes the capture file.
        Assert.False(File.Exists(captureFile));

        // Compaction drops the resolved dispatch/resolve pair, so the dedup key is gone from the journal.
        Assert.True(await RoomJournalCompactor.CompactAsync(room, TestContext.Current.CancellationToken));
        {
            var reader = new RoomEventLogReader(roomLogPath);
            var afterCompaction = RoomProjector.Project(
                await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
            Assert.False(afterCompaction.HeldWork.ContainsKey(@ref));
        }

        // Primary arm: with the file consumed, the post-compaction sweep finds nothing to re-dispatch.
        {
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);
            var reSwept = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
                room, "operator", reader, writer, TestContext.Current.CancellationToken);
            Assert.Empty(reSwept.HeldWork);
        }

        // Positive control: restore the capture file (the un-consumed pre-#1039 state) and the SAME
        // post-compaction sweep re-dispatches it — proving this test can observe a re-dispatch, so the
        // primary arm's absence of one is the fix at work, not an inert assertion.
        {
            await File.WriteAllTextAsync(
                captureFile,
                """{"Operation":"add","TargetPath":"fact.md","Content":"x","Rationale":"y"}""",
                TestContext.Current.CancellationToken);
            var reader = new RoomEventLogReader(roomLogPath);
            await using var writer = new RoomEventLogWriter(roomLogPath);
            var reSweptWithFile = await MemoryProposalEscalation.EscalateNewProposalsForRoomAsync(
                room, "operator", reader, writer, TestContext.Current.CancellationToken);
            Assert.Equal(HeldWorkStatus.Dispatched, reSweptWithFile.HeldWork[@ref].Status);
        }
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_tempDirectory);
    }
}
