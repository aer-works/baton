using System.Text;
using System.Text.Json;
using Baton.Vendors;
using Baton.Flow.Artifacts;
using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Status;
using Baton.Flow.Store;
using Baton.Mcp.Host;

namespace Baton.Mcp.Tests;

/// <summary>
/// Unit and integration coverage for <see cref="RoomDetailTool"/> (#1427): the level-two
/// drill-down beyond <c>fleet_status</c> — a single room's stdout tail and flow.jsonl timeline.
/// Fixture rooms are real files under a temp <c>BATON_HOME</c>, no mocks of the subject.
/// </summary>
[Collection(BatonHomeEnvCollection.Name)]
public sealed class RoomDetailToolTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _originalBatonHome;

    public RoomDetailToolTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-room-detail-test-home-{Guid.NewGuid():N}");
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
    public async Task TerminalRoom_ReturnsStdoutTailAndTimeline()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "terminal-room");
        Directory.CreateDirectory(roomDir);

        var execId = new ExecutionId("exec-term-1");
        WriteStdout(roomDir, execId, "line one\nline two\nline three\n");

        await AppendFlowEventsAsync(roomDir, execId);

        var sentinel = new WorkflowStatusView("Succeeded", [], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(roomDir, sentinel, TestContext.Current.CancellationToken);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "terminal-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view);
        Assert.Equal("terminal-room", view!.Name);
        Assert.Null(view.Error);

        Assert.NotNull(view.Stdout);
        Assert.Contains("line one", view.Stdout!.Text);
        Assert.False(view.Stdout.Truncated);
        Assert.Equal($"execution_{execId.Value}", view.Stdout.Source);

        Assert.NotNull(view.Timeline);
        Assert.False(view.Timeline!.Truncated);
        Assert.Equal(2, view.Timeline.TotalEntries);
        Assert.Equal(2, view.Timeline.Entries.Count);
        Assert.Equal("flow.executionRequestAccepted", view.Timeline.Entries[0].Type);
        Assert.Equal("core.executionStarted", view.Timeline.Entries[1].Type);
        Assert.NotNull(view.Timeline.Entries[1].Timestamp);
    }

    [Fact]
    public async Task MidFlightRoom_HasStdoutAndTimelineButNoTerminalSentinel()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "mid-flight-room");
        Directory.CreateDirectory(roomDir);

        var execId = new ExecutionId("exec-mid-1");
        WriteStdout(roomDir, execId, "still running...\n");
        await AppendFlowEventsAsync(roomDir, execId);

        Assert.False(File.Exists(Path.Combine(roomDir, TerminalSentinelWriter.TerminalSentinelFileName)));

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "mid-flight-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view);
        Assert.Null(view!.Error);
        Assert.NotNull(view.Stdout);
        Assert.Contains("still running", view.Stdout!.Text);
        Assert.NotNull(view.Timeline);
        Assert.Equal(2, view.Timeline!.Entries.Count);
        Assert.Null(view.Note);
    }

    [Fact]
    public async Task MissingRoom_DegradesGracefullyWithoutThrowing()
    {
        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "no-such-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view);
        Assert.Equal("no-such-room", view!.Name);
        Assert.NotNull(view.Error);
        Assert.Null(view.Stdout);
        Assert.Null(view.Timeline);
    }

    [Fact]
    public async Task RoomWithNoArtifactsOrLedger_ReturnsNoteInsteadOfNulls()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "bare-room");
        Directory.CreateDirectory(roomDir);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "bare-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view);
        Assert.Null(view!.Error);
        Assert.Null(view.Stdout);
        Assert.Null(view.Timeline);
        Assert.NotNull(view.Note);
    }

    [Fact]
    public async Task StdoutLongerThanCap_IsTailedAndMarkedTruncated()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "chatty-room");
        Directory.CreateDirectory(roomDir);

        var execId = new ExecutionId("exec-chatty-1");
        const string paddingLine = "this is a line of stdout output padding\n";
        var oversized = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            oversized.Append(paddingLine);
        }

        oversized.Append("FINAL MARKER LINE\n");
        var content = oversized.ToString();
        WriteStdout(roomDir, execId, content);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "chatty-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view?.Stdout);
        Assert.True(view!.Stdout!.Truncated);
        Assert.True(view.Stdout.Text.Length < content.Length);
        Assert.EndsWith("FINAL MARKER LINE\n", view.Stdout.Text);
        // The dropped leading line must be a clean cut at a padding-line boundary, never a fragment.
        Assert.StartsWith(paddingLine, view.Stdout.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), view.Stdout.TotalBytes);
    }

    /// <summary>
    /// Pins the exact 64 KiB boundary (review of #1427 flagged the cap test above as only
    /// exercising ~13x the cap): a file of exactly DefaultStdoutTailBytes must come back whole and
    /// unmarked, and one byte more must trip the tail path — an off-by-one in the
    /// <c>totalLength - DefaultStdoutTailBytes</c> math would flip one of the two.
    /// </summary>
    [Fact]
    public async Task StdoutAtExactlyTheCap_IsNotTruncated_AndOneByteOverIs()
    {
        var tool = new RoomDetailTool();

        var atCapDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "at-cap-room");
        Directory.CreateDirectory(atCapDir);
        var atCap = new string('x', RoomDetailTool.DefaultStdoutTailBytes - 1) + "\n";
        WriteStdout(atCapDir, new ExecutionId("exec-at-cap"), atCap);

        var atCapResult = await tool.CallAsync(Parse("""{ "room": "at-cap-room" }"""), TestContext.Current.CancellationToken);
        var atCapView = JsonSerializer.Deserialize<RoomDetailView>(atCapResult.Text);
        Assert.NotNull(atCapView?.Stdout);
        Assert.False(atCapView!.Stdout!.Truncated);
        Assert.Equal(RoomDetailTool.DefaultStdoutTailBytes, atCapView.Stdout.TotalBytes);

        var overCapDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "over-cap-room");
        Directory.CreateDirectory(overCapDir);
        WriteStdout(overCapDir, new ExecutionId("exec-over-cap"), "y\n" + atCap);

        var overCapResult = await tool.CallAsync(Parse("""{ "room": "over-cap-room" }"""), TestContext.Current.CancellationToken);
        var overCapView = JsonSerializer.Deserialize<RoomDetailView>(overCapResult.Text);
        Assert.NotNull(overCapView?.Stdout);
        Assert.True(overCapView!.Stdout!.Truncated);
        Assert.Equal(RoomDetailTool.DefaultStdoutTailBytes + 2, overCapView.Stdout.TotalBytes);
    }

    [Fact]
    public async Task StdoutFallsBackToPrunedExecutionDirectory()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "pruned-room");
        Directory.CreateDirectory(roomDir);

        var execId = new ExecutionId("exec-pruned-1");
        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(artifactsRoot, execId);
        Directory.CreateDirectory(prunedDir);
        File.WriteAllText(
            Path.Combine(prunedDir, ExecutionStreamLogger.StdoutLogFileName), "swept but readable\n", Utf8NoBom);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "pruned-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view?.Stdout);
        Assert.Contains("swept but readable", view!.Stdout!.Text);
        Assert.Contains("(pruned)", view.Stdout.Source);
    }

    [Fact]
    public async Task PinnedExecution_OverridesTheMostRecentlyWrittenHeuristic()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "retried-room");
        Directory.CreateDirectory(roomDir);

        var firstAttempt = new ExecutionId("exec-attempt-1");
        WriteStdout(roomDir, firstAttempt, "first attempt failed here\n");

        // Written after the first attempt, so the "newest write wins" heuristic would pick this one.
        var retryAttempt = new ExecutionId("exec-attempt-2");
        WriteStdout(roomDir, retryAttempt, "retry succeeded\n");

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(
            Parse($$"""{ "room": "retried-room", "execution": "{{firstAttempt.Value}}" }"""),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view?.Stdout);
        Assert.Contains("first attempt failed here", view!.Stdout!.Text);
        Assert.Equal($"execution_{firstAttempt.Value}", view.Stdout.Source);
    }

    [Fact]
    public async Task TimelineLongerThanCap_IsTailedAndMarkedTruncated()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "verbose-ledger-room");
        Directory.CreateDirectory(roomDir);

        var logPath = Path.Combine(roomDir, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var entryCount = RoomDetailTool.DefaultTimelineTailEntries + 10;
        for (var i = 0; i < entryCount; i++)
        {
            await writer.AppendAsync(
                new CoreEvent.ExecutionStarted(new ExecutionId($"exec-{i}"), Pid: (uint)i),
                TestContext.Current.CancellationToken);
        }

        await writer.DisposeAsync();

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "verbose-ledger-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view?.Timeline);
        Assert.True(view!.Timeline!.Truncated);
        Assert.Equal(entryCount, view.Timeline.TotalEntries);
        Assert.Equal(RoomDetailTool.DefaultTimelineTailEntries, view.Timeline.Entries.Count);
    }

    [Fact]
    public async Task HeldLedger_DegradesToAnUnreadableTimelineEntryInsteadOfThrowing()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "held-ledger-room");
        Directory.CreateDirectory(roomDir);

        var logPath = Path.Combine(roomDir, "flow.jsonl");
        await File.WriteAllTextAsync(logPath, string.Empty, TestContext.Current.CancellationToken);

        await using var exclusiveHold = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "held-ledger-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view?.Timeline);
        Assert.Equal(1, view!.Timeline!.TotalEntries);
        Assert.Equal("unreadable", view.Timeline.Entries[0].Type);
        Assert.NotNull(view.Timeline.Entries[0].Detail);
    }

    [Fact]
    public async Task ExtraRoots_ResolveRoomByName()
    {
        var extraRoot = Path.Combine(Path.GetTempPath(), $"baton-room-detail-test-extra-{Guid.NewGuid():N}");
        var roomDir = Path.Combine(extraRoot, "extra-room");

        try
        {
            Directory.CreateDirectory(roomDir);
            var execId = new ExecutionId("exec-extra-1");
            WriteStdout(roomDir, execId, "from an extra root\n");

            var tool = new RoomDetailTool();
            var escapedExtraRoot = extraRoot.Replace("\\", "\\\\");
            var result = await tool.CallAsync(
                Parse($$"""{ "room": "extra-room", "roots": ["{{escapedExtraRoot}}"] }"""),
                TestContext.Current.CancellationToken);

            Assert.False(result.IsError);
            var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
            Assert.NotNull(view);
            Assert.Null(view!.Error);
            Assert.NotNull(view.Stdout);
            Assert.Contains("from an extra root", view.Stdout!.Text);
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
    public async Task MissingRoomArgument_ReturnsError()
    {
        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static void WriteStdout(string roomDir, ExecutionId execId, string content)
    {
        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var executionDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
        File.WriteAllText(Path.Combine(executionDir, ExecutionStreamLogger.StdoutLogFileName), content, Utf8NoBom);
    }

    private static async Task AppendFlowEventsAsync(string roomDir, ExecutionId execId)
    {
        var logPath = Path.Combine(roomDir, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);

        var req = new ExecutionRequest(
            execId,
            new WorkflowId("wf-1"),
            new StepId("step-1"),
            "agent-worker",
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 1234), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
