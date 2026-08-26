using System.Diagnostics;
using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

/// <summary>
/// #1356 points 2-4: the terminal sentinel (<c>terminal.json</c>), the pre-ledger Failed state a
/// provisioning/validation failure must leave behind, and the exit codes <c>Program</c> derives from
/// both. The exit-code CLASSIFICATION itself is unit-tested directly in
/// <see cref="WorkflowOutcomeAndExitCodeTests"/>; this file covers the wiring — the real
/// <c>Program.cs</c> catch/success paths, which are not otherwise reachable from a test (top-level
/// statements), so the two process-spawn tests below follow the same real-process pattern
/// <c>DecideCommandEndToEndTests</c> established for exactly this reason.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class TerminalSentinelEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task A_room_that_fails_before_a_ledger_exists_is_left_queryable_as_Failed()
    {
        // The task's own suggested fixture -- a bindings entry naming a model the vendor would
        // reject -- turns out NOT to be a local fail-fast check for the "claude" adapter: only a
        // narrow dot-vs-dash typo (ClaudeWorkerAdapter.RefuseDotDelimitedClaudeModelId) is refused
        // before dispatch; an arbitrary unknown model string is not, since claude ships no model
        // list to validate against. An unregistered adapter name IS refused locally and offline
        // (WorkerBindingResolver.Resolve, UnknownWorkerAdapterException) -- the same case
        // RunCommandEndToEndTests already proves throws -- so that is the fixture used here.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-preledger-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var thrown = await Assert.ThrowsAsync<UnknownWorkerAdapterException>(
                () => RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken));

            // RunCommand.ExecuteAsync's own throw-on-validation-failure contract is unchanged (every
            // existing caller/test keeps working) -- Program's catch block is what records the
            // sentinel, so this reproduces exactly what that catch does.
            Assert.False(File.Exists(Path.Combine(roomDirectory, "flow.jsonl")), "A pre-ledger failure must not create a ledger.");
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                roomDirectory, thrown.Message, TestContext.Current.CancellationToken);

            using var humanOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), humanOutput, TestContext.Current.CancellationToken);
            Assert.Contains("Workflow status: Failed", humanOutput.ToString());
            Assert.Contains(thrown.Message, humanOutput.ToString());

            using var jsonOutput = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Json: true), jsonOutput, TestContext.Current.CancellationToken);
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(jsonOutput.ToString());
            Assert.NotNull(view);
            Assert.Equal("Failed", view!.State);
            Assert.Empty(view.Steps);
            Assert.Empty(view.Outputs);
            Assert.Equal(thrown.Message, view.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Retrying_a_pre_ledger_failure_with_corrected_bindings_invalidates_the_stale_sentinel()
    {
        // Without RunCommand's own stale-sentinel delete, a watcher polling for terminal.json during
        // the SECOND, genuinely-in-flight attempt would see the FIRST attempt's stale "Failed" and
        // read the retry as already done.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-preledger-retry-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var badBindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            var firstOptions = new RunOptions(workflowFilePath, badBindingsFilePath, roomDirectory);

            await Assert.ThrowsAsync<UnknownWorkerAdapterException>(
                () => RunCommand.ExecuteAsync(firstOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken));
            await TerminalSentinelWriter.WriteValidationRefusedAsync(
                roomDirectory, "first attempt failed", TestContext.Current.CancellationToken);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));

            var goodBindingsFilePath = await WriteOneStepBindingsAsync(testRoot, WriteFileCommand("plan", "the-plan"));
            var secondOptions = new RunOptions(WorkflowFilePath: null, goodBindingsFilePath, roomDirectory);
            var result = await RunCommand.ExecuteAsync(secondOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            Assert.All(result.State.Steps, s => Assert.Equal(StepStatus.Succeeded, s.Status));
            // Nothing in this test calls TerminalSentinelWriter.WriteAsync for the second attempt --
            // if the file is still here, it is necessarily the FIRST attempt's stale content.
            Assert.False(File.Exists(sentinelPath), "RunCommand must invalidate a stale sentinel before a fresh dispatch.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_sentinel_is_absent_while_a_step_output_already_exists_mid_run_and_present_once_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-sentinel-order-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoStepBindingsAsync(testRoot, SleepThenWriteCommand("out_b", seconds: 4));
            var options = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");

            var runTask = RunCommand.ExecuteAsync(options, Adapters, cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                var artifactsDir = Path.Combine(roomDirectory, "artifacts");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                var sawOutputMidRun = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (Directory.Exists(artifactsDir)
                        && Directory.GetDirectories(artifactsDir, "execution_*")
                            .Any(d => File.Exists(Path.Combine(d, "out_a"))))
                    {
                        sawOutputMidRun = true;
                        break;
                    }

                    // wait-ok: poll interval for an in-process room this test is driving; the loop's own 20s deadline is the real ceiling.
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }

                Assert.True(sawOutputMidRun, "Step 'a' never produced its output within the deadline.");
                // RunCommand itself never writes the sentinel -- only Program's shared post-pump
                // step does, and this test has not reached that step yet. Absence here is therefore
                // exactly "still running", not a race against the writer.
                Assert.False(File.Exists(sentinelPath), "The sentinel must not exist while the room is still mid-run.");
            }
            finally
            {
                var result = await runTask;

                // The same two calls Program.cs's shared post-pump step makes.
                var view = WorkflowStatusProjector.Project(result.State, result.Snapshot, roomDirectory);
                await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(sentinelPath));
            var written = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Succeeded", written!.State);
            Assert.Equal(2, written.Outputs.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_exits_2_for_a_pre_ledger_validation_failure_and_writes_the_sentinel()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-validation-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);

            using var process = StartAerProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var stderr = await stderrTask;

            Assert.Equal(2, process.ExitCode);
            Assert.Contains("not-registered", stderr);

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Failed", view!.State);
            Assert.Contains("not-registered", view.Error);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task The_real_CLI_process_exits_0_for_a_succeeded_run_and_writes_a_Succeeded_sentinel()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-ok-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            // The real WorkerAdapterRegistry.Default, not this file's test-only "shell" adapter --
            // the spawned subprocess resolves through the real registry, same as an operator's
            // actual invocation (same reasoning DecideCommandEndToEndTests' process test uses).
            var bindingsFilePath = await WriteNoOpBindingsAsync(testRoot);

            using var process = StartAerProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Succeeded", view!.State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static Process StartAerProcess(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(typeof(RunCommand).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'aer'.");
    }

    private static async Task<string> WriteOneStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-step"), 1,
            [new WorkflowStepDefinition(new StepId("solo"), "solo", [], ["plan"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneStepBindingsAsync(string directory, string command)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("solo", [], [new ProducedOutput("plan")], []), command, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteUnregisteredAdapterBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                "not-registered", new WorkerContract("solo", [], [new ProducedOutput("plan")], []),
                "irrelevant", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteNoOpBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["solo"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("solo", [], [new ProducedOutput("plan")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static async Task<string> WriteTwoStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("two-step-order"), 1,
            [
                new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteTwoStepBindingsAsync(string directory, string stepBCommand)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                WriteFileCommand("out_a", "a-done"), TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                stepBCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string SleepThenWriteCommand(string outputName, int seconds) => OperatingSystem.IsWindows()
        ? $"ping -n {seconds + 1} 127.0.0.1>nul & echo done>%AER_OUTPUT_DIR%\\{outputName}"
        : $"sleep {seconds}; echo done > \"$AER_OUTPUT_DIR/{outputName}\"";
}
