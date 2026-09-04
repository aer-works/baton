using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton rooms prune</c> (#1659) — the batch form of <see cref="RoomDeleteCommand"/>, plus
/// unconditional registry compaction (dedupe, drop missing-directory lines). Dry-run (no <c>--yes</c>)
/// is the default and must mutate nothing at all, including the compaction.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public sealed class RoomsPruneCommandTests
{
    private static string CreateTempHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "baton_rooms_prune_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempHome);
        return tempHome;
    }

    private static async Task<string> CreateTerminalRoomAsync(
        string parentDir, string name, string state, CancellationToken cancellationToken)
    {
        var roomDir = Path.Combine(parentDir, name);
        Directory.CreateDirectory(roomDir);
        await TerminalSentinelWriter.WriteAsync(roomDir, new WorkflowStatusView(state, [], [], null), cancellationToken);
        return roomDir;
    }

    [Fact]
    public async Task ExecuteAsync_DryRunDefault_ListsCandidatesButMutatesNothing()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var terminalRoom = await CreateTerminalRoomAsync(tempHome, "terminal-room", WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);
            var nonTerminalRoom = Path.Combine(tempHome, "non-terminal-room");
            Directory.CreateDirectory(nonTerminalRoom);

            var registryPath = Path.Combine(tempHome, "room-registry.jsonl");
            // A duplicate line (#1657 dedupe gap) plus a line for a directory that no longer exists —
            // both must survive untouched under dry-run.
            await RoomRegistryStore.AppendAsync(terminalRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(terminalRoom, tempHome + "2", registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(nonTerminalRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            var missingRoomPath = Path.Combine(tempHome, "already-gone");
            await RoomRegistryStore.AppendAsync(missingRoomPath, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var beforeText = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);

            var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: null, State: null, DryRun: true, Yes: false);
            var result = await RoomsPruneCommand.ExecuteAsync(
                options, TextWriter.Null, registryPath, TestContext.Current.CancellationToken);

            var afterText = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
            Assert.Equal(beforeText, afterText); // dry-run mutates nothing, including compaction

            Assert.False(result.Executed);
            Assert.Empty(result.Deleted);
            var candidate = Assert.Single(result.Candidates);
            Assert.Equal(BatonPaths.RecordKey(terminalRoom), candidate.RoomDirectoryPath);

            Assert.True(Directory.Exists(terminalRoom));
            Assert.True(Directory.Exists(nonTerminalRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Yes_DeletesExactlyTheMatchingSet_AndCompactsTheRegistry()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var succeededRoom = await CreateTerminalRoomAsync(tempHome, "succeeded-room", WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);
            var failedRoom = await CreateTerminalRoomAsync(tempHome, "failed-room", WorkflowOutcome.Failed, TestContext.Current.CancellationToken);
            var nonTerminalRoom = Path.Combine(tempHome, "non-terminal-room");
            Directory.CreateDirectory(nonTerminalRoom);

            var registryPath = Path.Combine(tempHome, "room-registry.jsonl");
            await RoomRegistryStore.AppendAsync(succeededRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(failedRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(nonTerminalRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            // --state Succeeded restricts the matching set to exactly one of the two terminal rooms.
            var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: null, State: WorkflowOutcome.Succeeded, DryRun: false, Yes: true);
            var result = await RoomsPruneCommand.ExecuteAsync(
                options, TextWriter.Null, registryPath, TestContext.Current.CancellationToken);

            Assert.True(result.Executed);
            var deleted = Assert.Single(result.Deleted);
            Assert.Equal(BatonPaths.RecordKey(succeededRoom), deleted.RoomDirectoryPath);

            Assert.False(Directory.Exists(succeededRoom));
            Assert.True(Directory.Exists(failedRoom)); // wrong state -> not a candidate
            Assert.True(Directory.Exists(nonTerminalRoom)); // not terminal -> not a candidate

            var remaining = await RoomRegistryStore.ReadDistinctByRoomAsync(registryPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(remaining, e => string.Equals(e.RoomPath, BatonPaths.RecordKey(succeededRoom), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(remaining, e => string.Equals(e.RoomPath, BatonPaths.RecordKey(failedRoom), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(remaining, e => string.Equals(e.RoomPath, BatonPaths.RecordKey(nonTerminalRoom), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Yes_StateIndeterminate_SelectsIndeterminate_ExcludesFailed()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var indeterminateRoom = await CreateTerminalRoomAsync(tempHome, "indeterminate-room", WorkflowOutcome.Indeterminate, TestContext.Current.CancellationToken);
            var failedRoom = await CreateTerminalRoomAsync(tempHome, "failed-room", WorkflowOutcome.Failed, TestContext.Current.CancellationToken);

            var registryPath = Path.Combine(tempHome, "room-registry.jsonl");
            await RoomRegistryStore.AppendAsync(indeterminateRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(failedRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: null, State: WorkflowOutcome.Indeterminate, DryRun: false, Yes: true);
            var result = await RoomsPruneCommand.ExecuteAsync(
                options, TextWriter.Null, registryPath, TestContext.Current.CancellationToken);

            Assert.True(result.Executed);
            var deleted = Assert.Single(result.Deleted);
            Assert.Equal(BatonPaths.RecordKey(indeterminateRoom), deleted.RoomDirectoryPath);

            Assert.False(Directory.Exists(indeterminateRoom));
            Assert.True(Directory.Exists(failedRoom)); // wrong state -> not a candidate
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Yes_WithOlderThan_ExcludesRecentlyTerminalRooms()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var recentRoom = await CreateTerminalRoomAsync(tempHome, "recent-room", WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);
            var registryPath = Path.Combine(tempHome, "room-registry.jsonl");
            await RoomRegistryStore.AppendAsync(recentRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: 7, State: null, DryRun: false, Yes: true);
            var result = await RoomsPruneCommand.ExecuteAsync(
                options, TextWriter.Null, registryPath, TestContext.Current.CancellationToken);

            Assert.Empty(result.Candidates);
            Assert.True(Directory.Exists(recentRoom));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithoutTerminal_OnlyCompacts_SelectsNoCandidates()
    {
        var tempHome = CreateTempHome();
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = tempHome });
        try
        {
            var terminalRoom = await CreateTerminalRoomAsync(tempHome, "terminal-room", WorkflowOutcome.Succeeded, TestContext.Current.CancellationToken);
            var registryPath = Path.Combine(tempHome, "room-registry.jsonl");
            await RoomRegistryStore.AppendAsync(terminalRoom, tempHome, registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(terminalRoom, tempHome + "2", registryPath, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var options = new RoomsPruneOptions(Terminal: false, OlderThanDays: null, State: null, DryRun: false, Yes: true);
            var result = await RoomsPruneCommand.ExecuteAsync(
                options, TextWriter.Null, registryPath, TestContext.Current.CancellationToken);

            Assert.Empty(result.Candidates);
            Assert.Equal(1, result.DedupedRegistryLines);
            Assert.True(Directory.Exists(terminalRoom)); // --terminal omitted -> never a delete candidate
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempHome);
        }
    }
}
