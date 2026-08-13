using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Tests.TestSupport;

namespace Aer.Ui.Tests;

/// <summary>
/// #1142 review (high): the projector computed <c>PermissionAnswers</c> correctly and every test
/// injected it by hand, while <see cref="RoomProjectionLoader.LoadAsync"/> — the daemon's OWN load
/// path for every live request and broadcast — silently dropped it, so no real client ever saw the
/// feature. This drives the loader against a real room directory with a real <c>room.jsonl</c>,
/// the seam every hand-built-projection test skips.
/// </summary>
public class RoomProjectionLoaderPermissionTests
{
    [Fact]
    public async Task LoadAsync_carries_the_answer_history_from_room_jsonl()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-loader-perm-{Guid.NewGuid():N}");
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
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
                    new WorkflowId("wf-loader-perm"),
                    roomDirectory,
                    snapshot,
                    bindings,
                    Path.Combine(roomDirectory, "artifacts"),
                    reader,
                    writer,
                    dispatcher,
                    cancellationToken: TestContext.Current.CancellationToken);
            }

            var asked = new DateTimeOffset(2026, 8, 13, 4, 0, 0, TimeSpan.Zero);
            await using (var roomWriter = new RoomEventLogWriter(Path.Combine(roomDirectory, "room.jsonl")))
            {
                await roomWriter.AppendAsync(
                    new RoomEvent.RuntimePermissionAsked(
                        "perm-1", new ExecutionId("exec-1"), new StepId("architect"), "architect", "claude",
                        "corr-1", "Bash", "{}", "Shell", asked),
                    TestContext.Current.CancellationToken);
                await roomWriter.AppendAsync(
                    new RoomEvent.RuntimePermissionAnswered("perm-1", "AllowOnce", null, null, "operator", asked.AddSeconds(20)),
                    TestContext.Current.CancellationToken);
            }

            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var answer = Assert.Single(projection.PermissionAnswers);
            Assert.Equal("Bash", answer.ToolName);
            Assert.Equal("AllowOnce", answer.DecisionKind);
            Assert.False(answer.WasRevoked);
            Assert.Null(projection.PendingPermission);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
