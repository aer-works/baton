using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;

namespace Baton.Cli.Tests;

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
        // #1360 F6: pins the clamp-avoidance ExecutionUsageProjector.BuildByExecutionId documents --
        // see its own remarks for why. Here: a backwards clock step (NTP correction, VM resume)
        // between the two DateTime.UtcNow stamps produces a negative delta, and the result must treat
        // it the same as a still-running execution -- absent entirely, not a printed 0.
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
    public void Token_counts_are_still_read_after_a_retention_sweep_moves_stdout_log_to_the_pruned_path()
    {
        // #1360 F7: pins the fallback ExecutionUsageProjector.TryReadWorkerUsage documents -- see
        // that method's own remarks for why the fallback exists. Here: only the pruned path has
        // .stdout.log, so the live-path lookup must fail through to it rather than giving up.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-pruned-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-pruned");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(4)),
            };

            // No execution_<id> directory at all -- only its pruned counterpart exists, as after a sweep.
            var prunedDir = ArtifactManager.ResolvePrunedOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(prunedDir);
            File.WriteAllText(
                Path.Combine(prunedDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":6,"usage":{"input_tokens":12,"output_tokens":9}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(4000, view.WallClockMs);
            Assert.Equal(12, view.TokensIn);
            Assert.Equal(9, view.TokensOut);
            Assert.Equal(6, view.Turns);
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
    public void A_recorded_adapter_wins_over_bindings_json_even_after_failover_edits_the_file()
    {
        // Issue #1567 (quota-design S1), the keystone defect: ExecutionUsageProjector used to recover
        // the vendor by reading bindings.json's CURRENT Adapter at read time. Failover editing that
        // file after this execution completed used to retroactively re-attribute it to the new
        // vendor -- silently, with plausible output. This execution's own ExecutionRequestAccepted
        // now records "agy" as the adapter it actually ran through; bindings.json has since been
        // rebound "plan" to "claude". The recorded value must win.
        //
        // The two vendors' terminal-usage envelopes are shaped differently (AgyUsageParser needs
        // "event":"result" wrapping a nested "result" object; ClaudeUsageParser needs a flat
        // "type":"result"), so picking the wrong parser for a captured agy line doesn't produce a
        // plausible wrong number here -- it fails to parse at all, silently losing real token/turn
        // data for an execution that genuinely has it. That silent loss is exactly what this test
        // pins: this fails against current main, where bindings.json's post-failover "claude" wins
        // and ClaudeUsageParser can't read the agy-shaped line.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-failover-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-failed-over");
            var start = DateTime.UtcNow;
            WriteBindings(testRoot, ("plan", "claude"));
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId, "plan", adapter: "agy"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"event":"result","result":{"num_turns":5,"usage":{"input_tokens":21,"output_tokens":13}}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            var view = Assert.Single(usage).Value;
            Assert.Equal(21, view.TokensIn);
            Assert.Equal(13, view.TokensOut);
            Assert.Equal(5, view.Turns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void Recorded_adapter_absent_falls_back_to_bindings_json_recorded_adapter_present_ignores_it()
    {
        // Polarity, both directions, made observable via CommandWorkerAdapter (#1360 F1's spoof
        // regression above already proves a mismatched adapter parses nothing): bindings.json names
        // "command" for this worker, which never overrides TryParseFinalUsage, so a claude-shaped
        // usage line yields no token fields when attribution falls through to bindings.json -- but
        // yields real fields when the recorded Adapter overrides bindings.json with "claude" instead.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-polarity-{Guid.NewGuid():N}");
        try
        {
            WriteBindings(testRoot, ("plan", CommandWorkerAdapter.AdapterName));

            var noRecordedAdapter = new ExecutionId("exec-no-recorded-adapter");
            var recordedAdapterPresent = new ExecutionId("exec-recorded-adapter-present");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(noRecordedAdapter, "plan", adapter: null))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(noRecordedAdapter, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(noRecordedAdapter, 0, CoreExitReason.Natural), start.AddSeconds(1)),

                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(recordedAdapterPresent, "plan", adapter: "claude"))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(recordedAdapterPresent, Pid: 2), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(recordedAdapterPresent, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            foreach (var id in new[] { noRecordedAdapter, recordedAdapterPresent })
            {
                var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, id);
                Directory.CreateDirectory(outputDir);
                File.WriteAllText(
                    Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                    """{"type":"result","num_turns":2,"usage":{"input_tokens":8,"output_tokens":4}}""" + "\n");
            }

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default, testRoot);

            Assert.Null(usage[noRecordedAdapter.Value].TokensIn);
            Assert.Equal(8, usage[recordedAdapterPresent.Value].TokensIn);
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

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker, string? adapter = null) => new(
        executionId,
        new WorkflowId("wf-usage-test"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: adapter);

    private static void WriteBindings(string roomDirectoryPath, params (string WorkerName, string Adapter)[] entries)
    {
        Directory.CreateDirectory(roomDirectoryPath);
        var config = entries.ToDictionary(
            e => e.WorkerName,
            e => new WorkerBindingConfigEntry(
                e.Adapter, new WorkerContract(e.WorkerName, [], [], []), "unused prompt", TimeSpan.FromSeconds(30)));

        File.WriteAllText(
            BatonPaths.RoomBindingsFile(roomDirectoryPath),
            System.Text.Json.JsonSerializer.Serialize(config));
    }
}
