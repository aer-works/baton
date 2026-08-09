using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Xunit;

namespace Aer.Flow.Tests.Mutation;

public sealed class RuntimePermissionMutationTests : IDisposable
{
    private readonly string _tempDirectory;

    public RuntimePermissionMutationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"runtime-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public async Task RaisePermissionAsync_is_idempotent()
    {
        var logPath = Path.Combine(_tempDirectory, "room.jsonl");
        var reader = new RoomEventLogReader(logPath);
        await using var writer = new RoomEventLogWriter(logPath);

        var reqId = "req-idempotent-1";
        var execId = new ExecutionId("ex-1");
        var stepId = new StepId("st-1");

        var state1 = await RoomMutationInterface.RaisePermissionAsync(
            _tempDirectory, reader, writer, reqId, execId, stepId, "w-1", "claude", "corr-1", "ReadFile", "{}", "ReadFile");

        Assert.NotNull(state1.PendingPermission);
        Assert.Equal(reqId, state1.PendingPermission.PermissionRequestId);

        var state2 = await RoomMutationInterface.RaisePermissionAsync(
            _tempDirectory, reader, writer, reqId, execId, stepId, "w-1", "claude", "corr-1", "ReadFile", "{}", "ReadFile");

        var events = await reader.ReadAllRoomEventsAsync();
        var askedCount = events.OfType<RoomEvent.RuntimePermissionAsked>().Count(a => a.PermissionRequestId == reqId);
        Assert.Equal(1, askedCount);
    }

    [Fact]
    public async Task AnswerPermissionAsync_is_idempotent()
    {
        var logPath = Path.Combine(_tempDirectory, "room.jsonl");
        var reader = new RoomEventLogReader(logPath);
        await using var writer = new RoomEventLogWriter(logPath);

        var reqId = "req-idempotent-2";
        var execId = new ExecutionId("ex-1");
        var stepId = new StepId("st-1");

        await RoomMutationInterface.RaisePermissionAsync(
            _tempDirectory, reader, writer, reqId, execId, stepId, "w-1", "claude", "corr-1", "ReadFile", "{}", "ReadFile");

        var state1 = await RoomMutationInterface.AnswerPermissionAsync(
            _tempDirectory, reader, writer, reqId, "AllowOnce", "{}", "ok", "human");

        Assert.Null(state1.PendingPermission);

        var state2 = await RoomMutationInterface.AnswerPermissionAsync(
            _tempDirectory, reader, writer, reqId, "AllowOnce", "{}", "ok", "human");

        var events = await reader.ReadAllRoomEventsAsync();
        var answeredCount = events.OfType<RoomEvent.RuntimePermissionAnswered>().Count(a => a.PermissionRequestId == reqId);
        Assert.Equal(1, answeredCount);
    }
}
