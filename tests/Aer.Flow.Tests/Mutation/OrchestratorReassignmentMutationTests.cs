using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// #592's engine half: the room's orchestrator reassignment is journaled by
/// <see cref="RoomMutationInterface.ReassignOrchestratorAsync"/>, refused while the room has work in
/// flight, refused for a worker that never joined this room, and a no-op (per the scoping pass's
/// ruling 3) when the target already holds the role.
/// </summary>
/// <remarks>
/// Mirrors <see cref="WorkflowSwitchMutationTests"/>'s shape: the refusal arms plus, per ruling 3's
/// test addendum, the polarity pair -- the journal stays unchanged after a no-op reassignment AND
/// gains exactly one new event after a real one, so a regression to always-append or never-append
/// fails either way.
/// </remarks>
public class OrchestratorReassignmentMutationTests : IDisposable
{
    private static readonly WorkerId Claude = new("claude");
    private static readonly WorkerId ClaudeTwo = new("claude-2");
    private static readonly WorkerId Stranger = new("never-joined");

    private readonly string _roomDirectory;
    private readonly string _roomLogPath;

    public OrchestratorReassignmentMutationTests()
    {
        _roomDirectory = Path.Combine(Path.GetTempPath(), "aer_orch_reassign_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_roomDirectory);
        _roomLogPath = Path.Combine(_roomDirectory, "room.jsonl");
    }

    /// <summary>Seeds the room's journal the way materialization does (#1305): two participants joined, the first implicitly the orchestrator.</summary>
    private async Task SeedTwoParticipantsAsync()
    {
        var writer = new RoomEventLogWriter(_roomLogPath);
        await using (writer)
        {
            var joinedAt = DateTimeOffset.UtcNow;
            await writer.AppendAsync(new RoomEvent.WorkerJoined(Claude, "claude", "claude", "sonnet", null, joinedAt), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new RoomEvent.OrchestratorAssigned(Claude, joinedAt), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new RoomEvent.WorkerJoined(ClaudeTwo, "claude-2", "claude", "sonnet", null, joinedAt), TestContext.Current.CancellationToken);
        }
    }

    private Task<RoomState> ReassignAsync(WorkerId workerId)
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        var writer = new RoomEventLogWriter(_roomLogPath);
        return ReassignCoreAsync(workerId, reader, writer);
    }

    private async Task<RoomState> ReassignCoreAsync(WorkerId workerId, IRoomEventLogReader reader, RoomEventLogWriter writer)
    {
        await using (writer)
        {
            return await RoomMutationInterface.ReassignOrchestratorAsync(
                _roomDirectory, workerId, reader, writer,
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private async Task<int> OrchestratorAssignedCountAsync()
    {
        var events = await new RoomEventLogReader(_roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        return events.OfType<RoomEvent.OrchestratorAssigned>().Count();
    }

    [Fact]
    public async Task A_room_whose_pump_is_alive_refuses_reassignment()
    {
        await SeedTwoParticipantsAsync();
        using var liveRun = ConcurrencyGuard.Acquire(_roomDirectory, "a live pump");

        var refusal = await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => ReassignAsync(ClaudeTwo));

        Assert.Contains("running", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await OrchestratorAssignedCountAsync());
    }

    /// <summary>The arm that discriminates: no lock held, so the identical journal permits reassignment.</summary>
    [Fact]
    public async Task A_room_whose_process_died_permits_reassignment()
    {
        await SeedTwoParticipantsAsync();
        Assert.False(ConcurrencyGuard.IsHeld(_roomDirectory));

        await ReassignAsync(ClaudeTwo);

        Assert.Equal(2, await OrchestratorAssignedCountAsync());
    }

    [Fact]
    public async Task A_worker_that_never_joined_this_room_is_refused()
    {
        await SeedTwoParticipantsAsync();

        var refusal = await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => ReassignAsync(Stranger));

        Assert.Contains(Stranger.Value, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, await OrchestratorAssignedCountAsync());
    }

    /// <summary>Ruling 3's no-op half: reassigning to the current holder appends nothing.</summary>
    [Fact]
    public async Task Reassigning_to_the_current_holder_appends_no_new_event()
    {
        await SeedTwoParticipantsAsync();

        await ReassignAsync(Claude);

        Assert.Equal(1, await OrchestratorAssignedCountAsync());
    }

    /// <summary>Ruling 3's other half, beside the no-op above: a real reassignment appends exactly one event -- the polarity pair that catches a regression to always-append or never-append.</summary>
    [Fact]
    public async Task Reassigning_to_a_different_participant_appends_exactly_one_event()
    {
        await SeedTwoParticipantsAsync();

        var state = await ReassignAsync(ClaudeTwo);

        Assert.Equal(2, await OrchestratorAssignedCountAsync());
        Assert.NotNull(state);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DirectoryCleanup.DeleteRecursively(_roomDirectory);
    }
}
