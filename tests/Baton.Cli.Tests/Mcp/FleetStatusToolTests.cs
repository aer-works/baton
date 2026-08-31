using System.Diagnostics;
using System.Text.Json;
using Baton.Vendors;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Cli.Mcp;

namespace Baton.Cli.Tests.Mcp;

/// <summary>
/// Unit and integration coverage for <see cref="FleetStatusTool"/> (#1392 Spike 1).
/// Validates root enumeration, terminal sentinel fast path, active room projection,
/// filtering, and graceful error handling on malformed rooms.
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
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
        // #1509/#1510: a step's very first execution has no failure history to derive a real
        // attempt ordinal or failure classification from -- both fields must be OMITTED, never
        // defaulted to "attempt 1" or a fabricated classification. Object-level Assert.Null cannot
        // tell an omitted key apart from a serialized null, so also pin the wire shape directly
        // (PR #1504 review finding A: only a serialized assertion catches a dropped
        // JsonIgnore(WhenWritingNull)).
        Assert.Null(singleStep.Attempt);
        Assert.Null(singleStep.MaxAttempts);
        Assert.Null(singleStep.FailureKind);
        Assert.Null(singleStep.RetryEligible);
        var wire = JsonSerializer.Serialize(singleRoom);
        Assert.DoesNotContain("\"attempt\"", wire);
        Assert.DoesNotContain("\"maxAttempts\"", wire);
        Assert.DoesNotContain("\"failureKind\"", wire);
        Assert.DoesNotContain("\"retryEligible\"", wire);
    }

    [Fact]
    public async Task ExhaustedUntilFailure_AttemptUndercountsByOne_AKnownFinding()
    {
        // #1509 finding (see report-1509.md): StateProjector deliberately does NOT increment
        // ConsecutiveFailureCount for FailureClassification.ExhaustedUntil (0026: a paced,
        // quota-exhausted wait is not a spent retry-budget attempt). Since `attempt` is derived
        // from that same count, the execution that actually reported ExhaustedUntil renders one
        // ordinal LOW -- here, a step's first-ever execution reports ExhaustedUntil and therefore
        // renders no `attempt` at all (count stays 0), even though `failureKind`/`retryEligible`
        // both correctly surface. This test pins the actual (imperfect but never-fabricated)
        // behavior, not an idealized one.
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "exhausted-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-exhausted"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("exhausted-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-exhausted-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("exhausted-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted"),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("ExhaustedUntil", singleStep.FailureKind);
        Assert.True(singleStep.RetryEligible);
        Assert.Null(singleStep.Attempt);
    }

    [Fact]
    public async Task FailedStep_SurfacesFailureKindAndRetryEligible_FromEngineClassification()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "failed-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-failed"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("failed-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-failed-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("failed-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.Retryable, "worker crashed"),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Failed", singleStep.State);
        // One consecutive failure recorded -> this failed execution WAS attempt 1, out of 3 allowed.
        Assert.Equal(1, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        Assert.Equal("Retryable", singleStep.FailureKind);
        Assert.True(singleStep.RetryEligible);
    }

    [Fact]
    public async Task PermanentFailure_SurfacesRetryEligibleFalse()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "permanent-fail-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-permanent"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("permanent-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-permanent-1");
        var req = new ExecutionRequest(
            execId, new WorkflowId("permanent-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(execId, FailureClassification.Permanent, "invalid config"),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Permanent", singleStep.FailureKind);
        Assert.False(singleStep.RetryEligible);
    }

    [Fact]
    public async Task RunningStep_SurfacesAttemptOrdinalAfterAPriorFailure()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "retrying-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-retrying"), "agent-worker", [], ["plan.md"], [], new RetryPolicy(3));
        var def = new WorkflowDefinition(new WorkflowTemplateId("retrying-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var firstExecId = new ExecutionId("exec-retrying-1");
        var firstReq = new ExecutionRequest(
            firstExecId, new WorkflowId("retrying-wf"), stepDef.StepId, stepDef.Worker,
            [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(firstReq), TestContext.Current.CancellationToken);
        await writer.AppendAsync(
            new FlowEvent.ExecutionFailed(firstExecId, FailureClassification.Retryable, "transient"),
            TestContext.Current.CancellationToken);

        var secondExecId = new ExecutionId("exec-retrying-2");
        var secondReq = firstReq with { ExecutionId = secondExecId };
        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(secondReq), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("exec-retrying-2", singleStep.Execution);
        // One prior consecutive failure -> this running execution is attempt 2 of 3.
        Assert.Equal(2, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        Assert.Null(singleStep.FailureKind);
        Assert.Null(singleStep.RetryEligible);
    }

    [Fact]
    public async Task TerminalSentinel_CarriesAttemptAndFailureKindThroughVerbatim()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "sentinel-retry-room");
        Directory.CreateDirectory(room);

        var step = new WorkflowStatusStepView(
            "step-a", "Failed", "exec-1", null, null, null, null, Attempt: 2, MaxAttempts: 3,
            FailureKind: "Permanent", RetryEligible: false);
        var sentinel = new WorkflowStatusView("Failed", [step], [], "invalid config", null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleStep = Assert.Single(Assert.Single(rooms!).Steps!);
        Assert.Equal(2, singleStep.Attempt);
        Assert.Equal(3, singleStep.MaxAttempts);
        Assert.Equal("Permanent", singleStep.FailureKind);
        Assert.False(singleStep.RetryEligible);
    }

    /// <summary>Same technique as <c>WorkflowStatusProjectorLivenessTests.DeadProcessIdentity</c>:
    /// capture a real process's identity while it is provably alive, then kill it, so the probe's
    /// OS-level checks see a genuinely dead PID rather than a fabricated one that might coincidentally
    /// collide with something else running on the host.</summary>
    private static (int Pid, DateTimeOffset StartTime) DeadProcessIdentity()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "30") { CreateNoWindow = true };

        using var process = Process.Start(psi)!;
        try
        {
            return (process.Id, new DateTimeOffset(process.StartTime).ToUniversalTime());
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    /// <summary>
    /// #1462: `fleet_status` must inherit `WorkflowStatusStepView.Liveness` off the SAME
    /// <see cref="WorkflowStatusProjector"/> projection `status --json` reads (spec/baton.md §3/§6) --
    /// never a second <see cref="Baton.Outcomes.EngineLivenessProbe"/> call. A fleet caller reading a
    /// "Running" step whose engine was SIGKILLed must be able to tell a dead engine from a merely slow
    /// one without a second, per-room `status --json` call.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithDeadEngine_ReportsDeadLivenessThroughFleetStatus()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "dead-engine-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-dead"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("dead-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-dead-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("dead-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: deadPid, EngineStartTime: deadStartTime),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("Running", singleStep.State);
        Assert.Equal("dead", singleStep.Liveness);
    }

    /// <summary>
    /// Polarity arm for the same #1462 fix, opposite direction: a step whose engine is genuinely
    /// alive must read "alive" (or be omitted entirely once non-Running), never silently coincide
    /// with the "dead" arm above -- proving `fleet_status` carries the probe's actual verdict rather
    /// than a hardcoded string.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithAliveEngine_ReportsAliveLivenessThroughFleetStatus()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "alive-engine-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-alive"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("alive-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-alive-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("alive-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(
            new FlowEvent.ExecutionRequestAccepted(req, EnginePid: livePid, EngineStartTime: liveStartTime),
            TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        var singleRoom = Assert.Single(rooms!);
        var singleStep = Assert.Single(singleRoom.Steps!);
        Assert.Equal("alive", singleStep.Liveness);
    }

    /// <summary>
    /// #1462: `fleet_status` must inherit `WorkflowStatusView.Rejected` off the same projection
    /// `status --json` reads (spec/baton.md §3/§6) -- copied from the terminal sentinel, since the
    /// sentinel already IS a <see cref="WorkflowStatusView"/>. A rejected room must read distinctly
    /// from an ordinary crashed one: both settle as `"state": "Failed"`, and `rejected` is the only
    /// structural fact telling them apart.
    /// </summary>
    [Fact]
    public async Task TerminalSentinel_RejectedRoom_ReportsRejectedTrue_DistinctFromOrdinaryFailure()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var rejectedRoom = Path.Combine(defaultRoomsDir, "rejected-room");
        var crashedRoom = Path.Combine(defaultRoomsDir, "crashed-room");
        Directory.CreateDirectory(rejectedRoom);
        Directory.CreateDirectory(crashedRoom);

        var rejectedSentinel = new WorkflowStatusView("Failed", [], [], "a step was rejected", null, Rejected: true);
        var crashedSentinel = new WorkflowStatusView("Failed", [], [], "the worker crashed", null, Rejected: false);
        await TerminalSentinelWriter.WriteAsync(rejectedRoom, rejectedSentinel, TestContext.Current.CancellationToken);
        await TerminalSentinelWriter.WriteAsync(crashedRoom, crashedSentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        Assert.NotNull(rooms);
        Assert.Equal(2, rooms!.Count);

        var rejected = rooms.First(r => r.Name == "rejected-room");
        Assert.Equal("Failed", rejected.State);
        Assert.True(rejected.Rejected);

        var crashed = rooms.First(r => r.Name == "crashed-room");
        Assert.Equal("Failed", crashed.State);
        Assert.False(crashed.Rejected);
        // Wire-level: a non-rejected room must OMIT the key, not emit "rejected": false -- the
        // omission rests on JsonIgnoreCondition.WhenWritingDefault, and only a serialized assertion
        // catches that attribute breaking.
        Assert.DoesNotContain("\"rejected\"", JsonSerializer.Serialize(crashed));
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

    /// <summary>
    /// #1503: the Running step's role/adapter/model/effort/timeout pass through from the room's real
    /// <c>bindings.json</c>, keyed by the same worker name <c>FlowEvent.ExecutionRequestAccepted</c>
    /// names for the Running step's execution.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithBindings_ReportsRoleAdapterModelEffortAndTimeoutFromBindingsJson()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "bound-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-bound"), "architect", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("bound-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("architect", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Draft a plan.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4",
                Effort: "high"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-bound-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("bound-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromMinutes(5),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(execId, Pid: 6001), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Equal("architect", singleRoom.Role);
        Assert.Equal("claude", singleRoom.Adapter);
        Assert.Equal("claude-opus-4", singleRoom.Model);
        Assert.Equal("high", singleRoom.Effort);
        Assert.Equal((long)TimeSpan.FromMinutes(5).TotalMilliseconds, singleRoom.TimeoutMs);
    }

    /// <summary>
    /// #1503 fail-open arm: a room with no <c>bindings.json</c> at all (pre-#153, or simply never
    /// written for this room) must still render its row -- role/adapter/model/effort/timeout are
    /// just absent, never a thrown error or a missing room.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithNoBindingsFile_OmitsBindingFieldsButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "unbound-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-unbound"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("unbound-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-unbound-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("unbound-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
        Assert.Null(singleRoom.Model);
        Assert.Null(singleRoom.Effort);
        Assert.Null(singleRoom.TimeoutMs);
        AssertBindingFieldsAbsentFromWire(singleRoom);
    }

    /// <summary>
    /// Wire-level "absent, not emitted null" for all five binding fields — object-level
    /// <c>Assert.Null</c> cannot distinguish an omitted key from a serialized <c>"field": null</c>
    /// round-tripped back, which is exactly what a dropped <c>JsonIgnore(WhenWritingNull)</c> would
    /// ship silently (PR #1504 review finding A).
    /// </summary>
    private static void AssertBindingFieldsAbsentFromWire(FleetRoomStatusView room)
    {
        var wire = JsonSerializer.Serialize(room);
        Assert.DoesNotContain("\"role\"", wire);
        Assert.DoesNotContain("\"adapter\"", wire);
        Assert.DoesNotContain("\"model\"", wire);
        Assert.DoesNotContain("\"effort\"", wire);
        Assert.DoesNotContain("\"timeoutMs\"", wire);
    }

    /// <summary>
    /// #1503 fail-open arm, opposite corruption mode: a <c>bindings.json</c> that exists but is not
    /// valid JSON must degrade the same way a missing file does -- the room row still renders with
    /// everything else intact, only the binding fields absent.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_RunningStepWithCorruptBindingsFile_OmitsBindingFieldsButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "corrupt-bindings-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-corrupt"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("corrupt-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            BatonPaths.RoomBindingsFile(room), "{ not valid json", TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-corrupt-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("corrupt-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Null(singleRoom.Error);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
        Assert.Null(singleRoom.Model);
        Assert.Null(singleRoom.Effort);
        Assert.Null(singleRoom.TimeoutMs);
        AssertBindingFieldsAbsentFromWire(singleRoom);
    }

    /// <summary>
    /// #1503 fail-open arm three (PR #1504 review finding B): a VALID <c>bindings.json</c> whose
    /// dictionary simply lacks the Running step's worker role degrades identically to a missing
    /// file — display metadata fails open where <c>ResumeCommand</c> treats the same situation as a
    /// hard error, because a fleet row without chips beats a fleet call that throws.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_ValidBindingsWithoutTheRunningRolesKey_OmitsBindingFieldsButStillRendersRow()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "role-missing-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-role-missing"), "agent-worker", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("role-missing-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, "snapshot.json"), TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["some-other-role"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("some-other-role", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Do something else.",
                TimeSpan.FromMinutes(5),
                Model: "claude-opus-4",
                Effort: "high"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var logPath = Path.Combine(room, "flow.jsonl");
        var writer = new FlowEventLogWriter(logPath);
        var execId = new ExecutionId("exec-role-missing-1");
        var req = new ExecutionRequest(
            execId,
            new WorkflowId("role-missing-wf"),
            stepDef.StepId,
            stepDef.Worker,
            [],
            [],
            TimeSpan.FromSeconds(30),
            [],
            new Dictionary<StepId, ExecutionId>());

        await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(req), TestContext.Current.CancellationToken);
        await writer.DisposeAsync();

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Running", singleRoom.State);
        Assert.Null(singleRoom.Error);
        AssertBindingFieldsAbsentFromWire(singleRoom);
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

    /// <summary>
    /// #1499: the terminal-sentinel fast path never reads <c>bindings.json</c> for
    /// role/adapter/model/effort/timeout (#1503, spec/baton.md §6 schema), but a room's <c>--label</c>
    /// is a room-level fact, not scoped to a Running step -- so it must still surface here, unlike
    /// that quartet.
    /// </summary>
    [Fact]
    public async Task TerminalFastPath_WithLabelInBindings_ReportsLabel()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "labeled-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advise"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                Label: "env-snapshot lane"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.Equal("Succeeded", singleRoom.State);
        Assert.Equal("env-snapshot lane", singleRoom.Label);
        // The sentinel fast path never resolves a Running binding -- the quartet stays absent even
        // though the label, read independently, is present.
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
    }

    [Fact]
    public async Task TerminalFastPath_WithNoBindingsFile_OmitsLabelFromTheWire()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "unlabeled-terminal-room");
        Directory.CreateDirectory(room);

        var sentinel = new WorkflowStatusView("Succeeded", [], [], null, null);
        await TerminalSentinelWriter.WriteAsync(room, sentinel, TestContext.Current.CancellationToken);

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("{}"), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.Null(singleRoom.Label);
        var wire = JsonSerializer.Serialize(singleRoom);
        Assert.DoesNotContain("\"label\"", wire);
    }

    /// <summary>
    /// #1499: an active room with NO Running step at all (no <c>flow.jsonl</c>, so the workflow
    /// projects as Pending) -- <see cref="FleetStatusTool.TryResolveRunningBindingAsync"/> would never
    /// even attempt a bindings read here, since role/adapter/model/effort/timeout are scoped to a
    /// Running step this room does not have. The label read is a separate, ungated path, so it must
    /// still come back present.
    /// </summary>
    [Fact]
    public async Task ActiveRoom_WithNoRunningStep_StillReportsLabelButNotTheRunningStepQuartet()
    {
        var defaultRoomsDir = Path.Combine(_tempHome, BatonPaths.RoomsDirectoryName);
        var room = Path.Combine(defaultRoomsDir, "pending-labeled-room");
        Directory.CreateDirectory(room);

        var stepDef = new WorkflowStepDefinition(new StepId("step-pending"), "advise", [], [], [], new RetryPolicy(1));
        var def = new WorkflowDefinition(new WorkflowTemplateId("pending-wf"), 1, [stepDef]);
        var snapshot = SnapshotBinder.Bind(def);
        var snapshotPath = Path.Combine(room, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["advise"] = new WorkerBindingConfigEntry(
                "claude",
                new WorkerContract("advise", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                "Weigh the options.",
                TimeSpan.FromMinutes(5),
                Label: "env-snapshot lane"),
        };
        await WorkerBindingConfigWriter.SaveToFileAsync(
            bindings, BatonPaths.RoomBindingsFile(room), TestContext.Current.CancellationToken);

        // No flow.jsonl at all -- FlowEventLogReader.ReadAllEntriesWithTimestampsAsync treats a
        // missing log as zero entries, so the STEP projects Pending, never Running. (The room's own
        // top-level `state` still reads "Running" either way -- WorkflowOutcome.Describe reports the
        // overall WorkflowStatus, which starts Running before any step's own state does; the gate that
        // matters here is per-step.)

        var tool = new FleetStatusTool();
        var result = await tool.CallAsync(Parse("""{ "include_terminal": false }"""), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var rooms = JsonSerializer.Deserialize<List<FleetRoomStatusView>>(result.Text);
        var singleRoom = Assert.Single(rooms!);
        Assert.DoesNotContain(singleRoom.Steps ?? [], s => s.State == "Running");
        Assert.Equal("env-snapshot lane", singleRoom.Label);
        Assert.Null(singleRoom.Role);
        Assert.Null(singleRoom.Adapter);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
