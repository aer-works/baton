using Baton.Domain;
using Baton.Projection;
using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Projection;

[Collection(ConsoleErrorCaptureCollection.Name)]
public class OrchestratorSessionStoreTests
{
    private static async Task<string> CreateTestRoomAsync(params RoomEvent[] events)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_cursor_test_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);

        var roomLogPath = Path.Combine(tempDir, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            foreach (var evt in events)
            {
                await writer.AppendAsync(evt, TestContext.Current.CancellationToken);
            }
        }

        return tempDir;
    }

    [Fact]
    public async Task Landmine_rewritten_journal_with_same_event_count_forces_cold_start()
    {
        var evt1 = new RoomEvent.HeldWorkDispatched(
            Ref: new HeldWorkRef("lane-1"),
            Shape: "shape-a",
            Budget: TimeSpan.FromMinutes(5),
            DeciderIdentity: "human");

        var evt2Original = new RoomEvent.HeldWorkEscalated(
            Ref: new HeldWorkRef("lane-1"),
            ToWhom: "operator-alice");

        var roomDir = await CreateTestRoomAsync(evt1, evt2Original);
        try
        {
            var turn1 = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            Assert.Equal(2, turn1.TotalEventCount);
            OrchestratorTurnInput.CommitTurn(roomDir, turn1);

            var cursorBeforeRewrite = OrchestratorSessionStore.Load(roomDir);
            Assert.NotNull(cursorBeforeRewrite);
            Assert.Equal(2, cursorBeforeRewrite!.ProcessedEventCount);
            Assert.NotNull(cursorBeforeRewrite.LastEventLineHash);

            // Rewrite room.jsonl with same count (2 events), but event 2 is rewritten to different content
            var evt2Rewritten = new RoomEvent.HeldWorkEscalated(
                Ref: new HeldWorkRef("lane-1"),
                ToWhom: "operator-bob");

            var roomLogPath = Path.Combine(roomDir, "room.jsonl");
            FileCleanup.Delete(roomLogPath);

            await using (var writer = new RoomEventLogWriter(roomLogPath))
            {
                await writer.AppendAsync(evt1, TestContext.Current.CancellationToken);
                await writer.AppendAsync(evt2Rewritten, TestContext.Current.CancellationToken);
            }

            // The cursor load must now fail loudly due to line hash mismatch and return null (cold start)
            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            OrchestratorSessionCursor? cursorAfterRewrite;
            OrchestratorTurnInput turn2;
            try
            {
                cursorAfterRewrite = OrchestratorSessionStore.Load(roomDir);
                turn2 = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Null(cursorAfterRewrite);
            Assert.True(turn2.IsColdStart);
            Assert.Null(turn2.InitialCursor);
            Assert.Equal(2, turn2.EventDelta.Count);
            Assert.Contains("Content identity hash mismatch", sw.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Unchanged_journal_with_matching_hash_resumes_at_cursor_without_cold_start()
    {
        var evt = new RoomEvent.HeldWorkDispatched(
            Ref: new HeldWorkRef("lane-1"),
            Shape: "shape-a",
            Budget: TimeSpan.FromMinutes(5),
            DeciderIdentity: "human");

        var roomDir = await CreateTestRoomAsync(evt);
        try
        {
            var turn1 = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            OrchestratorTurnInput.CommitTurn(roomDir, turn1);

            var loadedCursor = OrchestratorSessionStore.Load(roomDir);
            Assert.NotNull(loadedCursor);
            Assert.Equal(1, loadedCursor!.ProcessedEventCount);

            var turn2 = await OrchestratorTurnInput.AssembleAsync(roomDir, [], TestContext.Current.CancellationToken);
            Assert.False(turn2.IsColdStart);
            Assert.NotNull(turn2.InitialCursor);
            Assert.Empty(turn2.EventDelta);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    [Fact]
    public async Task Legacy_cursor_file_no_hash_nonzero_count_triggers_cold_start()
    {
        var evt = new RoomEvent.HeldWorkDispatched(
            Ref: new HeldWorkRef("lane-1"),
            Shape: "shape-a",
            Budget: TimeSpan.FromMinutes(5),
            DeciderIdentity: "human");

        var roomDir = await CreateTestRoomAsync(evt);
        try
        {
            var batonDir = Path.Combine(roomDir, ".baton");
            Directory.CreateDirectory(batonDir);
            var cursorPath = OrchestratorSessionStore.GetCursorFilePath(roomDir);
            File.WriteAllText(cursorPath, "{\"processedEventCount\":1,\"lastCompletedTurnAt\":\"2026-08-05T12:00:00Z\"}");

            using var sw = new StringWriter();
            var originalErr = Console.Error;
            Console.SetError(sw);

            OrchestratorSessionCursor? cursor;
            try
            {
                cursor = OrchestratorSessionStore.Load(roomDir);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            Assert.Null(cursor);
            Assert.Contains("carries no content identity hash", sw.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
}
