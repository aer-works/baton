using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Projection;
using Baton.Flow.Store;

namespace Baton.Flow.Tests.Mutation;

public class RoomMutationInterfaceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;

    public RoomMutationInterfaceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_room_mut_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
    }

    // Rooted on both platforms: DispatchHeldWorkAsync refuses relative lane paths (the 798
    // review's low finding -- a relative ref resolves against whichever process reads it).
    private HeldWorkRef RootedLaneRef(string name) => new(Path.Combine(_tempDirectory, "lanes", name));

    [Fact]
    public async Task Mutation_interface_dispatches_escalates_and_resolves_held_work()
    {
        var laneRef = RootedLaneRef("lane-1");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var state1 = await RoomMutationInterface.DispatchHeldWorkAsync(_tempDirectory, laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-alice", reader, writer, TestContext.Current.CancellationToken);

        Assert.Single(state1.HeldWork);
        Assert.Equal(HeldWorkStatus.Dispatched, state1.HeldWork[laneRef].Status);

        var state2 = await RoomMutationInterface.EscalateHeldWorkAsync(_tempDirectory, laneRef, "op-bob", reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(HeldWorkStatus.Escalated, state2.HeldWork[laneRef].Status);
        Assert.Equal("op-bob", state2.HeldWork[laneRef].EscalatedTo);

        var citation = new HeldWorkCitation("exec-1", "executionSucceeded", 1);
        var state3 = await RoomMutationInterface.ResolveHeldWorkAsync(_tempDirectory, laneRef, citation, reader, writer, TestContext.Current.CancellationToken);

        Assert.Equal(HeldWorkStatus.Resolved, state3.HeldWork[laneRef].Status);
        Assert.Equal(citation, state3.HeldWork[laneRef].Citation);
    }

    [Fact]
    public async Task Dispatching_duplicate_ref_throws_InvalidRoomMutationException()
    {
        var laneRef = RootedLaneRef("lane-1");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        await RoomMutationInterface.DispatchHeldWorkAsync(_tempDirectory, laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-alice", reader, writer, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(async () =>
            await RoomMutationInterface.DispatchHeldWorkAsync(_tempDirectory, laneRef, "shape-2", TimeSpan.FromMinutes(5), "op-alice", reader, writer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Escalating_unknown_ref_throws_InvalidRoomMutationException()
    {
        var laneRef = RootedLaneRef("unknown");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(async () =>
            await RoomMutationInterface.EscalateHeldWorkAsync(_tempDirectory, laneRef, "op-bob", reader, writer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolving_already_resolved_ref_throws_InvalidRoomMutationException()
    {
        var laneRef = RootedLaneRef("lane-1");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        await RoomMutationInterface.DispatchHeldWorkAsync(_tempDirectory, laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-alice", reader, writer, TestContext.Current.CancellationToken);

        var citation = new HeldWorkCitation("exec-1", "executionSucceeded", 1);
        await RoomMutationInterface.ResolveHeldWorkAsync(_tempDirectory, laneRef, citation, reader, writer, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(async () =>
            await RoomMutationInterface.ResolveHeldWorkAsync(_tempDirectory, laneRef, citation, reader, writer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolving_unknown_ref_throws_InvalidRoomMutationException()
    {
        var laneRef = RootedLaneRef("unknown");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        var citation = new HeldWorkCitation("exec-1", "executionSucceeded", 1);
        await Assert.ThrowsAsync<InvalidRoomMutationException>(async () =>
            await RoomMutationInterface.ResolveHeldWorkAsync(_tempDirectory, laneRef, citation, reader, writer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Escalating_already_resolved_ref_throws_InvalidRoomMutationException()
    {
        var laneRef = RootedLaneRef("lane-1");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        await RoomMutationInterface.DispatchHeldWorkAsync(_tempDirectory, laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-alice", reader, writer, TestContext.Current.CancellationToken);
        var citation = new HeldWorkCitation("exec-1", "executionSucceeded", 1);
        await RoomMutationInterface.ResolveHeldWorkAsync(_tempDirectory, laneRef, citation, reader, writer, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(async () =>
            await RoomMutationInterface.EscalateHeldWorkAsync(_tempDirectory, laneRef, "op-bob", reader, writer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispatching_a_relative_lane_path_throws_InvalidRoomMutationException()
    {
        var laneRef = new HeldWorkRef("lanes/lane-relative");
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(async () =>
            await RoomMutationInterface.DispatchHeldWorkAsync(_tempDirectory, laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-alice", reader, writer, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_tempDirectory);
    }
}
