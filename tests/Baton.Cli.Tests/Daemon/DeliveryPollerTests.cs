using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #734: <see cref="DeliveryPoller"/>'s room-level poll. Fixtures use the terminal-sentinel fast path
/// (<see cref="TerminalSentinelWriter.WriteAsync"/>) to declare a room's resolved outputs directly,
/// the same shortcut <c>FleetStatusToolTests.TerminalFastPath_...</c> already uses -- delivery polling
/// deliberately continues after a room's own workflow goes Terminal, so a terminal fixture is the
/// realistic shape, not a simplification.
/// </summary>
public sealed class DeliveryPollerTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _projectRoot;
    private readonly IDisposable _scope;

    public DeliveryPollerTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-delivery-poller-home-{Guid.NewGuid():N}");
        _projectRoot = Path.Combine(Path.GetTempPath(), $"baton-delivery-poller-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Directory.CreateDirectory(_projectRoot);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }

        if (Directory.Exists(_projectRoot))
        {
            DirectoryCleanup.DeleteRecursively(_projectRoot);
        }
    }

    private async Task<FleetStatusTool.DiscoveredRoom> CreateRoomAsync(int pullRequestNumber, string? branch = "734-lane")
    {
        var roomDir = Path.Combine(_tempHome, "rooms", $"room-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);

        var outputs = new List<string>();
        if (pullRequestNumber > 0)
        {
            var prPath = Path.Combine(roomDir, DeliveryReferenceOutputNames.PullRequest);
            File.WriteAllText(prPath, pullRequestNumber.ToString());
            outputs.Add(prPath);
        }

        if (branch is not null)
        {
            var branchPath = Path.Combine(roomDir, DeliveryReferenceOutputNames.Branch);
            File.WriteAllText(branchPath, branch);
            outputs.Add(branchPath);
        }

        var sentinel = new WorkflowStatusView("Succeeded", [], outputs, null, null);
        await TerminalSentinelWriter.WriteAsync(roomDir, sentinel, TestContext.Current.CancellationToken);

        return new FleetStatusTool.DiscoveredRoom(roomDir, _projectRoot);
    }

    private static async Task<IReadOnlyList<FlowEvent>> ReadEventsAsync(FleetStatusTool.DiscoveredRoom room)
    {
        var logPath = Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName);
        return await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
    }

    private const string OpenJson = """{"state":"OPEN","mergedAt":null,"statusCheckRollup":[]}""";

    private const string ChecksRedJson = """
        {"state":"OPEN","mergedAt":null,"statusCheckRollup":[
            {"status":"COMPLETED","conclusion":"SUCCESS"},
            {"status":"COMPLETED","conclusion":"FAILURE"}
        ]}
        """;

    private const string ChecksGreenJson = """
        {"state":"OPEN","mergedAt":null,"statusCheckRollup":[
            {"status":"COMPLETED","conclusion":"SUCCESS"},
            {"status":"COMPLETED","conclusion":"SUCCESS"}
        ]}
        """;

    private const string MergedJson = """
        {"state":"MERGED","mergedAt":"2026-09-03T00:00:00Z","statusCheckRollup":[
            {"status":"COMPLETED","conclusion":"SUCCESS"}
        ]}
        """;

    private const string ClosedUnmergedJson = """{"state":"CLOSED","mergedAt":null,"statusCheckRollup":[]}""";

    [Fact]
    public async Task Open_then_checks_red_then_checks_green_then_merged_lands_each_fact_once_and_stops_polling()
    {
        var room = await CreateRoomAsync(1799);
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);
        var sink = new StringWriter();

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, OpenJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterOpen = await ReadEventsAsync(room);
        Assert.Single(afterOpen);
        Assert.IsType<FlowEvent.DeliveryPrOpened>(afterOpen[0]);

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, ChecksRedJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterRed = await ReadEventsAsync(room);
        Assert.Equal(2, afterRed.Count);
        Assert.IsType<FlowEvent.DeliveryChecksRed>(afterRed[1]);

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, ChecksGreenJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterGreen = await ReadEventsAsync(room);
        Assert.Equal(3, afterGreen.Count);
        Assert.IsType<FlowEvent.DeliveryChecksGreen>(afterGreen[2]);

        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, MergedJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        var afterMerged = await ReadEventsAsync(room);
        Assert.Equal(4, afterMerged.Count);
        var merged = Assert.IsType<FlowEvent.DeliveryMerged>(afterMerged[3]);
        Assert.True(merged.Merged);

        // Each of the four facts landed exactly once.
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryPrOpened>());
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryChecksRed>());
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryChecksGreen>());
        Assert.Single(afterMerged.OfType<FlowEvent.DeliveryMerged>());

        // Polling stops: a further tick never calls gh again and never appends another event.
        var callsBefore = gh.CallCount;
        gh.NextResult = new GhCliResult(Started: true, ExitCode: 0, MergedJson, string.Empty);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        Assert.Equal(callsBefore, gh.CallCount);
        Assert.Equal(4, (await ReadEventsAsync(room)).Count);
    }

    [Fact]
    public async Task PR_closed_without_merging_is_recorded_once_as_DeliveryMerged_with_Merged_false()
    {
        var room = await CreateRoomAsync(55);
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: true, ExitCode: 0, ClosedUnmergedJson, string.Empty) };
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        var events = await ReadEventsAsync(room);
        var merged = Assert.Single(events.OfType<FlowEvent.DeliveryMerged>());
        Assert.False(merged.Merged);

        // Terminal: a further tick never calls gh again.
        var callsBefore = gh.CallCount;
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());
        Assert.Equal(callsBefore, gh.CallCount);
    }

    [Fact]
    public async Task Missing_gh_logs_once_and_records_no_events()
    {
        var room = await CreateRoomAsync(7);
        var gh = new FakeGhCliRunner { NextResult = new GhCliResult(Started: false, ExitCode: -1, string.Empty, "gh was not found on PATH.") };
        var poller = new DeliveryPoller(gh);
        var sink = new StringWriter();

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);
        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, sink);

        var lines = sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        var logPath = Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName);
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public async Task A_room_with_no_declared_delivery_output_is_never_polled()
    {
        var room = await CreateRoomAsync(pullRequestNumber: 0, branch: null);
        var gh = new FakeGhCliRunner();
        var poller = new DeliveryPoller(gh);

        await poller.PollRoomAsync(room, TestContext.Current.CancellationToken, new StringWriter());

        Assert.Equal(0, gh.CallCount);
        Assert.False(File.Exists(Path.Combine(room.RoomDir, BatonPaths.FlowLogFileName)));
    }

    private sealed class FakeGhCliRunner : IGhCliRunner
    {
        public GhCliResult NextResult { get; set; } = new(Started: true, ExitCode: 0, "{}", string.Empty);

        public int CallCount { get; private set; }

        public Task<GhCliResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(NextResult);
        }
    }
}
