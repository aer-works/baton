using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1608 re-review finding 2, at the call site the finding is about: <c>RunCommand</c> deletes a stale
/// <c>terminal.json</c> before its pump starts, and that delete must fail CLOSED — a sentinel left in
/// place reads as "already done" to anything watching the room for the whole duration of the fresh
/// attempt. The first response to review finding 8 made the shared helper best-effort for every caller,
/// which silently turned this site fail-open; this pins the refusal, and that it is a typed
/// <see cref="StaleSentinelDeletionException"/> (so <c>Program.cs</c> prints a refusal rather than a
/// stack trace) rather than a bare <see cref="IOException"/>.
/// </summary>
public class RunCommandStaleSentinelTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task A_stale_sentinel_that_cannot_be_deleted_refuses_the_run_instead_of_pumping_behind_it()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-sentinel-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var options = await SeedRoomWithStaleSentinelAsync(testRoot, roomDirectory);
            var sentinelPath = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);

            using (new FileStream(sentinelPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = await Assert.ThrowsAsync<StaleSentinelDeletionException>(
                    () => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));
                Assert.Contains(sentinelPath, ex.Message, StringComparison.Ordinal);
            }

            // Nothing was dispatched behind the false signal: no ledger, and the stale sentinel is
            // still the room's only terminal record.
            Assert.False(
                File.Exists(Path.Combine(roomDirectory, "flow.jsonl")),
                "the pump must not have started behind an un-invalidated 'already done' signal.");
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_same_stale_sentinel_is_deleted_and_the_run_proceeds_when_nothing_holds_it()
    {
        // The control arm: one condition apart (no open handle), proving the refusal above is about the
        // lock rather than about the sentinel merely being present.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-sentinel-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var options = await SeedRoomWithStaleSentinelAsync(testRoot, roomDirectory);
            var sentinelPath = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);

            var result = await RunCommand.ExecuteAsync(
                options, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.True(
                File.Exists(Path.Combine(roomDirectory, "flow.jsonl")),
                "the run must have proceeded once the sentinel could be removed.");
            // RunCommand itself never rewrites the sentinel (Program.cs does, after the pump returns),
            // so its absence here is the delete having happened.
            Assert.False(File.Exists(sentinelPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// A room that is NOT terminal by its own ledger (it has none yet) but carries a sentinel from a
    /// prior pre-ledger failure — the exact shape <c>RunCommand</c>'s delete exists for (#1356 point 3).
    /// </summary>
    private static async Task<RunOptions> SeedRoomWithStaleSentinelAsync(string testRoot, string roomDirectory)
    {
        Directory.CreateDirectory(testRoot);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("run-sentinel-test"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["a.md"], [], new RetryPolicy(1))]);
        var workflowFilePath = Path.Combine(testRoot, "workflow.json");
        await File.WriteAllTextAsync(workflowFilePath, JsonSerializer.Serialize(definition));

        const string writeCommand = "echo done>%BATON_OUTPUT_DIR%\\a.md";
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("a.md")], []),
                PromptTemplate: writeCommand, TimeSpan.FromSeconds(30)),
        };
        var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
        await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config));

        Directory.CreateDirectory(roomDirectory);
        await TerminalSentinelWriter.WriteValidationRefusedAsync(
            roomDirectory, "a prior pre-ledger refusal", TestContext.Current.CancellationToken);

        return new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
    }
}
