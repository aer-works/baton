using Aer.Adapters;
using Aer.Flow.Domain;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #1319 (PR A of #1306's three-way split) — the control arm of #1306's RP1. RP1 itself (the room-wide
/// turn lock removed, no metadata mutex, one turn silently lost) is #1306 PR C's proof; this only
/// demonstrates the mutex side, directly against <see cref="InteractiveSessionMaterializer.UpdateMetadataAsync"/>
/// rather than through the daemon's HTTP endpoints, because the still-present <c>SessionTurnLockFor</c>
/// would otherwise serialize two concurrent chat-turn completions before either reached room.json,
/// making a full end-to-end test pass regardless of whether this mutex does anything. Calling
/// <see cref="InteractiveSessionMaterializer.UpdateMetadataAsync"/> straight, with no turn lock held,
/// is what actually exercises the new guard the way #1306 PR C's per-participant world will.
/// </summary>
public class MetadataUpdateConcurrencyTests
{
    [Fact]
    public async Task Fifty_runs_of_two_concurrent_updates_each_leave_both_turns_in_room_json()
    {
        for (var run = 0; run < 50; run++)
        {
            var dir = Path.Combine(Path.GetTempPath(), "aer-metadata-mutex-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, ".aer"));

            try
            {
                var path = Path.Combine(dir, ".aer", AerPaths.RoomMetadataFileName);
                var seed = new SessionMetadata(
                    SessionId: "sess-mutex-" + run,
                    RoomDirectoryPath: dir,
                    CurrentAdapter: "claude",
                    CurrentVendorSessionId: null,
                    Model: null,
                    WorkingDirectory: null,
                    TurnCount: 0,
                    SafetyCeiling: InteractiveSessionMaterializer.DefaultSafetyCeiling,
                    CreatedAt: DateTimeOffset.UnixEpoch,
                    UpdatedAt: DateTimeOffset.UnixEpoch,
                    Turns: []);
                await InteractiveSessionMaterializer.SaveMetadataAsync(seed, path, TestContext.Current.CancellationToken);

                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                async Task<SessionMetadata> AppendAsync(string label)
                {
                    await start.Task;
                    return await InteractiveSessionMaterializer.UpdateMetadataAsync(
                        dir,
                        current =>
                        {
                            // A tiny yield inside the guarded section: without the mutex this widens
                            // the window for the second call's load to race the first call's save.
                            Thread.Sleep(1); // wait-ok: widens a deliberate race window in a concurrency test, not a poll/settle wait
                            var turn = new SessionTurn(
                                TurnIndex: current.TurnCount + 1,
                                Vendor: "claude",
                                HumanMessage: label,
                                AssistantResponse: null,
                                ExecutedAt: DateTimeOffset.UnixEpoch,
                                NativeSessionResumed: false,
                                VendorHandoffSynthesized: false);
                            return current with
                            {
                                TurnCount = current.TurnCount + 1,
                                Turns = new List<SessionTurn>(current.Turns) { turn },
                            };
                        },
                        cancellationToken: TestContext.Current.CancellationToken);
                }

                var taskA = AppendAsync("turn-a");
                var taskB = AppendAsync("turn-b");
                start.SetResult();
                await Task.WhenAll(taskA, taskB);

                var final = await InteractiveSessionMaterializer.LoadMetadataAsync(path, TestContext.Current.CancellationToken);

                Assert.NotNull(final);
                Assert.Equal(2, final.Turns.Count);
                Assert.Contains(final.Turns, t => t.HumanMessage == "turn-a");
                Assert.Contains(final.Turns, t => t.HumanMessage == "turn-b");
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(dir);
            }
        }
    }
}
