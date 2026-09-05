using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495's money test: a live <c>baton run</c> pump — a real, separate OS process holding the room's
/// real <c>flow.lock</c> (<see cref="Baton.Concurrency.ConcurrencyGuard"/>) — is reached from a second,
/// independent <c>baton cancel</c> process with no <c>--execution</c>. That process cannot win the
/// guard, catches <see cref="Baton.Concurrency.WorkflowLockedException"/>, and writes
/// <see cref="CancelRequestFile"/> instead; the live pump's own <see cref="CancelRequestPoller"/> picks
/// it up, delivers the cancellation to the in-flight execution, and the workflow settles
/// <see cref="WorkflowStatus.Terminal"/> — proving the whole chain (a)-(d): the retained registry, the
/// out-of-band channel, room-level targeting through a real lock contention, and the real
/// <c>Program.cs</c> exit path writing <c>terminal.json</c> for a cancellation-settled run exactly as
/// it does for a natural completion, which is what lets a follow-up <c>redispatch</c> proceed past its
/// terminal-sentinel guard.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class LiveCancelRequestChannelEndToEndTests : IDisposable
{
    private readonly IsolatedBatonHome _batonHome = new();

    public void Dispose()
    {
        _batonHome.Dispose();
        GC.SuppressFinalize(this);
    }

    // The real production registry, not a test double (Program.cs's own Main uses this exact
    // dictionary) -- the `run`/`cancel` halves below are real subprocesses of the real binary, so
    // only an adapter actually registered there (CommandWorkerAdapter's "command", here) resolves.
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters = WorkerAdapterRegistry.Default;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Baton_cancel_against_a_live_pump_arrests_it_to_Terminal_and_redispatch_is_then_accepted()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-live-cancel-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        Process? runProcess = null;
        try
        {
            // Written directly under roomDirectory, not testRoot: RedispatchCommand's no-spec path
            // reads workflow.json/bindings.json from the PARENT room directory itself
            // (BatonPaths.RoomBindingsFile / RedispatchCommand.WorkflowFileName) -- `baton dispatch`
            // writes them there, but `baton run` (used directly here, not through dispatch) does not,
            // so this test must put them there itself for the redispatch step below to find them.
            var workflowFilePath = await WriteOneSleepingStepWorkflowAsync(roomDirectory);
            var bindingsFilePath = await WriteOneSleepingStepBindingsAsync(roomDirectory);
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");

            runProcess = StartBatonProcess("run", workflowFilePath, "--bindings", bindingsFilePath, "--room-dir", roomDirectory);

            await WaitForConditionAsync(
                () => ReadCoreEventsSafely(logPath).Any(e => e is CoreEvent.ExecutionStarted),
                WaitTimeout,
                "the sleeping step to start");

            using var cancelProcess = StartBatonProcess("cancel", roomDirectory, "--bindings", bindingsFilePath);
            var (cancelStdout, cancelStderr) = await BoundedProcessWait.RunToExitAsync(
                cancelProcess, WaitTimeout, TestContext.Current.CancellationToken);
            Assert.True(
                cancelStdout.Contains("live pump", StringComparison.OrdinalIgnoreCase),
                $"expected the fall-through message on stdout; got stdout='{cancelStdout}' stderr='{cancelStderr}'");

            using var runExitTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            runExitTimeout.CancelAfter(WaitTimeout);
            await runProcess.WaitForExitAsync(runExitTimeout.Token);

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.CancellationRequested);
            Assert.Contains(events, e => e is FlowEvent.ExecutionCancelled);

            var snapshot = await SnapshotBinder.LoadFromFileAsync(
                Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);
            var state = StateProjector.Project(events, snapshot);
            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.Equal(StepStatus.Cancelled, state.Steps.Single().Status);

            var requestFilePath = Path.Combine(roomDirectory, CancelRequestFile.FileName);
            Assert.True(
                File.Exists($"{requestFilePath}.consumed") && !File.Exists(requestFilePath),
                "expected the pump to consume (rename) the request file once it acted on it");

            var terminalSentinelPath = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
            Assert.True(
                File.Exists(terminalSentinelPath),
                "expected the real Program.cs exit path to write terminal.json for the cancellation-settled run");

            var childRoom = Path.Combine(testRoot, "child");
            var redispatchOptions = new RedispatchOptions(roomDirectory, childRoom);
            var redispatchResult = await RedispatchCommand.ExecuteAsync(
                redispatchOptions, Adapters, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, redispatchResult.State.Status);
        }
        finally
        {
            runProcess?.Dispose();
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static IReadOnlyList<CoreEvent> ReadCoreEventsSafely(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return [];
        }

        try
        {
            return new FlowEventLogReader(logPath).ReadAllCoreEventsAsync().GetAwaiter().GetResult();
        }
        catch (FlowEventLogReadException)
        {
            // A torn tail mid-write by the live pump; the next poll observes the completed line.
            return [];
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken); // wait-ok: polling interval inside WaitForConditionAsync
        }

        Assert.Fail($"Timed out after {timeout} waiting for {description}.");
    }

    private static Process StartBatonProcess(params string[] args)
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

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'baton'.");
    }

    private static async Task<string> WriteOneSleepingStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("one-sleeping-step"), 1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteOneSleepingStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        // ~5s: long enough to still be Running by the time the cancel process runs and the pump's own
        // poller (2s cadence) next ticks, short enough to keep this test's own wall-clock small. A
        // portable Windows-sleep idiom (`ping`, since `timeout` needs a console and fails headless);
        // the redispatch assertion below reruns this exact command uncancelled, so it must actually
        // finish quickly. CommandWorkerAdapter (production's "command" adapter) takes argv directly,
        // no shell -- Architecture Rule 1.
        var argv = new[] { "ping", "-n", "6", "127.0.0.1" };
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["a"] = new WorkerBindingConfigEntry(
                "command",
                new WorkerContract("a", [], [new ProducedOutput("out")], []),
                JsonSerializer.Serialize(argv),
                TimeSpan.FromSeconds(30)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }
}
