using System.Diagnostics;
using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

/// <summary>
/// #1356 points 2-4 and #1374's follow-up fixes: the terminal sentinel (<c>terminal.json</c>), the
/// pre-ledger Failed state a provisioning/validation failure must leave behind (but only when the
/// room is genuinely pre-ledger), the RoomHeld exit code a concurrency refusal gets instead, and the
/// exit codes <c>Program</c> derives from all of it. The exit-code CLASSIFICATION itself is
/// unit-tested directly in <see cref="WorkflowOutcomeAndExitCodeTests"/>; this file covers the
/// wiring — the real <c>Program.cs</c> catch/success paths, which are not otherwise reachable from a
/// test (top-level statements), so the process-spawn tests below follow the same real-process
/// pattern <c>DecideCommandEndToEndTests</c> established for exactly this reason.
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
    public async Task The_real_CLI_process_writes_the_sentinel_no_earlier_than_every_output_it_declares()
    {
        // #1374 F3: the prior version of this test asserted the sentinel's absence against
        // RunCommand.ExecuteAsync called directly -- code that never writes the sentinel at all, so
        // the assertion could not fail. Program's shared post-pump step (the thing that actually
        // writes terminal.json last) only exists in the real 'aer' binary (top-level statements
        // aren't otherwise reachable from a test), so the write-last guarantee needs the real
        // process, same as the exit-code tests below. Two steps, both via the production-registered
        // NoOpWorkerAdapter (the "shell" test double used elsewhere in this file only exists in the
        // in-process Adapters dictionary above, not WorkerAdapterRegistry.Default the real binary
        // resolves against), so there are two independently-declared outputs to check ordering against.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-sentinel-order-proc-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteTwoStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteTwoStepNoOpBindingsAsync(testRoot);
            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");

            using var process = StartAerProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);

            Assert.True(File.Exists(sentinelPath));
            var view = JsonSerializer.Deserialize<WorkflowStatusView>(await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken));
            Assert.Equal("Succeeded", view!.State);
            Assert.Equal(2, view.Outputs.Count);

            // The load-bearing assertion: every output the sentinel names actually exists, and the
            // sentinel itself was written no earlier than the newest of them -- the ordering #1356
            // point 4 exists to guarantee, checked against the real write, not a hand-reproduced one.
            var sentinelWrittenAtUtc = File.GetLastWriteTimeUtc(sentinelPath);
            foreach (var outputPath in view.Outputs)
            {
                Assert.True(File.Exists(outputPath), $"Declared output '{outputPath}' must exist once the sentinel is read.");
                Assert.True(
                    File.GetLastWriteTimeUtc(outputPath) <= sentinelWrittenAtUtc,
                    $"The sentinel must be written no earlier than declared output '{outputPath}'.");
            }
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

    [Fact]
    public async Task A_second_real_CLI_run_against_an_already_completed_room_does_not_overwrite_its_sentinel()
    {
        // #1374 F1's second scenario: a room finishes, then a LATER invocation against that same
        // room fails validation (a typo'd --bindings, here). Before the fix, Program's catch wrote
        // a fresh Failed/no-outputs sentinel unconditionally, destroying the room's real terminal
        // record. The room already has a ledger (flow.jsonl from the first run), so the fix must
        // leave the sentinel untouched -- the second invocation still exits non-zero, it just must
        // not lie about what the room actually is.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-reledger-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var goodBindingsFilePath = await WriteNoOpBindingsAsync(testRoot);

            using (var firstProcess = StartAerProcess(
                "run", workflowFilePath, "--bindings", goodBindingsFilePath, "--room-dir", roomDirectory))
            {
                await firstProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, firstProcess.ExitCode);
            }

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Assert.True(File.Exists(sentinelPath));
            var originalSentinelJson = await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken);
            var originalView = JsonSerializer.Deserialize<WorkflowStatusView>(originalSentinelJson);
            Assert.Equal("Succeeded", originalView!.State);

            // Same workflow file and room (the CLI always requires the positional <workflow-file>
            // argument, even on a resume -- RunOptionsParser.Parse's own contract), a bindings file
            // naming an unregistered adapter, same fixture as the pre-ledger test above -- except
            // this room already has a ledger and a real Succeeded terminal record behind it.
            var badBindingsFilePath = await WriteUnregisteredAdapterBindingsAsync(testRoot);
            using var secondProcess = StartAerProcess(
                "run", workflowFilePath, "--bindings", badBindingsFilePath, "--room-dir", roomDirectory);
            var stderrTask = secondProcess.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await secondProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            var stderr = await stderrTask;

            Assert.Equal((int)RunExitCode.ValidationRefused, secondProcess.ExitCode);
            Assert.Contains("not-registered", stderr);

            var sentinelJsonAfterSecondRun = await File.ReadAllTextAsync(sentinelPath, TestContext.Current.CancellationToken);
            Assert.Equal(originalSentinelJson, sentinelJsonAfterSecondRun);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_real_CLI_run_against_a_room_whose_lock_is_held_exits_RoomHeld_and_writes_no_sentinel()
    {
        // #1374 F1's first scenario, the concurrency family: WorkflowLockedException/
        // FlowJournalHeldException must map to a code distinct from ValidationRefused and must never
        // write a sentinel -- the room this exception fires against may be perfectly healthy. Holding
        // ConcurrencyGuard's own lock file from this test process is the same deterministic technique
        // WorktreeProvisioningCommandTests already uses for WorkflowLockedException, chosen over a
        // real two-process timing race so this test cannot flake on scheduling.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-run-proc-locked-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteOneStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteNoOpBindingsAsync(testRoot);

            var sentinelPath = Path.Combine(roomDirectory, "terminal.json");
            Directory.CreateDirectory(roomDirectory);
            using (ConcurrencyGuard.Acquire(roomDirectory))
            {
                using var process = StartAerProcess(
                    "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
                var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                var stderr = await stderrTask;

                Assert.Equal((int)RunExitCode.RoomHeld, process.ExitCode);
                Assert.Contains("already locked", stderr);
                Assert.False(File.Exists(sentinelPath), "A room-held refusal must not fabricate a terminal sentinel.");
            }

            // Releasing the lock and running again proves the room itself was never touched by the
            // refused attempt -- it starts and completes exactly as if the first attempt never happened.
            using var retryProcess = StartAerProcess(
                "run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);
            await retryProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, retryProcess.ExitCode);
            Assert.True(File.Exists(sentinelPath));
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

    private static async Task<string> WriteTwoStepNoOpBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
            ["b"] = new WorkerBindingConfigEntry(
                NoOpWorkerAdapter.AdapterName, new WorkerContract("b", [], [new ProducedOutput("out_b")], []),
                PromptTemplate: "unused-by-noop", TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";
}
