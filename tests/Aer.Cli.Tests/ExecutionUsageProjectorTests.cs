using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Cli.Tests;

/// <summary>
/// Unit coverage for <see cref="ExecutionUsageProjector.BuildByExecutionId"/> (issue #1360), isolated
/// from a full <c>RunCommand</c> dispatch: hand-assembled <see cref="LogEntry"/> lists and a
/// hand-written <c>.stdout.log</c>, so the wall-clock arithmetic and the "no terminal pair yet"
/// absence rule are each pinned directly rather than only inferred from an end-to-end room.
/// </summary>
public sealed class ExecutionUsageProjectorTests
{
    [Fact]
    public void An_execution_with_both_start_and_exit_reports_the_exact_millisecond_delta()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1");
            var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var exit = start.AddMilliseconds(2500);

            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 123), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), exit),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Equal(2500, view.WallClockMs);
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_only_a_start_event_is_entirely_absent_never_a_zero_wall_clock()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var stillRunning = new ExecutionId("exec-running");
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(stillRunning, Pid: 456), DateTime.UtcNow),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Empty(usage);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_no_captured_stdout_file_reports_wall_clock_with_no_token_fields()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-no-stdout");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            // No .stdout.log written to disk at all -- the execution's output directory never even exists.
            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            var view = Assert.Single(usage).Value;
            Assert.Equal(1000, view.WallClockMs);
            Assert.Null(view.TokensIn);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_whose_exit_timestamp_precedes_its_start_is_entirely_absent_never_a_zero_wall_clock()
    {
        // #1360 F6: a backwards clock step (NTP correction, VM resume) between the two DateTime.UtcNow
        // stamps produces a negative delta. Clamping that to 0 would print the exact "zero standing in
        // for unknown" the issue rules out; the honest response is the same as a still-running
        // execution -- absent from the result entirely.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-backwards-clock");
            var start = new DateTime(2026, 1, 1, 12, 0, 5, DateTimeKind.Utc);
            var exit = start.AddSeconds(-3);
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), exit),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Empty(usage);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Token_and_turn_counts_are_read_when_the_execution_is_attributed_to_its_dispatching_adapter()
    {
        // #1360 F1: attribution, not content-sniffing -- the claude-shaped line is only trusted
        // because the execution's own ExecutionRequestAccepted names worker role "plan", and
        // bindings.json maps "plan" to the "claude" adapter, the SAME adapter this execution actually
        // ran through.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-claude-shaped");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":4,"usage":{"input_tokens":7,"output_tokens":3}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(3000, view.WallClockMs);
            Assert.Equal(7, view.TokensIn);
            Assert.Equal(3, view.TokensOut);
            Assert.Equal(4, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_worker_echoing_a_vendor_shaped_usage_line_it_did_not_itself_produce_yields_no_token_fields()
    {
        // #1360 F1 spoof regression: a `command`-worker step's stdout is operator-supplied and can
        // contain anything, including a captured claude transcript line -- but this execution was
        // dispatched through the "command" adapter (per bindings.json), which never overrides
        // TryParseFinalUsage, so the spoofed line must never reach ClaudeWorkerAdapter's parser even
        // though ClaudeWorkerAdapter is registered in the same adapters map. Only wallClockMs may
        // survive; the fabricated 100/50/3 must not.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-spoof-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-spoofed");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", CommandWorkerAdapter.AdapterName));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(2.5)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":3,"usage":{"input_tokens":100,"output_tokens":50}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(2500, view.WallClockMs);
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void An_execution_with_no_ExecutionRequestAccepted_record_yields_no_token_fields_even_with_a_matching_bindings_entry()
    {
        // #1360 F1: attribution needs BOTH halves -- a worker-role name from the ledger AND that
        // role's adapter from bindings.json. A room with bindings.json but no accepted-request event
        // for this execution (an older ledger, or a log gap) cannot resolve the first half, so it
        // must fail closed exactly like a missing bindings file does.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-noaccept-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-no-accept");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":1,"usage":{"input_tokens":1,"output_tokens":1}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Null(view.TokensIn);
            Assert.Null(view.TokensOut);
            Assert.Null(view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker) => new(
        executionId,
        new WorkflowId("wf-usage-test"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static void WriteBindings(string roomDirectoryPath, params (string WorkerName, string Adapter)[] entries)
    {
        Directory.CreateDirectory(roomDirectoryPath);
        var config = entries.ToDictionary(
            e => e.WorkerName,
            e => new WorkerBindingConfigEntry(
                e.Adapter, new WorkerContract(e.WorkerName, [], [], []), "unused prompt", TimeSpan.FromSeconds(30)));

        File.WriteAllText(
            AerPaths.RoomBindingsFile(roomDirectoryPath),
            System.Text.Json.JsonSerializer.Serialize(config));
    }
}
