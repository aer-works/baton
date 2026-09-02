using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton room delete</c> (#1659): the operator ruling — "we definitely need a way to actually
/// delete stuff, not just hide it from the glass" — this verb removes the room directory, its
/// <c>room-registry.jsonl</c> lines, and records a deliverables tombstone (see
/// <see cref="RoomDeleteCommand"/>'s own remarks for what it cannot reach: the Cloudflare Worker's KV
/// deliverables index).
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RoomDeleteCommandTests
{
    private static string CreateTempHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "baton_room_delete_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempHome);
        return tempHome;
    }

    private static async Task WriteTerminalSentinelAsync(string roomDirectoryPath, string state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(roomDirectoryPath);
        await TerminalSentinelWriter.WriteAsync(
            roomDirectoryPath, new WorkflowStatusView(state, [], [], null), cancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_NonTerminalRoom_RefusesWithoutForce_AndLeavesTheDirectoryInPlace()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-not-terminal");
            Directory.CreateDirectory(roomDir); // no terminal.json -> not terminal

            var options = new RoomDeleteOptions(roomDir, KeepDeliverables: false, Force: false);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.Contains("has not reached a terminal state", ex.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(roomDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonTerminalRoom_WithForce_DeletesAnyway()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-not-terminal-forced");
            Directory.CreateDirectory(roomDir);

            var options = new RoomDeleteOptions(roomDir, KeepDeliverables: false, Force: true);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.True(result.DirectoryExisted);
            Assert.False(Directory.Exists(roomDir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TerminalRoom_RemovesDirectory_RegistryLine_AndWritesATombstone()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-terminal");
            await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(
                roomDir, tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true,
                cancellationToken: TestContext.Current.CancellationToken);

            var options = new RoomDeleteOptions(roomDir, KeepDeliverables: false, Force: false);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.True(result.DirectoryExisted);
            Assert.Equal(1, result.RegistryLinesRemoved);
            Assert.True(result.DeliverablesTombstoneWritten);

            Assert.False(Directory.Exists(roomDir));
            var remainingRegistryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(
                BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            Assert.Empty(remainingRegistryEntries);

            var tombstoneLine = (await File.ReadAllLinesAsync(BatonPaths.DeletedRoomsFile, TestContext.Current.CancellationToken))
                .Single(line => !string.IsNullOrWhiteSpace(line));
            var tombstone = System.Text.Json.JsonSerializer.Deserialize<DeletedRoomTombstone>(tombstoneLine)!;
            Assert.Equal(BatonPaths.RecordKey(roomDir), tombstone.RoomPath);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_KeepDeliverables_SkipsTheTombstoneButStillRemovesTheRoom()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-terminal-keep-deliverables");
            await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);

            var options = new RoomDeleteOptions(roomDir, KeepDeliverables: true, Force: false);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.False(result.DeliverablesTombstoneWritten);
            Assert.False(Directory.Exists(roomDir));
            Assert.False(File.Exists(BatonPaths.DeletedRoomsFile));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RoomDirectoryAlreadyGone_StillCleansUpTheRegistryLine()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var roomDir = Path.Combine(tempHome, "room-already-gone");
            await RoomRegistryStore.AppendAsync(
                roomDir, tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true,
                cancellationToken: TestContext.Current.CancellationToken);
            // Directory never created -> RefuseUnlessTerminalOrForced treats an absent directory as
            // having nothing left for a live engine to hold, so this must not refuse.

            var options = new RoomDeleteOptions(roomDir, KeepDeliverables: false, Force: false);
            var result = await RoomDeleteCommand.ExecuteAsync(options, TextWriter.Null, TestContext.Current.CancellationToken);

            Assert.False(result.DirectoryExisted);
            Assert.Equal(1, result.RegistryLinesRemoved);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }
}
