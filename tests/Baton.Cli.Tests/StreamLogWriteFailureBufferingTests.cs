using System.Text;
using Baton.Artifacts;
using Baton.Cli.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1876. Pins THE INVARIANT, which is stated once on <see cref="ExecutionStreamLogger"/>'s private
/// append path and not restated here — read it there before reading these fixtures, since every arm
/// below is an attempt to break it. Before this, a write that failed skipped its chunk and said
/// "Continuing to retry on subsequent chunks" — which retried the SINK, never the chunk, so the
/// promise in the warning was not the behaviour and the resulting hole was silent.
/// <para>
/// The control arm throughout is <c>maxPendingBytes: 0</c>, which reproduces the pre-fix drop exactly.
/// It is read first in the first test: if the control did not show the gap, these fixtures would be
/// proving something about the fake appender rather than about the logger.
/// </para>
/// </summary>
public sealed class StreamLogWriteFailureBufferingTests
{
    /// <summary>
    /// The Windows shape the issue reported — <c>UnauthorizedAccessException("Access to the path is
    /// denied")</c> on an append — made deterministic. Non-failing calls do the real append, so the
    /// assertions below read actual file bytes rather than a recording of intended writes.
    /// </summary>
    private sealed class FlakyAppender(int failuresToInject)
    {
        private int _remaining = failuresToInject;

        public int Attempts { get; private set; }

        public void Heal() => _remaining = 0;

        public void Append(string path, byte[] data)
        {
            Attempts++;
            if (_remaining > 0)
            {
                _remaining--;
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(data, 0, data.Length);
            fs.Flush();
        }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string StdoutText(string dir) =>
        File.ReadAllText(Path.Combine(dir, ExecutionStreamLogger.StdoutLogFileName));

    private static bool WriteFailureMarked(string dir) =>
        File.Exists(Path.Combine(dir, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName));

    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stream-1876-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void One_transient_failure_leaves_no_gap_where_dropping_the_chunk_leaves_one()
    {
        var control = NewDir("control");
        var fixed_ = NewDir("buffered");
        try
        {
            // CONTROL, read first: maxPendingBytes 0 is the pre-#1876 behaviour. If this arm did not
            // lose "a\n", the fixture's injected failure never reached the logger and the arm below
            // would prove nothing.
            var controlAppender = new FlakyAppender(failuresToInject: 1);
            var controlLogger = new ExecutionStreamLogger(control, maxPendingBytes: 0, appendBytes: controlAppender.Append);
            controlLogger.AppendStdout(Bytes("a\n"));
            controlLogger.AppendStdout(Bytes("b\n"));
            controlLogger.AppendStdout(Bytes("c\n"));
            controlLogger.MarkTerminal();

            Assert.Equal("b\nc\n", StdoutText(control));
            Assert.True(WriteFailureMarked(control), "a dropped chunk is a gap and must be announced");

            // The fix: the same failure, the same chunks, nothing lost and nothing to announce.
            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(fixed_, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("a\n"));
            logger.AppendStdout(Bytes("b\n"));
            logger.AppendStdout(Bytes("c\n"));
            logger.MarkTerminal();

            Assert.Equal("a\nb\nc\n", StdoutText(fixed_));
            Assert.False(WriteFailureMarked(fixed_), "nothing was lost, so nothing may be marked lost");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(control);
            DirectoryCleanup.DeleteRecursively(fixed_);
        }
    }

    [Fact]
    public void Two_consecutive_failures_are_both_recovered_in_arrival_order()
    {
        var dir = NewDir("twice");
        try
        {
            var appender = new FlakyAppender(failuresToInject: 2);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("a\n"));
            logger.AppendStdout(Bytes("b\n"));
            logger.AppendStdout(Bytes("c\n"));
            logger.MarkTerminal();

            // Order is the queue's order, not the order the writes happened to succeed in.
            Assert.Equal("a\nb\nc\n", StdoutText(dir));
            Assert.False(WriteFailureMarked(dir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_recovered_chunk_is_written_exactly_once()
    {
        var dir = NewDir("no-dup");
        try
        {
            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("x\n"));
            logger.AppendStdout(Bytes("x\n"));
            logger.MarkTerminal();

            // Two identical chunks, one failure: exactly two copies on disk. A retry that re-sent a
            // chunk the sink had actually accepted would show three.
            Assert.Equal("x\nx\n", StdoutText(dir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void The_truncation_marker_appears_only_once_the_buffer_bound_is_passed()
    {
        var dir = NewDir("bound");
        try
        {
            // Permanent failure, a 8-byte bound, 4-byte chunks: the bound is a byte count, so the
            // marker's arrival is a function of queued VOLUME rather than of failure count.
            var appender = new FlakyAppender(failuresToInject: int.MaxValue);
            var logger = new ExecutionStreamLogger(dir, maxPendingBytes: 8, appendBytes: appender.Append);

            logger.AppendStdout(Bytes("aaa\n"));
            Assert.False(WriteFailureMarked(dir), "4 queued bytes is inside the bound");

            logger.AppendStdout(Bytes("bbb\n"));
            Assert.False(WriteFailureMarked(dir), "8 queued bytes is still inside the bound");

            logger.AppendStdout(Bytes("ccc\n"));
            Assert.True(WriteFailureMarked(dir), "past the bound the bytes are surrendered, and said so");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_chunk_still_queued_at_terminal_is_flushed_before_the_stream_closes()
    {
        var dir = NewDir("terminal-flush");
        try
        {
            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);

            // The LAST chunk fails, and no further chunk is coming -- the vendor's terminal usage line
            // is exactly this shape. MarkTerminal is its only remaining chance.
            logger.AppendStdout(Bytes("final\n"));
            Assert.Equal(string.Empty, StdoutText(dir));

            appender.Heal();
            logger.MarkTerminal();

            Assert.Equal("final\n", StdoutText(dir));
            Assert.False(WriteFailureMarked(dir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_permanently_broken_sink_is_declared_lost_at_terminal_rather_than_left_looking_clean()
    {
        var dir = NewDir("permanent");
        try
        {
            var appender = new FlakyAppender(failuresToInject: int.MaxValue);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("lost\n"));
            Assert.False(WriteFailureMarked(dir), "still buffered, still inside the bound -- not lost yet");

            logger.MarkTerminal();

            Assert.Equal(string.Empty, StdoutText(dir));
            Assert.True(WriteFailureMarked(dir), "terminal is the last retry; what is still queued is now a gap");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_buffered_chunk_survives_the_caller_reusing_its_array()
    {
        var dir = NewDir("aliasing");
        try
        {
            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);

            var buffer = Bytes("aa\n");
            logger.AppendStdout(buffer);

            // CoreDispatcher hands out the array its reader filled; nothing promises it will not fill
            // it again. Holding the caller's reference rather than a copy would write "zz\n" here.
            Bytes("zz\n").CopyTo(buffer, 0);

            logger.AppendStdout(Bytes("b\n"));
            logger.MarkTerminal();

            Assert.Equal("aa\nb\n", StdoutText(dir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void Stdout_and_stderr_buffer_independently()
    {
        var dir = NewDir("split");
        try
        {
            // The bound and the loss declaration are per-stream: a stderr sink that never recovers must
            // not cost stdout -- the reconcilable stream -- its own bytes. Same cross-stream coupling
            // #1525 F4 removed one layer down.
            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);

            logger.AppendStderr(Bytes("e1\n")); // consumes the single failure
            logger.AppendStdout(Bytes("o1\n"));
            logger.AppendStderr(Bytes("e2\n"));
            logger.MarkTerminal();

            Assert.Equal("o1\n", StdoutText(dir));
            Assert.Equal("e1\ne2\n", File.ReadAllText(Path.Combine(dir, ExecutionStreamLogger.StderrLogFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void Token_dimensions_survive_a_failure_on_the_chunk_carrying_the_terminal_usage_line()
    {
        // The end-to-end claim the issue is actually about: a transient write failure on the chunk that
        // happens to carry the vendor's terminal usage record must not cost the attempt its token
        // reconciliation. Written through the real logger, read through the real projector.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1876-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1876");
            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);

            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(outputDir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes(ClaudeAssistantLine + "\n"));
            logger.AppendStdout(Bytes(ClaudeTerminalLine + "\n"));
            logger.MarkTerminal();

            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var view = Assert.Single(
                ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default)).Value;

            Assert.Equal(1000, view.TokensIn);
            Assert.Equal(500, view.TokensOut);
            Assert.Equal(5500, view.BilledTokens);
            Assert.Equal(700, view.LiveBilledTokens);
            Assert.Null(view.BilledReconciliationUnavailable);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_permanently_broken_sink_still_reports_the_in_memory_token_totals_with_the_reason_set()
    {
        // The half of #1876 the buffer cannot save: the sink never recovers, the bytes are gone, and the
        // stream is honestly declared incomplete. The token counts must survive anyway, because the live
        // monitor already observed them in memory and the arrest event already journals them -- they
        // never went near the disk, so a disk problem has no business erasing them. Before #1876 nothing
        // read that event's `Usage` and the attempt reported wall-clock alone.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1876-arrest-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1876-arrest");
            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);

            var appender = new FlakyAppender(failuresToInject: int.MaxValue);
            var logger = new ExecutionStreamLogger(outputDir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes(ClaudeTerminalLine + "\n"));
            logger.MarkTerminal();
            Assert.True(WriteFailureMarked(outputDir));

            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, -1, CoreExitReason.CancelRequested), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionArrested(
                    executionId,
                    Usage: new WorkerUsage(TokensIn: 11, TokensOut: 22, Turns: 3, CacheReadTokens: 44, CacheCreationTokens: 55, ThinkingTokens: 66),
                    Reason: ArrestReason.ToolStepCap,
                    ToolStepCount: 41,
                    PeakBilledInWindow: 88)),
            };

            var view = Assert.Single(
                ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default)).Value;

            Assert.Equal(11, view.TokensIn);
            Assert.Equal(22, view.TokensOut);
            Assert.Equal(3, view.Turns);
            Assert.Equal(44, view.CacheReadTokens);
            Assert.Equal(55, view.CacheCreationTokens);
            Assert.Equal(66, view.ThinkingTokens);
            Assert.Equal(88, view.PeakBilledInWindow);

            // Loud, not zero-filled: the stream is still declared incomplete...
            Assert.Equal("stream-truncated-by-write-failure", view.BilledReconciliationUnavailable);
            // ...and the fallback stops at the dimensions: no member of the reconciliation triple is
            // synthesised from the journalled figure. Why that line is drawn there: spec/baton.md §3.
            Assert.Null(view.BilledTokens);
            Assert.Null(view.LiveBilledTokens);
            Assert.Null(view.BilledUnderReadTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void CONTROL_a_readable_stream_prefers_its_own_terminal_line_over_the_journalled_arrest_usage()
    {
        // The polarity arm, and the one that keeps the fallback a fallback. Same journalled arrest
        // usage, but the capture is intact: every dimension must come from the terminal LINE, whose
        // figures differ from the journalled ones on purpose. Without this, a fallback that always won
        // would pass the test above and silently downgrade every reconcilable room to a live floor.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1876-ctl-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1876-ctl");
            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            File.WriteAllLines(
                Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName),
                [ClaudeAssistantLine, ClaudeTerminalLine]);

            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, -1, CoreExitReason.CancelRequested), start.AddSeconds(1)),
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionArrested(
                    executionId,
                    Usage: new WorkerUsage(TokensIn: 11, TokensOut: 22, Turns: 3),
                    Reason: ArrestReason.ToolStepCap,
                    ToolStepCount: 41)),
            };

            var view = Assert.Single(
                ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default)).Value;

            Assert.Equal(1000, view.TokensIn);
            Assert.Equal(500, view.TokensOut);
            Assert.Equal(5500, view.BilledTokens);
            Assert.Equal(700, view.LiveBilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static ExecutionRequest AcceptedRequest(ExecutionId executionId) => new(
        executionId,
        new WorkflowId("wf-1876"),
        new StepId("plan"),
        "plan",
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: "claude");

    private const string ClaudeAssistantLine =
        """{"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":2,"cache_creation_input_tokens":700,"cache_read_input_tokens":0,"output_tokens":3}}}""";

    private const string ClaudeTerminalLine =
        """{"type":"result","num_turns":2,"usage":{"input_tokens":1,"output_tokens":2,"cache_creation_input_tokens":3},"modelUsage":{"claude-opus-5":{"inputTokens":1000,"outputTokens":500,"cacheReadInputTokens":9000,"cacheCreationInputTokens":4000}}}""";
}
