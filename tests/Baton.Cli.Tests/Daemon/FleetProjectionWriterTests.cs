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
            // #1782: WriteAtomic now owns its own retry against a transient sharing violation, so this
            // tight back-to-back loop calls it directly rather than through a test-side wrapper -- the
            // property under test (a landed read is never torn) still gets exercised hundreds of times
            // against a genuinely concurrent reader.
            FleetProjectionWriter.WriteAtomic(path, i % 2 == 0 ? contentB : contentA);
        }

        Volatile.Write(ref stop, true);
        reader.Join();

        Assert.Null(readerException);
    }

    /// <summary>#1782: a reader that opens the file with <see cref="FileShare.Read"/> only (the
    /// hostile case -- e.g. a naive poller that did not opt into <see cref="FileShare.Delete"/>) holds
    /// the target open across a write. The writer's retry must either land once the reader closes, or
    /// log-and-skip without throwing if the reader never does -- WriteAtomic must never throw out of
    /// the hosted service's tick for a transient sharing violation.</summary>
    [Fact]
    public async Task WriteAtomic_RetriesPastAHostileReader_ThatEventuallyCloses()
    {
        var path = Path.Combine(_tempHome, "projection.json");
        var original = "original-content";
        FleetProjectionWriter.WriteAtomic(path, original);

        using var blockingStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var updated = "updated-content-after-reader-closes";
        var writerTask = Task.Run(() => FleetProjectionWriter.WriteAtomic(path, updated), TestContext.Current.CancellationToken);

        // Give the writer a chance to hit -- and retry past -- the sharing violation before the
        // reader releases its handle, so the assertion actually exercises the retry path rather than
        // racing a writer that never contended in the first place.
        // wait-ok: fixed local delay bounding an in-process race window, not a wait for external state.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        blockingStream.Dispose();

        // wait-ok: upper bound on WriteAtomic's own bounded retry budget (5 attempts, backoff capped at 200ms) -- not a wait for external state.
        var completed = await Task.WhenAny(writerTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(writerTask, completed);
        Assert.True(writerTask.IsCompletedSuccessfully);

        // Confirms the write actually landed post-close rather than the test passing vacuously on a
        // writer that silently gave up: the file must hold the NEW content, not the original.
        Assert.Equal(updated, File.ReadAllText(path));
    }

    /// <summary>Polarity arm: a reader that never releases its handle within the retry budget must not
    /// crash the writer -- WriteAtomic logs and skips, leaving the prior content in place, and the
    /// original file must still be intact and readable afterward.</summary>
    [Fact]
    public void WriteAtomic_LogsAndSkips_WhenAHostileReaderNeverCloses()
    {
        var path = Path.Combine(_tempHome, "projection.json");
        var original = "original-content";
        FleetProjectionWriter.WriteAtomic(path, original);

        using var blockingStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exception = Record.Exception(() => FleetProjectionWriter.WriteAtomic(path, "content-that-never-lands"));

        Assert.Null(exception);
        blockingStream.Dispose();

        // The skipped write must not have corrupted the target: the reader's own view (still open
        // above) and a fresh read afterward both see the untouched original content.
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void GetInterval_ClampsPathologicalValue_InsteadOfOverflowing()
    {
        // Mirrors RoomRetentionSweepTests' identically-named test: a value whose seconds would
        // overflow TimeSpan.FromSeconds must collapse to MaxInterval, never throw.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { FleetProjectionIntervalSecondsOverride = "1e300" });

        var interval = FleetProjectionWriter.GetInterval();
        Assert.Equal(FleetProjectionWriter.MaxInterval, interval);
    }

    [Fact]
    public void GetInterval_LiftsSubSecondValue_ToMinInterval()
    {
        // Mirrors RoomRetentionSweepTests' identically-named test: a value below one second must lift
        // to MinInterval rather than pass through near-zero.
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { FleetProjectionIntervalSecondsOverride = "1e-9" });

        Assert.Equal(FleetProjectionWriter.MinInterval, FleetProjectionWriter.GetInterval());
    }

    [Fact]
    public async Task BuildProjectionJson_deserializes_into_FleetRoomStatusView_and_carries_derived_at()
    {
        var room = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName, "terminal-room");
        Directory.CreateDirectory(room);
        var sentinel = new WorkflowStatusView("Succeeded", [], ["/tmp/out.txt"], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        // A pruned execution directory containing an engine-written stream log alongside a worker
        // output. pruned[].bytes sums the whole thing unfiltered (matching pusher.py's own sum), not
        // #1351's listing filter -- see FleetProjectionWriter.cs's ComputePrunedInfo comment.
        var prunedExecDir = Path.Combine(room, "artifacts", "pruned", "execution_exec-1");
        Directory.CreateDirectory(prunedExecDir);
        await File.WriteAllTextAsync(
            Path.Combine(prunedExecDir, ".stdout.log"), new string('x', 500), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(prunedExecDir, "output.txt"), new string('y', 300), TestContext.Current.CancellationToken);

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

        var prunedItem = Assert.Single(roomObject["pruned"]!["items"]!.AsArray());
        Assert.Equal(800, prunedItem!["bytes"]!.GetValue<long>());
    }

    /// <summary>Pins the `live`-vs-diagnostics gating split <see cref="FleetProjectionWriter.BuildProjectionJsonAsync"/>'s
    /// own remarks state -- see that method for why.</summary>
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
