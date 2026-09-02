using System.Text.Json;
using Baton.Artifacts;
using Baton.Cli.Mcp;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1574: <c>room_detail</c> (MCP) and <c>baton status --follow</c> must render a claude/agy
/// stream-json worker log as prose, not the raw <c>"type":"assistant"</c> envelope. The fixture is a
/// real, unmodified <c>.stdout.log</c> tail from a finished claude lane on this machine
/// (<c>Fixtures/claude-stream-json-sample.log</c>) -- it carries a genuine assistant text turn plus
/// system/tool_use/tool_result/thinking/rate_limit_event lines, so both surfaces are proven against
/// the same shape a real dispatch produces, not a hand-built fixture.
/// </summary>
public sealed class WorkerStreamJsonRenderingTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-stream-json-sample.log");

    private const string ExpectedProse =
        "I'll start by reading the issue and inspecting the current state of the workspace.";

    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public WorkerStreamJsonRenderingTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-stream-json-render-test-{Guid.NewGuid():N}");
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

    /// <summary>Sanity check on the fixture itself, independent of either surface under test.</summary>
    [Fact]
    public void Fixture_IsRealClaudeStreamJsonCarryingAnAssistantEnvelope()
    {
        var raw = File.ReadAllText(FixturePath);
        Assert.Contains("\"type\":\"assistant\"", raw);
        Assert.Contains(ExpectedProse, raw);
    }

    [Fact]
    public async Task RoomDetail_RendersProse_NotTheRawAssistantEnvelope()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "stream-json-room");
        Directory.CreateDirectory(roomDir);

        var execId = new ExecutionId("exec-stream-json-1");
        await WriteBindingsAndStdoutAsync(roomDir, execId);
        await WriteFlowLedgerAsync(roomDir, execId);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "stream-json-room" }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.NotNull(view?.Stdout);
        var text = view!.Stdout!.Text;

        Assert.Contains(ExpectedProse, text);
        Assert.DoesNotContain("\"type\":\"assistant\"", text);
    }

    [Fact]
    public void StatusFollow_TailStreams_RendersProse_NotTheRawAssistantEnvelope()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"baton-status-stream-json-{Guid.NewGuid():N}");
        var execDir = Path.Combine(testRoot, "execution_exec-stream-json-2");
        Directory.CreateDirectory(execDir);
        try
        {
            File.Copy(FixturePath, Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName));

            var output = new StringWriter();
            var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
            var assemblers = new Dictionary<string, StreamLineAssembler>(StringComparer.Ordinal);
            var claudeAdapter = new ClaudeWorkerAdapter();

            StatusCommand.TailStreams(output, testRoot, offsets, assemblers, _ => claudeAdapter);

            var statusText = output.ToString();
            Assert.Contains(ExpectedProse, statusText);
            Assert.DoesNotContain("\"type\":\"assistant\"", statusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// #1574 second-reader finding: a one-shot reader at EOF (room_detail) must not silently drop a
    /// worker's final, newline-less write (a crashed process, or simply still mid-write) -- unlike a
    /// polling reader, there is no future call left to complete the line.
    /// </summary>
    [Fact]
    public async Task RoomDetail_UnterminatedFinalLine_IsStillIncluded()
    {
        var roomDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "crashed-mid-line-room");
        Directory.CreateDirectory(roomDir);

        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var executionDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, new ExecutionId("exec-crashed"));
        await File.WriteAllTextAsync(
            Path.Combine(executionDir, ExecutionStreamLogger.StdoutLogFileName),
            "first line\nunterminated crash tail with no newline",
            TestContext.Current.CancellationToken);

        var tool = new RoomDetailTool();
        var result = await tool.CallAsync(Parse("""{ "room": "crashed-mid-line-room" }"""), TestContext.Current.CancellationToken);

        var view = JsonSerializer.Deserialize<RoomDetailView>(result.Text);
        Assert.Contains("unterminated crash tail with no newline", view!.Stdout!.Text);
    }

    /// <summary>
    /// The polling counterpart of the finding above: <c>baton status --follow</c> holds a partial
    /// trailing line across polls (correct -- more bytes may still be coming), but once the workflow
    /// reaches Terminal there is no next poll, so that call must flush it. The non-flushing call is
    /// the negative control: without <c>flushPending: true</c>, the same content stays held.
    /// </summary>
    [Fact]
    public void StatusFollow_TailStreams_FlushesPendingOnlyWhenToldTo()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"baton-status-flush-{Guid.NewGuid():N}");
        var execDir = Path.Combine(testRoot, "execution_exec-flush");
        Directory.CreateDirectory(execDir);
        try
        {
            File.WriteAllText(
                Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName),
                "first line\nunterminated tail");

            var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
            var assemblers = new Dictionary<string, StreamLineAssembler>(StringComparer.Ordinal);

            var withoutFlush = new StringWriter();
            StatusCommand.TailStreams(withoutFlush, testRoot, offsets, assemblers, _ => null, flushPending: false);
            Assert.DoesNotContain("unterminated tail", withoutFlush.ToString());

            var withFlush = new StringWriter();
            StatusCommand.TailStreams(withFlush, testRoot, offsets, assemblers, _ => null, flushPending: true);
            Assert.Contains("unterminated tail", withFlush.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task WriteBindingsAndStdoutAsync(string roomDir, ExecutionId execId)
    {
        var artifactsRoot = Path.Combine(roomDir, ArtifactManager.ArtifactsDirectoryName);
        var executionDir = ArtifactManager.AllocateOutputDirectory(artifactsRoot, execId);
        var fixtureBytes = await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(executionDir, ExecutionStreamLogger.StdoutLogFileName), fixtureBytes, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("worker", [], [], []),
                "prompt",
                TimeSpan.FromMinutes(1),
                StreamJson: true),
        };
        await File.WriteAllTextAsync(
            Path.Combine(roomDir, "bindings.json"), JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);
    }

    private static async Task WriteFlowLedgerAsync(string roomDir, ExecutionId execId)
    {
        var logPath = Path.Combine(roomDir, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);

        var req = new ExecutionRequest(
            execId,
            new WorkflowId("wf-1"),
            new StepId("worker"),
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(1),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 4242), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
