using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;

namespace Aer.Ui.Tests;

/// <summary>
/// M14 Phase 1's completion gate (issue #118): proves the seam end to end against a real task
/// directory — a real bound snapshot and a real Flow Event Store, produced through the exact same
/// <c>MutationInterface.StartWorkflowAsync</c> write path <c>Aer.Cli</c>'s <c>aer run</c> uses
/// (<c>Aer.Flow.Tests.EndToEnd.WorkflowEndToEndTests</c>' convention), then read back exclusively
/// through <see cref="RoomProjectionLoader"/> — never by constructing a <see cref="FlowState"/> by
/// hand.
/// </summary>
public class RoomProjectionLoaderTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task Loads_a_bound_snapshot_and_projects_state_from_a_real_room_directory()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-task-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ShellWorkerCommands.WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    ShellWorkerCommands.CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    ShellWorkerCommands.CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);

                await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-ui-e2e"),
                    roomDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(roomDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            // Not Assert.Equal(snapshot, projection.Snapshot): WorkflowDefinitionSnapshot's Steps
            // is a List<T>, which has no value-equality override, so a record freshly deserialized
            // from disk never structurally equals the in-memory instance it was persisted from.
            Assert.Equal(snapshot.WorkflowDefinitionSnapshotId, projection.Snapshot.WorkflowDefinitionSnapshotId);
            Assert.Equal(WorkflowStatus.Terminal, projection.State.Status);
            var stepStatusByStepId = projection.State.Steps.ToDictionary(step => step.StepId, step => step.Status);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Architect]);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Critic]);
            Assert.Equal(StepStatus.Succeeded, stepStatusByStepId[Publisher]);

            // M14 Phase 4 (issue #121): the same run also projects real artifact lineage — actual
            // files on disk, and each downstream step's input traced back to the exact upstream
            // execution that produced it.
            var executionByStepId = projection.Lineage.Executions
                .Where(execution => execution.StepId is not null)
                .ToDictionary(execution => execution.StepId!.Value);

            Assert.Equal(["plan"], executionByStepId[Architect].OutputFiles);
            Assert.Empty(executionByStepId[Architect].Inputs);

            var criticInput = Assert.Single(executionByStepId[Critic].Inputs);
            Assert.Equal("plan", criticInput.InputName);
            Assert.Equal(Architect, criticInput.ProducerStepId);
            Assert.Equal(executionByStepId[Architect].ExecutionId, criticInput.ProducerExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ReportsStatusAndArchivedStateWithoutRequiringLineageProjection()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ShellWorkerCommands.WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    ShellWorkerCommands.CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    ShellWorkerCommands.CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);
                await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-ui-fleet"), roomDirectory, snapshot, bindings,
                    Path.Combine(roomDirectory, "artifacts"), reader, writer, dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFileName(roomDirectory), fleetItem.FriendlyName);
            Assert.Equal(snapshot.WorkflowTemplateId.Value, fleetItem.TypeLabel);
            // #1049: the fleet carries the canonical DeriveStatus text/state now, not raw WorkflowStatus.
            // A completed run reads "Finished" (RoomCardStatus.Finished), not "Terminal".
            Assert.Equal("Finished", fleetItem.StatusText);
            Assert.Equal(RoomCardStatus.Finished, fleetItem.Status);
            Assert.Equal(0, fleetItem.PausedStepCount);
            Assert.False(fleetItem.IsArchived);

            // #322: a DAG task carries no serialized timestamp, so created/updated come from its own
            // data files -- snapshot.json (written once at creation) and flow.jsonl (append-only).
            Assert.NotEqual(default, fleetItem.Created);
            Assert.NotEqual(default, fleetItem.Updated);
            Assert.True(fleetItem.Updated >= fleetItem.Created);
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(roomDirectory, "snapshot.json"))),
                fleetItem.Created);
            Assert.Equal(
                new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(roomDirectory, "flow.jsonl"))),
                fleetItem.Updated);

            await RoomLifecycle.ArchiveAsync(roomDirectory, TestContext.Current.CancellationToken);
            var archivedItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.True(archivedItem.IsArchived);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ForASessionNeverRun_ReportsNotYetRunInsteadOfThrowing()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-session-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-fleet", roomDirectory, "claude", cancellationToken: TestContext.Current.CancellationToken);

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("interactive session", fleetItem.TypeLabel);
            Assert.Equal("Not yet run", fleetItem.StatusText);
            Assert.Equal(0, fleetItem.PausedStepCount);
            Assert.False(fleetItem.IsArchived);
            // #1044: a session row carries the id it taps into (row-as-place). Polarity partner is the
            // workflow test below, which pins SessionId null.
            Assert.Equal("sess-fleet", fleetItem.SessionId);

            // #322: a session (even one that never ran, so has no snapshot) takes its created/updated
            // straight from the durable in-data source, .aer/room.json -- not from filesystem times.
            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(
                Path.Combine(roomDirectory, ".aer", "room.json"), TestContext.Current.CancellationToken);
            Assert.NotNull(metadata);
            Assert.Equal(metadata.CreatedAt, fleetItem.Created);
            Assert.Equal(metadata.UpdatedAt, fleetItem.Updated);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #445/#390: the loader surfaces the runtime conversational gate a worker is blocked on, projected
    /// from <c>room.jsonl</c> — the second journal, per <c>RoomProjection.PendingPermission</c>, not the one the rest of the
    /// projection reads. Without this the mid-turn ask is journaled but never reaches a screen. Walks
    /// the whole progression in one room so the polarity is asserted in BOTH directions: absent journal
    /// -> null, an open ask -> the gate, an answered ask -> null again.
    /// </summary>
    [Fact]
    public async Task LoadAsync_surfaces_an_open_permission_gate_from_room_jsonl_and_clears_it_when_answered()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-gate-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ShellWorkerCommands.WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    ShellWorkerCommands.CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    ShellWorkerCommands.CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var reader = new FlowEventLogReader(logPath);
                var dispatcher = new CoreDispatcher(writer);
                await MutationInterface.StartWorkflowAsync(
                    new WorkflowId("wf-ui-gate"), roomDirectory, snapshot, bindings,
                    Path.Combine(roomDirectory, "artifacts"), reader, writer, dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            // Arm 1 — no room.jsonl at all: the common case, and it must not throw or invent a gate.
            var beforeAsk = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Null(beforeAsk.PendingPermission);

            // Arm 2 — an open ask journaled to room.jsonl. Writer disposed before the load so the read
            // is not racing an open append handle (the daemon's own ordering).
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            await using (var roomWriter = new RoomEventLogWriter(roomLogPath))
            {
                var roomReader = new RoomEventLogReader(roomLogPath);
                await RoomMutationInterface.RaisePermissionAsync(
                    roomDirectory, roomReader, roomWriter, "req-gate-1", new ExecutionId("ex-1"),
                    new StepId("architect"), "chat-worker", "claude", "corr-1", "Bash",
                    "{\"command\":\"ls\"}", "Bash", cancellationToken: TestContext.Current.CancellationToken);
            }

            var withAsk = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(withAsk.PendingPermission);
            Assert.Equal("req-gate-1", withAsk.PendingPermission!.PermissionRequestId);
            Assert.Equal("Bash", withAsk.PendingPermission.ToolName);
            Assert.Equal("chat-worker", withAsk.PendingPermission.WorkerId);

            // Arm 3 — the answer clears it, in the same room the ask opened it. This is the both-directions
            // control: if the projector ignored the Answered event, arm 2 could pass while the gate never closed.
            await using (var roomWriter = new RoomEventLogWriter(roomLogPath))
            {
                var roomReader = new RoomEventLogReader(roomLogPath);
                await RoomMutationInterface.AnswerPermissionAsync(
                    roomDirectory, roomReader, roomWriter, "req-gate-1", "AllowOnce", "{}", "ok", "human",
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var afterAnswer = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Null(afterAnswer.PendingPermission);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ForAWorkflowRoom_LabelsWorkflowFromRoomKindMarker()
    {
        // Polarity partner to the interactive case above (#443): a workflow room writes .aer/room.json
        // with Kind=Workflow at materialization, and the fleet label must read that marker as
        // "workflow", never "interactive session". Together the two tests pin RoomProjectionLoader's
        // kind discrimination on room.json in both directions, from the marker itself rather than from
        // a file's mere presence.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-workflow-{Guid.NewGuid():N}");
        try
        {
            await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                "solo-run", "claude", null, roomDirectory, "a prompt",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(
                RoomKind.Workflow,
                await InteractiveSessionMaterializer.ReadRoomKindAsync(roomDirectory, TestContext.Current.CancellationToken));

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotEqual("interactive session", fleetItem.TypeLabel);
            Assert.Equal("workflow", fleetItem.TypeLabel);
            // #1044 polarity: a workflow room has no session id to tap into.
            Assert.Null(fleetItem.SessionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_directory_with_no_snapshot_is_reported_as_not_a_room_directory()
    {
        var notARoomDirectory = Path.Combine(Path.GetTempPath(), $"ui-not-a-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(notARoomDirectory);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidRoomDirectoryException>(
                () => RoomProjectionLoader.LoadAsync(notARoomDirectory, TestContext.Current.CancellationToken));

            Assert.Contains(notARoomDirectory, exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(notARoomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_TaskWithJournalEvents_ReportsNewestEventTimestamp()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-lastact-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-1")), TestContext.Current.CancellationToken);
            }

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(fleetItem.LastActivityAt);
            Assert.True(fleetItem.LastActivityAt >= fleetItem.Created);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_EmptyNoJournalTask_FallsBackToDurableCreatedAt()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-lastact-empty-{Guid.NewGuid():N}");
        try
        {
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                "sess-empty", roomDirectory, "claude", cancellationToken: TestContext.Current.CancellationToken);

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(fleetItem.LastActivityAt);
            Assert.Equal(fleetItem.Created, fleetItem.LastActivityAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_Polarity_AppendingNewEventReordersTaskToTop()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var dirA = Path.Combine(Path.GetTempPath(), $"ui-lastact-polarity-a-{Guid.NewGuid():N}");
        var dirB = Path.Combine(Path.GetTempPath(), $"ui-lastact-polarity-b-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(dirA, "snapshot.json"), TestContext.Current.CancellationToken);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(dirB, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPathA = Path.Combine(dirA, "flow.jsonl");
            var logPathB = Path.Combine(dirB, "flow.jsonl");

            await using (var writerA = new FlowEventLogWriter(logPathA))
            {
                await writerA.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-a1")), TestContext.Current.CancellationToken);
            }

            // Write B after A so B has a newer timestamp
            await using (var writerB = new FlowEventLogWriter(logPathB))
            {
                await writerB.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-b1")), TestContext.Current.CancellationToken);
            }

            var itemA = await RoomProjectionLoader.LoadFleetStatusAsync(dirA, TestContext.Current.CancellationToken);
            var itemB = await RoomProjectionLoader.LoadFleetStatusAsync(dirB, TestContext.Current.CancellationToken);

            Assert.NotNull(itemA.LastActivityAt);
            Assert.NotNull(itemB.LastActivityAt);

            // Now append a new event to task A's journal
            await using (var writerA = new FlowEventLogWriter(logPathA))
            {
                await writerA.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId("exec-a2")), TestContext.Current.CancellationToken);
            }

            var itemAUpdated = await RoomProjectionLoader.LoadFleetStatusAsync(dirA, TestContext.Current.CancellationToken);
            Assert.True(itemAUpdated.LastActivityAt > itemA.LastActivityAt);
            Assert.True(itemAUpdated.LastActivityAt >= itemB.LastActivityAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dirA);
            DirectoryCleanup.DeleteRecursively(dirB);
        }
    }

    // #1049 polarity pair: the fleet path (LoadFleetStatusAsync, empty history/lineage) must resolve a
    // paused room's pause KIND, not just report "Paused". A NeedsInput pause is an ordinary chat turn
    // ("reply"); a ReadyForReview pause is an approval gate ("review"). One condition apart, opposite
    // words — if the loader stopped threading the snapshot's PausePoint the reply arm would default to
    // "review", and if it reverted to raw WorkflowStatus both would read "Paused".

    [Fact]
    public async Task LoadFleetStatusAsync_ForANeedsInputPause_ReadsWaitingForYourReply()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(PausePointKind.NeedsInput);
        try
        {
            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("Waiting for your reply", fleetItem.StatusText);
            Assert.Equal(RoomCardStatus.NeedsYou, fleetItem.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatusAsync_ForAReadyForReviewPause_ReadsWaitingForYourReview()
    {
        var roomDirectory = await CreatePausedRoomDirectoryAsync(PausePointKind.ReadyForReview);
        try
        {
            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("Waiting for your review", fleetItem.StatusText);
            Assert.Equal(RoomCardStatus.NeedsYou, fleetItem.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>Builds a room paused at Critic with the given pause kind, via hand-written FlowEvents
    /// (MainWindowProjectionTests' convention) — the lightest path to a Paused projection.</summary>
    private static async Task<string> CreatePausedRoomDirectoryAsync(PausePointKind pauseKind)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("architect-critic"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(Architect, "architect", ["goal"], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3)),
                new WorkflowStepDefinition(
                    Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [Architect], Kind: pauseKind)),
            ]));

        var architectExecutionId = new ExecutionId("a-1");
        var criticExecutionId = new ExecutionId("c-1");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-paused-{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

        await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl"));
        foreach (var flowEvent in new FlowEvent[]
        {
            new FlowEvent.ExecutionRequestAccepted(MakePausedStepRequest(architectExecutionId, Architect)),
            new FlowEvent.ExecutionSucceeded(architectExecutionId),
            new FlowEvent.ExecutionRequestAccepted(MakePausedStepRequest(criticExecutionId, Critic)),
            new FlowEvent.ExecutionSucceeded(criticExecutionId),
            new FlowEvent.WorkflowPaused(criticExecutionId, Critic),
        })
        {
            await writer.AppendAsync(flowEvent, TestContext.Current.CancellationToken);
        }

        return roomDirectory;
    }

    private static ExecutionRequest MakePausedStepRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("wf-1"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public async Task LoadFleetStatus_RoomWithJournaledAsk_IsNeedsYou()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-ask-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakePausedStepRequest(new ExecutionId("exec-01"), Architect)), TestContext.Current.CancellationToken);
            }

            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            var askedEvent = new RoomEvent.RuntimePermissionAsked(
                "req-101",
                new ExecutionId("exec-01"),
                Architect,
                "worker-alpha",
                "claude",
                "tool_use_123",
                "WriteFiles",
                """{"path":"test.txt"}""",
                "WriteFiles",
                DateTimeOffset.UtcNow);

            await using (var roomWriter = new RoomEventLogWriter(roomLogPath))
            {
                await roomWriter.AppendAsync(askedEvent, TestContext.Current.CancellationToken);
            }

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(RoomCardStatus.NeedsYou, fleetItem.Status);
            Assert.Equal("Permission requested", fleetItem.StatusText);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadFleetStatus_AskAnsweredOrRevoked_IsNotNeedsYou()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");

        // Case 1: Answered
        var roomDirectory1 = Path.Combine(Path.GetTempPath(), $"ui-fleet-answered-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory1, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory1, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakePausedStepRequest(new ExecutionId("exec-01"), Architect)), TestContext.Current.CancellationToken);
            }

            var askedAt = DateTimeOffset.UtcNow;
            var askedEvent = new RoomEvent.RuntimePermissionAsked(
                "req-101",
                new ExecutionId("exec-01"),
                Architect,
                "worker-alpha",
                "claude",
                "tool_use_123",
                "WriteFiles",
                """{"path":"test.txt"}""",
                "WriteFiles",
                askedAt);

            var answeredEvent = new RoomEvent.RuntimePermissionAnswered(
                "req-101",
                "AllowOnce",
                null,
                "Approved",
                "operator-bob",
                askedAt.AddSeconds(5));

            var roomLogPath1 = Path.Combine(roomDirectory1, "room.jsonl");
            await using (var roomWriter = new RoomEventLogWriter(roomLogPath1))
            {
                await roomWriter.AppendAsync(askedEvent, TestContext.Current.CancellationToken);
                await roomWriter.AppendAsync(answeredEvent, TestContext.Current.CancellationToken);
            }

            var fleetItem1 = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory1, TestContext.Current.CancellationToken);
            Assert.Equal(RoomCardStatus.Running, fleetItem1.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory1);
        }

        // Case 2: Revoked
        var roomDirectory2 = Path.Combine(Path.GetTempPath(), $"ui-fleet-revoked-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory2, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory2, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakePausedStepRequest(new ExecutionId("exec-01"), Architect)), TestContext.Current.CancellationToken);
            }

            var askedAt = DateTimeOffset.UtcNow;
            var askedEvent = new RoomEvent.RuntimePermissionAsked(
                "req-102",
                new ExecutionId("exec-01"),
                Architect,
                "worker-alpha",
                "claude",
                "tool_use_123",
                "WriteFiles",
                """{"path":"test.txt"}""",
                "WriteFiles",
                askedAt);

            var revokedEvent = new RoomEvent.RuntimePermissionRevoked(
                "req-102",
                "TurnEnded",
                askedAt.AddSeconds(5));

            var roomLogPath2 = Path.Combine(roomDirectory2, "room.jsonl");
            await using (var roomWriter = new RoomEventLogWriter(roomLogPath2))
            {
                await roomWriter.AppendAsync(askedEvent, TestContext.Current.CancellationToken);
                await roomWriter.AppendAsync(revokedEvent, TestContext.Current.CancellationToken);
            }

            var fleetItem2 = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory2, TestContext.Current.CancellationToken);
            Assert.Equal(RoomCardStatus.Running, fleetItem2.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory2);
        }
    }

    [Fact]
    public async Task LoadFleetStatus_NoRoomJournal_StillLoads()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-fleet-nojournal-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakePausedStepRequest(new ExecutionId("exec-01"), Architect)), TestContext.Current.CancellationToken);
            }

            // Ensure no room.jsonl exists
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            Assert.False(File.Exists(roomLogPath));

            var fleetItem = await RoomProjectionLoader.LoadFleetStatusAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Equal("Working — architect", fleetItem.StatusText);
            Assert.Equal(RoomCardStatus.Running, fleetItem.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
