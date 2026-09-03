using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Cli.Daemon;
using Baton.Cli.Mcp;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// #1557 PR-A: <see cref="FleetProjectionWriter"/> writes <see cref="BatonPaths.FleetProjectionFile"/>.
/// Mirrors <c>FleetStatusToolTests</c>' own per-test isolated <c>BATON_HOME</c> pattern.
/// </summary>
public sealed class FleetProjectionWriterTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public FleetProjectionWriterTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-fleet-projection-test-home-{Guid.NewGuid():N}");
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
    public void WriteAtomic_never_lets_a_concurrent_reader_see_a_torn_file()
    {
        var path = Path.Combine(_tempHome, "projection.json");
        var contentA = new string('a', 50_000);
        var contentB = new string('b', 80_000);
        FleetProjectionWriter.WriteAtomic(path, contentA);

        Exception? readerException = null;
        var stop = false;

        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    // FileShare.Delete: a well-behaved poller of a file it knows gets rewritten out from
                    // under it -- File.ReadAllText's own default share (Read only) would make the
                    // writer's rename fail with a sharing violation on Windows whenever this loop happens
                    // to hold the file open, which is a liveness question this single-writer, no-retry
                    // design (the next ~30s tick self-heals) accepts. What this test asserts is narrower
                    // and must hold regardless: a read that DOES land never observes torn or mixed content.
                    using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var streamReader = new StreamReader(stream);
                    var text = streamReader.ReadToEnd();
                    if (text.Length != contentA.Length && text.Length != contentB.Length)
                    {
                        throw new InvalidOperationException($"torn read of length {text.Length}");
                    }

                    if (text.Length > 0 && text[0] != text[^1])
                    {
                        throw new InvalidOperationException("read mixed content from two writes");
                    }
                }
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        });
        reader.Start();

        for (var i = 0; i < 200; i++)
        {
            // Production WriteAtomic carries no retry (single writer, ~30s cadence -- a skipped tick
            // self-heals on the next one). This tight back-to-back loop is a synthetic stress condition
            // no production cadence produces; the retry here is test-only, standing in for "the next
            // tick", so the property under test (a landed read is never torn) still gets exercised
            // hundreds of times against a genuinely concurrent reader rather than the test itself
            // flaking on an AV/indexer-timed sharing violation unrelated to the atomicity claim.
            WriteAtomicRetryingTransientSharingViolations(path, i % 2 == 0 ? contentB : contentA);
        }

        Volatile.Write(ref stop, true);
        reader.Join();

        Assert.Null(readerException);
    }

    private static void WriteAtomicRetryingTransientSharingViolations(string path, string content)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (true)
        {
            try
            {
                FleetProjectionWriter.WriteAtomic(path, content);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw;
                }

                Thread.Sleep(5);
            }
        }
    }

    [Fact]
    public async Task BuildProjectionJson_deserializes_into_FleetRoomStatusView_and_carries_derived_at()
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "terminal-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var writer = new FleetProjectionWriter();
        var json = await writer.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(root.ContainsKey("derived_at"));
        var roomsNode = root["rooms"]!.AsArray();
        var singleRoomNode = Assert.Single(roomsNode);

        var roomView = singleRoomNode.Deserialize<FleetRoomStatusView>(FleetStatusTool.SerializerOptions);
        Assert.NotNull(roomView);
        Assert.Equal("terminal-room", roomView!.Name);
        Assert.Equal("Succeeded", roomView.State);

        // A terminal room carries no Running execution, so none of the Running-only fields are present.
        var roomObject = singleRoomNode!.AsObject();
        Assert.False(roomObject.ContainsKey("live"));
        Assert.False(roomObject.ContainsKey("processAlive"));
    }

    /// <summary>
    /// #1513: a Running step whose engine is confirmed dead downgrades the room's DISPLAYED state to
    /// "Stalled" (FleetStatusTool's own override) even though the step itself is still "Running" --
    /// spec/baton.md §6's `live` stays gated on the displayed state (matching pusher.py's existing
    /// contract), so a Stalled room carries no `live`. `processAlive` is deliberately NOT behind that
    /// gate: it is the diagnostic that explains why the room reads Stalled at all -- exactly the janitor
    /// sweep's own ask (#1557's issue body) for a room a live `fleet_status` scan would otherwise need a
    /// second read to explain.
    /// </summary>
    [Fact]
    public async Task RunningRoom_WithDeadEngine_ReportsProcessAliveDeadButNoLiveSection()
    {
        var (room, execId) = await CreateRunningRoomAsync("dead-engine-room", DeadProcessIdentity());

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();

        Assert.Equal("Stalled", roomNode["state"]!.GetValue<string>());
        Assert.Equal("dead", roomNode["processAlive"]!.GetValue<string>());
        Assert.True(roomNode.ContainsKey("stdout_last_write_ago_sec"));
        Assert.False(roomNode.ContainsKey("live"));

        _ = execId;
        _ = room;
    }

    /// <summary>Polarity arm: a genuinely alive engine keeps the room "Running" and DOES carry `live`,
    /// accumulated from the captured stdout via a daemon-side <c>TokenBudgetMonitor</c>.</summary>
    [Fact]
    public async Task RunningRoom_WithAliveEngine_ReportsLiveUsageFromCapturedStdout()
    {
        var liveIdentity = (Environment.ProcessId, new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime());
        var (_, _) = await CreateRunningRoomAsync("alive-engine-room", liveIdentity);

        var projectionWriter = new FleetProjectionWriter();
        var json = await projectionWriter.BuildProjectionJsonAsync(TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        var roomNode = Assert.Single(root["rooms"]!.AsArray())!.AsObject();

        Assert.Equal("Running", roomNode["state"]!.GetValue<string>());
        Assert.Equal("alive", roomNode["processAlive"]!.GetValue<string>());
        Assert.True(roomNode.ContainsKey("live"));
        var live = roomNode["live"]!.AsObject();
        Assert.Equal(100, live["billedTokens"]!.GetValue<long>());
        Assert.True(live["billedIsFloor"]!.GetValue<bool>());
        Assert.Equal(10, live["cacheReadTokens"]!.GetValue<long>());
        Assert.True(roomNode.ContainsKey("stdout_last_write_ago_sec"));
    }

    /// <summary>Builds a room with one "architect" step Running under a real captured `.stdout.log`,
    /// recorded engine identity <paramref name="identity"/>, and a claude bindings.json entry.</summary>
    private async Task<(string RoomDir, ExecutionId ExecutionId)> CreateRunningRoomAsync(
        string roomName, (int Pid, DateTimeOffset StartTime) identity)
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, roomName);
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-a"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5)),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var execId = new ExecutionId($"exec-{roomName}");
        var req = new ExecutionRequest(
            execId, new WorkflowId("wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromMinutes(5), [], new Dictionary<StepId, ExecutionId>(), Adapter: "claude");

        var logWriter = new FlowEventLogWriter(Path.Combine(room, "flow.jsonl"));
        await logWriter.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: identity.Pid, EngineStartTime: identity.StartTime),
            TestContext.Current.CancellationToken);
        await logWriter.DisposeAsync();

        // The Running execution's own captured stdout, at the exact path ArtifactManager resolves.
        var stdoutDir = Path.Combine(room, "artifacts", $"execution_{execId.Value}");
        Directory.CreateDirectory(stdoutDir);
        await File.WriteAllTextAsync(
            Path.Combine(stdoutDir, ".stdout.log"),
            """{"type":"assistant","message":{"id":"msg_1","usage":{"cache_creation_input_tokens":100,"cache_read_input_tokens":10}}}""" + "\n",
            TestContext.Current.CancellationToken);

        return (room, execId);
    }
}
