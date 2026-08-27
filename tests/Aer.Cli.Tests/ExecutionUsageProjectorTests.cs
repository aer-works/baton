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
    public void Token_and_turn_counts_are_read_via_content_sniffing_across_every_registered_adapter()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-projector-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-claude-shaped");
            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(3)),
            };

            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                """{"type":"result","num_turns":4,"usage":{"input_tokens":7,"output_tokens":3}}""" + "\n");

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

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
}
