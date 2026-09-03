using System.Text.Json;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli.Tests;

/// <summary>
/// The fleet-level burn ledger (#1570) driven through the real pieces its two touch points assemble:
/// <see cref="RunCommand.ExecuteAsync"/> settling a room, and <c>Program.cs</c>'s own post-command
/// block appended verbatim here since <c>Program.Main</c> is not callable from a test (the same
/// discipline <c>ResolveCommandEndToEndTests</c> already documents at its own such call site). Proves
/// the settle-time harvest (issue's V&amp;V 2: fail-open) and <see cref="LedgerCommand"/>'s rebuild wiring
/// against real rooms rather than fabricated <see cref="LogEntry"/> lists alone
/// (<c>QuotaLedgerStoreTests</c> covers the store's own merge/concurrency contract in isolation).
/// </summary>
public sealed class QuotaLedgerSettleAndRebuildEndToEndTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose() => _batonHome.Dispose();

    [Fact]
    public async Task A_settled_room_appends_one_ledger_line_carrying_its_adapter_and_outcome()
    {
        var roomDirectory = Path.Combine(BatonPaths.Rooms, "room");
        var result = await RunSingleShellStepAsync(roomDirectory, "exit 0");
        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);

        await AppendLedgerLikeProgramDoesAsync(result, roomDirectory);

        var ledgerEntries = await QuotaLedgerStore.ReadAllAsync(BatonPaths.QuotaLedgerFile, TestContext.Current.CancellationToken);
        var entry = Assert.Single(ledgerEntries);
        Assert.Equal("shell", entry.Adapter);
        Assert.Equal("Succeeded", entry.Outcome);
        Assert.Equal(BatonPaths.RecordKey(roomDirectory), entry.Room);
        Assert.NotNull(entry.Execution);
        Assert.NotNull(entry.WallClockMs);
    }

    [Fact]
    public async Task A_failing_shell_step_records_its_FailureClassification_as_the_ledger_outcome()
    {
        var roomDirectory = Path.Combine(BatonPaths.Rooms, "room");
        var result = await RunSingleShellStepAsync(roomDirectory, "exit 1");
        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);

        await AppendLedgerLikeProgramDoesAsync(result, roomDirectory);

        var ledgerEntries = await QuotaLedgerStore.ReadAllAsync(BatonPaths.QuotaLedgerFile, TestContext.Current.CancellationToken);
        var entry = Assert.Single(ledgerEntries);
        Assert.NotNull(entry.Outcome);
        Assert.NotEqual("Succeeded", entry.Outcome);
    }

    [Fact]
    public async Task A_ledger_write_that_throws_is_swallowed_and_reported_on_stderr_never_failing_the_run()
    {
        var roomDirectory = Path.Combine(BatonPaths.Rooms, "room");
        var result = await RunSingleShellStepAsync(roomDirectory, "exit 0");
        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);

        // Forces AppendAsync's FileStream open to throw UnauthorizedAccessException -- the same
        // sanctioned-exception instrument QuotaLedgerStoreTests uses at the store level, driven here
        // through the actual settle-time call site Program.cs runs.
        Directory.CreateDirectory(BatonPaths.QuotaLedgerFile);

        var originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            await AppendLedgerLikeProgramDoesAsync(result, roomDirectory);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
        Assert.Contains("Could not append to the quota ledger", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ledger_rebuild_recovers_a_settled_execution_the_appender_never_reached()
    {
        var roomDirectory = Path.Combine(BatonPaths.Rooms, "room");
        var result = await RunSingleShellStepAsync(roomDirectory, "exit 0");
        Assert.Equal(WorkflowStatus.Terminal, result.State.Status);

        // Deliberately skip the settle-time append (simulating a lane whose process was killed after
        // Terminal but before the ledger write) so the ledger starts genuinely empty, then rebuild.
        Assert.Empty(await QuotaLedgerStore.ReadAllAsync(BatonPaths.QuotaLedgerFile, TestContext.Current.CancellationToken));

        // The room must be registered for LedgerCommand's own walk (BatonPaths.Rooms directory scan
        // alone is enough here, since RunCommand creates the room directly under it).
        using var output = new StringWriter();
        var exitCode = await LedgerCommand.RebuildAsync(output, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var recovered = await QuotaLedgerStore.ReadAllAsync(BatonPaths.QuotaLedgerFile, TestContext.Current.CancellationToken);
        var entry = Assert.Single(recovered);
        Assert.Equal("shell", entry.Adapter);
        Assert.Contains("recovers LESS than the ledger can hold", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Program.cs's own settle-time block (terminal sentinel + ledger append), verbatim -- see this
    /// project's <c>ResolveCommandEndToEndTests</c> for why the copy is unavoidable rather than a
    /// smell: <c>Program.Main</c> is not callable from a test.
    /// </summary>
    private static async Task AppendLedgerLikeProgramDoesAsync(CommandResult result, string roomDirectory)
    {
        var terminalLogPath = Path.Combine(roomDirectory, BatonPaths.FlowLogFileName);
        var terminalEntries = await new FlowEventLogReader(terminalLogPath)
            .ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);

        try
        {
            var ledgerEntries = QuotaLedgerStore.BuildEntries(terminalEntries, roomDirectory);
            await QuotaLedgerStore.AppendAsync(ledgerEntries, BatonPaths.QuotaLedgerFile, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine($"Could not append to the quota ledger at '{BatonPaths.QuotaLedgerFile}': {ex.Message}.");
        }
    }

    private static async Task<CommandResult> RunSingleShellStepAsync(string roomDirectory, string shellCommand)
    {
        var workflowFilePath = await WriteSingleStepWorkflowAsync(roomDirectory);
        var bindingsFilePath = await WriteSingleStepBindingsAsync(roomDirectory, shellCommand);
        var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

        return await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<string> WriteSingleStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        // No declared outputs: ShellCommandWorkerAdapter never captures a response, so a step
        // declaring one would settle Indeterminate on exit 0 (unsatisfied contract) rather than
        // Succeeded -- irrelevant to what this suite is proving (the ledger entry's own fields), and
        // the extra state would obscure the Succeeded/Failed outcome assertions below.
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("ledger-test"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], [], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<string> WriteSingleStepBindingsAsync(string directory, string shellCommand)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("a", [], [], []),
                PromptTemplate: shellCommand, TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), TestContext.Current.CancellationToken);
        return path;
    }
}
