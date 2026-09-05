using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495: <c>baton cancel</c> against a room whose <c>flow.lock</c> is held — genuinely live pump or
/// not, the guard cannot tell the difference, which is exactly what
/// <see cref="Baton.Concurrency.WorkflowLockedException"/>'s own message already says — must not
/// throw that exception itself. It catches it and falls through to <see cref="CancelRequestFile"/>
/// instead. Holds the lock directly via <see cref="ConcurrencyGuard.Acquire"/> rather than racing a
/// real live pump's own <see cref="CancelRequestPoller"/>, so this test is deterministic and isolates
/// exactly the catch/fall-through this file is named for — the real cross-process, live-pump case is
/// <see cref="LiveCancelRequestChannelEndToEndTests"/>'s money test.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class CancelCommandWorkflowLockedFallThroughTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Cancelling_a_room_whose_flow_lock_is_held_writes_the_request_file_instead_of_throwing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneQuickStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteOneQuickStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            // A real, already-settled room -- snapshot.json and flow.jsonl must already exist for
            // CancelCommand to even get past its own snapshot-load check, same as every other
            // CancelCommand test in this project.
            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            using (ConcurrencyGuard.Acquire(roomDirectory, "test holder simulating a live pump"))
            {
                var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: "whatever-exec-id", bindingsFilePath);
                var result = await CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken);

                // Still reads as whatever the last real MutationInterface call left it (Terminal here)
                // -- CancelCommand never re-pumps on the fall-through path, it only reprojects.
                Assert.Equal(WorkflowStatus.Terminal, result.State.Status);

                var requestFilePath = Path.Combine(roomDirectory, CancelRequestFile.FileName);
                Assert.True(File.Exists(requestFilePath), "expected cancel.request to have been written");
                var content = await CancelRequestFile.TryReadAsync(requestFilePath, TestContext.Current.CancellationToken);
                Assert.NotNull(content);
                Assert.Equal("whatever-exec-id", content.Target);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteOneQuickStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-quick-step-fallthrough"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneQuickStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        const string writeCommand = "echo done>%BATON_OUTPUT_DIR%\\out";
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out")], []), writeCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
