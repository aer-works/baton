using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Mutation;

public class RoomMutationLockTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;

    public RoomMutationLockTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_room_mut_lock_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch { }
        }
    }

    [Fact]
    public async Task RaisePermission_SucceedsWhileFlowLockHeld()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        // Acquire the FLOW lock on the room dir via the guard
        using var flowGuard = ConcurrencyGuard.Acquire(_tempDirectory, "test flow lock holder");

        var state = await RoomMutationInterface.RaisePermissionAsync(
            _tempDirectory,
            reader,
            writer,
            "req-lock-1",
            new ExecutionId("ex-1"),
            new StepId("step-1"),
            "worker-1",
            "claude",
            "correlation-1",
            "Bash",
            "{}",
            "shell",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(state.PendingPermission);
        Assert.Equal("req-lock-1", state.PendingPermission.PermissionRequestId);

        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        var asked = Assert.Single(events.OfType<RoomEvent.RuntimePermissionAsked>());
        Assert.Equal("req-lock-1", asked.PermissionRequestId);
    }

    [Fact]
    public async Task RaisePermission_FailsFastWhileRoomEventsLockHeld()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        // Acquire the ROOM-EVENTS lock on the room dir
        using var roomEventsGuard = ConcurrencyGuard.AcquireRoomEvents(_tempDirectory, "test room-events lock holder");

        await Assert.ThrowsAsync<WorkflowLockedException>(() =>
            RoomMutationInterface.RaisePermissionAsync(
                _tempDirectory,
                reader,
                writer,
                "req-lock-2",
                new ExecutionId("ex-2"),
                new StepId("step-2"),
                "worker-1",
                "claude",
                "correlation-2",
                "Bash",
                "{}",
                "shell",
                cancellationToken: TestContext.Current.CancellationToken));
    }
}
