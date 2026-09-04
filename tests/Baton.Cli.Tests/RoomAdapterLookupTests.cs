using Baton.Domain;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit coverage for <see cref="RoomAdapterLookup"/> (#1574) at the level both <c>room_detail</c>
/// and <c>baton status --follow</c> actually call it: bindings.json plus flow.jsonl's own
/// <see cref="FlowEvent.ExecutionRequestAccepted"/>/<see cref="FlowEvent.StepRebound"/> events, not a
/// hardcoded adapter -- closing the gap a second-reader review flagged where
/// <c>StatusCommand</c>'s own resolution wiring (as opposed to <c>room_detail</c>'s, which the
/// end-to-end test in <c>WorkerStreamJsonRenderingTests</c> exercises through the real room files) had
/// no direct coverage.
/// </summary>
public sealed class RoomAdapterLookupTests
{
    [Fact]
    public void ResolveAdapter_FromBindingsJson_WhenNoRecordedAdapterOverridesIt()
    {
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "claude", new WorkerContract("worker", [], [], []), "prompt", TimeSpan.FromMinutes(1)),
        };

        var req = new ExecutionRequest(
            new ExecutionId("exec-1"), new WorkflowId("wf-1"), new StepId("step"), "worker",
            Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(1), Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());
        var events = new List<FlowEvent> { new FlowEvent.ExecutionRequestAccepted(req) };

        var adapterNames = RoomAdapterLookup.BuildAdapterNameByExecutionId(events, bindings);
        var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() };

        var resolved = RoomAdapterLookup.ResolveAdapter("exec-1", adapterNames, adapters);

        Assert.IsType<ClaudeWorkerAdapter>(resolved);
    }

    [Fact]
    public void ResolveAdapter_RecordedAdapterOnTheRequest_WinsOverBindingsJson()
    {
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "agy", new WorkerContract("worker", [], [], []), "prompt", TimeSpan.FromMinutes(1)),
        };

        var req = new ExecutionRequest(
            new ExecutionId("exec-1"), new WorkflowId("wf-1"), new StepId("step"), "worker",
            Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(1), Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "claude");
        var events = new List<FlowEvent> { new FlowEvent.ExecutionRequestAccepted(req) };

        var adapterNames = RoomAdapterLookup.BuildAdapterNameByExecutionId(events, bindings);
        var adapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new ClaudeWorkerAdapter(),
            ["agy"] = new AgyWorkerAdapter(),
        };

        var resolved = RoomAdapterLookup.ResolveAdapter("exec-1", adapterNames, adapters);

        Assert.IsType<ClaudeWorkerAdapter>(resolved);
    }

    [Fact]
    public void ResolveAdapter_StepRebound_OverridesTheOriginallyRecordedAdapter()
    {
        var bindings = new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal);

        var req = new ExecutionRequest(
            new ExecutionId("exec-1"), new WorkflowId("wf-1"), new StepId("step"), "worker",
            Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(1), Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "agy");
        var events = new List<FlowEvent>
        {
            new FlowEvent.ExecutionRequestAccepted(req),
            new FlowEvent.StepRebound(new StepId("step"), new ExecutionId("exec-1"), PreviousAdapter: "agy", NewAdapter: "claude"),
        };

        var adapterNames = RoomAdapterLookup.BuildAdapterNameByExecutionId(events, bindings);
        var adapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new ClaudeWorkerAdapter(),
            ["agy"] = new AgyWorkerAdapter(),
        };

        var resolved = RoomAdapterLookup.ResolveAdapter("exec-1", adapterNames, adapters);

        Assert.IsType<ClaudeWorkerAdapter>(resolved);
    }

    [Fact]
    public void ResolveAdapter_UnknownExecution_ReturnsNull()
    {
        var adapterNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var adapters = new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() };

        Assert.Null(RoomAdapterLookup.ResolveAdapter("no-such-execution", adapterNames, adapters));
    }

    [Fact]
    public async Task TryLoadBindingsAsync_MissingFile_FailsOpenToEmpty()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"baton-adapter-lookup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);
        try
        {
            var bindings = await RoomAdapterLookup.TryLoadBindingsAsync(roomDir, TestContext.Current.CancellationToken);
            Assert.Empty(bindings);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task TryLoadBindingsAsync_MalformedFile_FailsOpenToEmpty()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"baton-adapter-lookup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(roomDir, "bindings.json"), "not json", TestContext.Current.CancellationToken);

            var bindings = await RoomAdapterLookup.TryLoadBindingsAsync(roomDir, TestContext.Current.CancellationToken);

            Assert.Empty(bindings);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
}
