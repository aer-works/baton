using System.Text.Json;
using Baton.Vendors;
using Baton.Flow.Domain;
using Baton.Flow.Status;
using Baton.Flow.Store;
using Baton.Flow.Templates;
using Baton.Mcp.Host;

namespace Baton.Mcp.Tests;

/// <summary>
/// Unit and integration coverage for <see cref="FleetStatusTool"/> (#1392 Spike 1).
/// Validates root enumeration, terminal sentinel fast path, active room projection,
/// filtering, and graceful error handling on malformed rooms.
/// </summary>
[Collection(BatonHomeEnvCollection.Name)]
public sealed class FleetStatusToolTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _originalBatonHome;

    public FleetStatusToolTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-fleet-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _originalBatonHome = Environment.GetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable, _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable, _originalBatonHome);
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    [Fact]
    public async Task Enumeration_IncludesExtraRoots_AndDiscoversRoomsAcrossRoots()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room1 = Path.Combine(defaultRoomsDir, "room-default");
        var extraRoot = Path.Combine(Path.GetTempPath(), $"baton-fleet-test-extra-{Guid.NewGuid():N}");
        var room2 = Path.Combine(extraRoot, "room-extra");

        try
        {
            Directory.CreateDirectory(room1);
            Directory.CreateDirectory(room2);

            var sentinel1 = new WorkflowStatusView("Succeeded", [], [], null, null);
            var sentinel2 = new WorkflowStatusView("Failed", [], [], "Test failure", null);

            await TerminalSentinelWriter.WriteAsync(room1, sentinel1, TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(room2, sentinel2, TestContext.Current.CancellationToken);

            var tool = new FleetStatusTool();
            var escapedExtraRoot = extraRoot.Replace("\\", "\\\\");
            var result = await tool.CallAsync(Parse($$"""{ "roots": ["{{escapedExtraRoot}}"] }"""), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
            Assert.NotNull(rooms);
            Assert.Equal(2, rooms!.Count);

            var names = rooms.Select(r => r.Name).OrderBy(n => n).ToList();
            Assert.Equal(["room-default", "room-extra"], names);
        }
        finally
        {
            if (Directory.Exists(extraRoot))
            {
                DirectoryCleanup.DeleteRecursively(extraRoot);
            }
        }
    }

    [Fact]
    public async Task TerminalFastPath_UsesSentinelWithoutReadingSnapshotOrLedger()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "terminal-room");
        Directory.CreateDirectory(room);

        var step = new WorkflowStatusStepView("step-a", "Succeeded", "exec-1", null, null, null);
        var sentinel = new WorkflowStatusView("Succeeded", [step], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("terminal-room", singleRoom.Name);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.NotNull(singleRoom.Steps);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("step-a", singleStep.Id);
        Assert.Equal("Succeeded", singleStep.State);
        Assert.Equal("exec-1", singleStep.Execution);
        Assert.Null(singleStep.Timestamp);
        Assert.Equal(["/tmp/out.txt"], singleRoom.Outputs);
        Assert.Null(singleRoom.Error);
    }

    [Fact]
    public async Task ActiveRoom_ProjectsFromSnapshotAndEvents()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "active-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-active"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(
            new WorkflowTemplateId("active-wf"),
            1,
            [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-active-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("active-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 4242), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("active-room", singleRoom.Name);
        Assert.Equal("Running", singleRoom.State);
        Assert.NotNull(singleRoom.Steps);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("step-active", singleStep.Id);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("exec-active-1", singleStep.Execution);
        Assert.NotNull(singleStep.Timestamp);
    }

    [Fact]
    public async Task IncludeTerminalFalse_FiltersOutTerminalRooms()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var roomTerminal = Path.Combine(defaultRoomsDir, "room-term");
        var roomActive = Path.Combine(defaultRoomsDir, "room-act");
        Directory.CreateDirectory(roomTerminal);
        Directory.CreateDirectory(roomActive);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(roomTerminal, sentinel, TestContext.Current.CancellationToken);

        var stepDef = new WorkflowStepDefinition(new StepId("step-active"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(
            new WorkflowTemplateId("active-wf"),
            1,
            [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(roomActive, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomActive, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-active-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("active-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 5555), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("""{ "include_terminal": false }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("room-act", singleRoom.Name);
    }

    [Fact]
    public async Task MalformedRoom_ReturnsErrorEntryWithoutFailingWholeResponse()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var healthyRoom = Path.Combine(defaultRoomsDir, "healthy-room");
        var brokenRoom = Path.Combine(defaultRoomsDir, "broken-room");
        Directory.CreateDirectory(healthyRoom);
        Directory.CreateDirectory(brokenRoom);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(healthyRoom, sentinel, TestContext.Current.CancellationToken);

        // Broken room has corrupt snapshot
        await File.WriteAllTextAsync(Path.Combine(brokenRoom, "snapshot.json"), "{ invalid json", TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        Assert.Equal(2, rooms!.Count);

        var healthy = rooms.First(r => r.Name == "healthy-room");
        Assert.Equal("Succeeded", healthy.State);
        Assert.Null(healthy.Error);

        var broken = rooms.First(r => r.Name == "broken-room");
        Assert.NotNull(broken.Error);
        Assert.Null(broken.State);
    }

    [Fact]
    public async Task Call_SynchronousOverload_ReturnsSameShape()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "sync-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = tool.Call(Parse("{}"));

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("sync-room", singleRoom.Name);
    }

    /// <summary>
    /// spec/baton.md §8's named invariant, as a regression test rather than a design note: a room
    /// registered under a project root the caller never passes as a <c>roots</c> entry is still found.
    /// The room directory here sits outside both <see cref="BatonPaths.Rooms"/> and any scanned root —
    /// only the registry names it — so this fails the moment the union degrades back to a bare
    /// directory scan.
    /// </summary>
    [Fact]
    public async Task RegistryEntry_OutsideEveryScannedRoot_IsStillFoundByFleetStatus()
    {
        var unlistedProjectDir = Path.Combine(Path.GetTempPath(), $"baton-fleet-unlisted-project-{Guid.NewGuid():N}");
        var room = Path.Combine(unlistedProjectDir, ".baton", "rooms", "registry-only-room");

        try
        {
            Directory.CreateDirectory(room);
            var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
            await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

            await RoomRegistryStore.AppendAsync(
                room, unlistedProjectDir, BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);

            var tool = new FleetStatusTool();
            // Deliberately no "roots" entry for unlistedProjectDir -- the whole point of the test.
            var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
            Assert.NotNull(rooms);
            var found = Assert.Single(rooms!, r => r.Name == "registry-only-room");
            Assert.Equal("Succeeded", found.State);
            Assert.Equal(BatonPaths.RecordKey(unlistedProjectDir), found.Project);
        }
        finally
        {
            if (Directory.Exists(unlistedProjectDir))
            {
                DirectoryCleanup.DeleteRecursively(unlistedProjectDir);
            }
        }
    }

    [Fact]
    public async Task RegistryEntry_WhoseRoomDirectoryWasDeleted_IsSkippedRatherThanErroring()
    {
        var deletedRoomProjectDir = Path.Combine(Path.GetTempPath(), $"baton-fleet-deleted-project-{Guid.NewGuid():N}");
        var deletedRoom = Path.Combine(deletedRoomProjectDir, "rooms", "gone-room");
        try
        {
            Directory.CreateDirectory(deletedRoom);
            await RoomRegistryStore.AppendAsync(
                deletedRoom, deletedRoomProjectDir, BatonPaths.RoomRegistryFile, TestContext.Current.CancellationToken);
            DirectoryCleanup.DeleteRecursively(deletedRoom);

            var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
            var healthyRoom = Path.Combine(defaultRoomsDir, "healthy-registry-room");
            Directory.CreateDirectory(healthyRoom);
            var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
            await TerminalSentinelWriter.WriteAsync(healthyRoom, sentinel, TestContext.Current.CancellationToken);

            var tool = new FleetStatusTool();
            var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
            Assert.NotNull(rooms);
            Assert.DoesNotContain(rooms!, r => r.Name == "gone-room");
            Assert.Contains(rooms!, r => r.Name == "healthy-registry-room");
        }
        finally
        {
            if (Directory.Exists(deletedRoomProjectDir))
            {
                DirectoryCleanup.DeleteRecursively(deletedRoomProjectDir);
            }
        }
    }

    [Fact]
    public async Task MalformedRegistry_IsToleratedAndFallsBackToTheDirectoryScan()
    {
        await File.WriteAllTextAsync(
            BatonPaths.RoomRegistryFile, "{ not valid json\n", TestContext.Current.CancellationToken);

        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "scanned-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("scanned-room", singleRoom.Name);
        Assert.Null(singleRoom.Project);
    }

    /// <summary>
    /// A real I/O failure, not just malformed content (#1447 review finding): the registry path
    /// occupied by a DIRECTORY makes every open attempt throw. The only-ever-adds-coverage
    /// contract means the scan's rooms must still come back with no error — losing the whole call
    /// to a registry read failure would be strictly worse than answering scan-only.
    /// </summary>
    [Fact]
    public async Task RegistryPathOccupiedByADirectory_StillAnswersFromTheScanAlone()
    {
        Directory.CreateDirectory(BatonPaths.RoomRegistryFile);

        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "scanned-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("scanned-room", singleRoom.Name);
        Assert.Null(singleRoom.Project);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
