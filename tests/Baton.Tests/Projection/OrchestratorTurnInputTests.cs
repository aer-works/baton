using Baton.Domain;
using Baton.Projection;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Projection;

[Collection(ConsoleErrorCaptureCollection.Name)]
public class OrchestratorTurnInputTests
{
    private static async Task<string> CreateTestRoomAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_turn_input_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);

        var roomLogPath = Path.Combine(tempDir, "room.jsonl");
        await using var writer = new RoomEventLogWriter(roomLogPath);

        var dispatch = new RoomEvent.HeldWorkDispatched(
            Ref: new HeldWorkRef("lane-1"),
            Shape: "test-shape",
            Budget: TimeSpan.FromMinutes(5),
            DeciderIdentity: "human");

        await writer.AppendAsync(dispatch, TestContext.Current.CancellationToken);

        return tempDir;
    }

    [Fact]
    public async Task First_turn_no_cursor_file_gets_full_history_as_delta_and_is_cold_start()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var wake = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.DispatchedWorkflowTerminated);
            var input = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake], TestContext.Current.CancellationToken);

            Assert.True(input.IsColdStart);
            Assert.Null(input.InitialCursor);
            Assert.Single(input.EventDelta);
            Assert.Single(input.Wakes);
            Assert.Equal(1, input.TotalEventCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Second_turn_after_commit_gets_only_delta()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var wake1 = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.DispatchedWorkflowTerminated);
            var turn1 = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake1], TestContext.Current.CancellationToken);
            Assert.True(turn1.IsColdStart);
            Assert.Single(turn1.EventDelta);

            OrchestratorTurnInput.CommitTurn(roomDir, turn1);

            // Append a second event to room journal
            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                var escalated = new RoomEvent.HeldWorkEscalated(
                    Ref: new HeldWorkRef("lane-1"),
                    ToWhom: "operator");
                await writer.AppendAsync(escalated, TestContext.Current.CancellationToken);
            }

            var wake2 = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.EscalatedWorkflowTerminated);
            var turn2 = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake2], TestContext.Current.CancellationToken);

            Assert.False(turn2.IsColdStart);
            Assert.NotNull(turn2.InitialCursor);
            Assert.Equal(1, turn2.InitialCursor!.ProcessedEventCount);
            Assert.Single(turn2.EventDelta);
            Assert.IsType<RoomEvent.HeldWorkEscalated>(turn2.EventDelta[0]);
            Assert.Equal(2, turn2.TotalEventCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Crash_between_assemble_and_commit_replays_same_delta()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var wake = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.DispatchedWorkflowTerminated);
            var turnAttempt1 = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake], TestContext.Current.CancellationToken);
            Assert.Single(turnAttempt1.EventDelta);

            // "Crash" -- no CommitTurn call.

            // Next wake / turn assembly replays identical delta
            var turnAttempt2 = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake], TestContext.Current.CancellationToken);
            Assert.True(turnAttempt2.IsColdStart);
            Assert.Single(turnAttempt2.EventDelta);
            Assert.Equal(turnAttempt1.EventDelta[0], turnAttempt2.EventDelta[0]);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Commit_advances_so_next_delta_is_empty_when_no_new_events()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var wake = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.DispatchedWorkflowTerminated);
            var turn1 = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake], TestContext.Current.CancellationToken);
            Assert.Single(turn1.EventDelta);

            OrchestratorTurnInput.CommitTurn(roomDir, turn1);

            var turn2 = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake], TestContext.Current.CancellationToken);
            Assert.False(turn2.IsColdStart);
            Assert.Empty(turn2.EventDelta);
            Assert.Equal(1, turn2.TotalEventCount);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Polarity_corrupt_cursor_file_falls_back_to_cold_start_loudly()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var batonDir = Path.Combine(roomDir, ".baton");
            Directory.CreateDirectory(batonDir);
            var cursorFile = Path.Combine(batonDir, "orchestrator-session.json");
            File.WriteAllText(cursorFile, "{ corrupt json ... }}}");

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            OrchestratorTurnInput input;
            try
            {
                input = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.True(input.IsColdStart);
            Assert.Null(input.InitialCursor);
            Assert.Single(input.EventDelta);

            var errOutput = sw.ToString();
            Assert.Contains("Cold start LOUDLY", errOutput);

            // Clean up single cursor file via FileCleanup
            FileCleanup.Delete(cursorFile);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Wakes_pass_through_unchanged_so_one_input_carries_the_whole_pending_set()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var wake1 = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.DispatchedWorkflowTerminated);
            var wake2 = new RoomWake(new HeldWorkRef("lane-2"), RoomWakeKind.DispatchOrphaned);

            var input = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake1, wake2], TestContext.Current.CancellationToken);

            Assert.Equal(2, input.Wakes.Count);
            Assert.Contains(wake1, input.Wakes);
            Assert.Contains(wake2, input.Wakes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Assembling_without_committing_leaves_no_cursor_on_disk()
    {
        var roomDir = await CreateTestRoomAsync();
        try
        {
            var wake = new RoomWake(new HeldWorkRef("lane-1"), RoomWakeKind.DispatchedWorkflowTerminated);
            var turn = await OrchestratorTurnInput.AssembleAsync(roomDir, [wake], TestContext.Current.CancellationToken);

            // Assembly alone must leave NO cursor on disk -- only an explicit CommitTurn does,
            // which is what makes a crashed turn re-schedulable.
            var cursorAfterAssembleNoCommit = OrchestratorSessionStore.Load(roomDir);
            Assert.Null(cursorAfterAssembleNoCommit);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
}
