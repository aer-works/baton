using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli.Tests;

public sealed class ConductorRoomExclusionTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public ConductorRoomExclusionTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-conductor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    [Fact]
    public async Task RoomsPrune_ExcludesConductorRoom_EvenIfTerminalSentinelExists()
    {
        var roomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var normalRoom = Path.Combine(roomsDir, "normal-room");
        var conductorRoom = Path.Combine(roomsDir, "conductor");

        Directory.CreateDirectory(normalRoom);
        Directory.CreateDirectory(conductorRoom);

        const string stubBindings = """
            {
              "conductor": {
                "Adapter": "none",
                "Contract": { "WorkerName": "conductor" },
                "PromptTemplate": "conductor",
                "Timeout": "01:00:00"
              }
            }
            """;
        await File.WriteAllTextAsync(BatonPaths.RoomBindingsFile(conductorRoom), stubBindings, TestContext.Current.CancellationToken);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(normalRoom, sentinel, TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(conductorRoom, sentinel, TestContext.Current.CancellationToken);

        await RoomRegistryStore.AppendAsync(normalRoom, _tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
        await RoomRegistryStore.AppendAsync(conductorRoom, _tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

        var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: null, State: null, DryRun: true, Yes: false);
        using var sw = new StringWriter();

        var result = await RoomsPruneCommand.ExecuteAsync(options, sw, BatonPaths.RoomRegistryFile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Candidates, c => c.RoomDirectoryPath.Equals(normalRoom, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Candidates, c => c.RoomDirectoryPath.Equals(conductorRoom, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RoomsPrune_WorkerRoomWithConductorSubstringInLabel_IsNotExempt()
    {
        // F4 (2026-09-02 review): role is resolved off the actual binding key, the same way
        // FleetStatusTool.TryResolveSoleBinding does -- never a raw substring search over
        // bindings.json's text, which would also exempt an ordinary worker room whose label happens
        // to contain the literal word "conductor".
        var roomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var workerRoom = Path.Combine(roomsDir, "worker-room");
        Directory.CreateDirectory(workerRoom);

        const string bindingsWithConductorLabel = """
            {
              "coder": {
                "Adapter": "none",
                "Contract": { "WorkerName": "coder" },
                "PromptTemplate": "coder",
                "Timeout": "01:00:00",
                "Label": "conductor-workstream"
              }
            }
            """;
        await File.WriteAllTextAsync(BatonPaths.RoomBindingsFile(workerRoom), bindingsWithConductorLabel, TestContext.Current.CancellationToken);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(workerRoom, sentinel, TestContext.Current.CancellationToken);
        await RoomRegistryStore.AppendAsync(workerRoom, _tempHome, BatonPaths.RoomRegistryFile, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

        var options = new RoomsPruneOptions(Terminal: true, OlderThanDays: null, State: null, DryRun: true, Yes: false);
        using var sw = new StringWriter();

        var result = await RoomsPruneCommand.ExecuteAsync(options, sw, BatonPaths.RoomRegistryFile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Candidates, c => c.RoomDirectoryPath.Equals(workerRoom, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FleetStatus_ConductorRoom_WithoutSnapshot_ReportsRoleConductorWithoutError()
    {
        var conductorRoom = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "conductor");
        Directory.CreateDirectory(conductorRoom);

        const string stubBindings = """
            {
              "conductor": {
                "Adapter": "none",
                "Contract": { "WorkerName": "conductor" },
                "PromptTemplate": "conductor",
                "Timeout": "01:00:00"
              }
            }
            """;
        await File.WriteAllTextAsync(BatonPaths.RoomBindingsFile(conductorRoom), stubBindings, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(JsonDocument.Parse("{}").RootElement, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<FleetStatusResponse>(result.Text)!.Rooms;
        Assert.NotNull(rooms);
        var conductor = Assert.Single(rooms!);
        Assert.Equal("conductor", conductor.Name);
        Assert.Equal("conductor", conductor.Role);
        Assert.Null(conductor.Error);
    }
}
