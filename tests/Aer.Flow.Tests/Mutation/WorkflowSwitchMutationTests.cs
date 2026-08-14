using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// #1216's engine half: the room's workflow switch is a durable room-level fact, and it is refused
/// while the room has work in flight.
/// </summary>
/// <remarks>
/// The refusal's two arms are tested against the primitives the rule is actually written on — the
/// §15 flow lock and a genuinely <see cref="StepStatus.Paused"/> step — with the opposite polarity
/// beside each. The polarity matters more than usual here: the obvious wrong implementation (refuse
/// on <see cref="StepStatus.Running"/>) passes every refusal arm and fails only the permitting one,
/// because a room whose process died is <see cref="WorkflowStatus.Running"/> by definition (§6).
/// </remarks>
public class WorkflowSwitchMutationTests : IDisposable
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    private readonly string _roomDirectory;
    private readonly string _roomLogPath;

    public WorkflowSwitchMutationTests()
    {
        _roomDirectory = Path.Combine(Path.GetTempPath(), "aer_wf_switch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_roomDirectory);
        _roomLogPath = Path.Combine(_roomDirectory, "room.jsonl");
    }

    private static WorkflowDefinitionSnapshot Snapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("architect-critic"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
            new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static ExecutionRequest Request(ExecutionId executionId, StepId stepId) => new(
        executionId,
        new WorkflowId("wf-1"),
        stepId,
        "worker",
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromMinutes(10),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    /// <summary>A step accepted and never concluded — <see cref="WorkflowStatus.Running"/>, which §6 says is a live attempt OR a crash.</summary>
    private static FlowEvent[] StillRunning() =>
        [new FlowEvent.ExecutionRequestAccepted(Request(new ExecutionId("exec-1"), Architect))];

    private static FlowEvent[] PausedOnADecision() =>
    [
        new FlowEvent.ExecutionRequestAccepted(Request(new ExecutionId("exec-1"), Critic)),
        new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1")),
        new FlowEvent.WorkflowPaused(new ExecutionId("exec-1"), Critic),
    ];

    private Task<RoomState> SwitchAsync(bool isOn, FlowEvent[] flowEvents)
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        var writer = new RoomEventLogWriter(_roomLogPath);
        return SwitchCoreAsync(isOn, flowEvents, reader, writer);
    }

    private async Task<RoomState> SwitchCoreAsync(bool isOn, FlowEvent[] flowEvents, IRoomEventLogReader reader, RoomEventLogWriter writer)
    {
        await using (writer)
        {
            return await RoomMutationInterface.SetWorkflowSwitchAsync(
                _roomDirectory, isOn, "operator", reader, writer,
                new StubFlowLogReader(flowEvents), Snapshot(),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Switching_off_is_durable_and_absence_of_the_event_means_on()
    {
        var beforeAnySwitch = RoomProjector.Project([]);
        Assert.False(beforeAnySwitch.IsWorkflowOff);

        var afterOff = await SwitchAsync(isOn: false, []);

        Assert.True(afterOff.IsWorkflowOff);

        // Re-projected from what is actually on disk, not from the return value — the claim is that
        // it survives a restart, and only the journal can carry that.
        var replayed = RoomProjector.Project(
            await new RoomEventLogReader(_roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
        Assert.True(replayed.IsWorkflowOff);
    }

    [Fact]
    public async Task Switching_back_on_returns_the_room_to_on()
    {
        await SwitchAsync(isOn: false, []);
        var afterOn = await SwitchAsync(isOn: true, []);

        Assert.False(afterOn.IsWorkflowOff);
    }

    [Fact]
    public async Task A_room_whose_pump_is_alive_refuses_the_switch()
    {
        using var liveRun = ConcurrencyGuard.Acquire(_roomDirectory, "a live pump");

        var refusal = await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => SwitchAsync(isOn: false, StillRunning()));

        Assert.Contains("running", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_roomLogPath) && File.ReadAllText(_roomLogPath).Contains("workflowSwitched", StringComparison.Ordinal));
    }

    /// <summary>
    /// The arm that discriminates. Identical journal to the refusing case above — a step accepted and
    /// never concluded, so <see cref="WorkflowStatus.Running"/> — with only the lock released. An
    /// implementation testing <see cref="StepStatus.Running"/> instead of the lock refuses here too,
    /// and leaves every crashed room permanently unable to switch its workflow off.
    /// </summary>
    [Fact]
    public async Task A_room_whose_process_died_mid_run_permits_the_switch()
    {
        Assert.False(ConcurrencyGuard.IsHeld(_roomDirectory));

        var state = await SwitchAsync(isOn: false, StillRunning());

        Assert.True(state.IsWorkflowOff);
    }

    [Fact]
    public async Task A_room_paused_on_a_decision_refuses_the_switch()
    {
        var refusal = await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => SwitchAsync(isOn: false, PausedOnADecision()));

        Assert.Contains("critic", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decision", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both directions are refused: a paused room cannot be switched back ON either.</summary>
    [Fact]
    public async Task The_refusal_applies_to_switching_on_as_well_as_off()
    {
        await Assert.ThrowsAsync<InvalidRoomMutationException>(
            () => SwitchAsync(isOn: true, PausedOnADecision()));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DirectoryCleanup.DeleteRecursively(_roomDirectory);
    }

    /// <summary>
    /// An in-memory flow log. Deliberately not a <see cref="FlowEventLogReader"/> over a written
    /// file: what these tests vary is the projected step status, and going through the file would
    /// test the log format a second time rather than the rule.
    /// </summary>
    private sealed class StubFlowLogReader(IReadOnlyList<FlowEvent> events) : IEventLogReader
    {
        public Task<IReadOnlyList<FlowEvent>> ReadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(events);

        public Task<IReadOnlyList<CoreEvent>> ReadAllCoreEventsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CoreEvent>>([]);

        public Task<EventLogSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new EventLogSnapshot(events, []));

        public Task<EventLogSnapshot> ReadSnapshotFromOffsetAsync(long seekByteOffset, CancellationToken cancellationToken = default)
            => Task.FromResult(new EventLogSnapshot(events, []));
    }
}
