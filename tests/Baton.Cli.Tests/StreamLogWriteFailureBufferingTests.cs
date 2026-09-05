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
[Collection(ConsoleErrorCaptureCollection.Name)]
public sealed class StreamLogWriteFailureBufferingTests
{
    /// <summary>
    /// The Windows shape the issue reported — <c>UnauthorizedAccessException("Access to the path is
    /// denied")</c> on an append, with nothing written — made deterministic. Non-failing calls do the
    /// real append, so the assertions below read actual file bytes rather than a recording of intended
    /// writes. <paramref name="failWhen"/> (#1879 review MEDIUM) picks WHICH chunk fails rather than
    /// only how many do: an arm about the terminal usage record has to fail the chunk carrying it, and
    /// a count alone silently consumed the failure on whatever chunk came first. It takes the path too,
    /// so an arm can fail one stream and leave the other healthy.
    /// </summary>
    private sealed class FlakyAppender(int failuresToInject, Func<string, byte[], bool>? failWhen = null)
    {
        private int _remaining = failuresToInject;

        public int Attempts { get; private set; }

        public void Heal() => _remaining = 0;

        public void Append(string path, byte[] data)
        {
            Attempts++;
            if (_remaining > 0 && (failWhen is null || failWhen(path, data)))
            {
                _remaining--;
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(data, 0, data.Length);
            fs.Flush();
        }
    }

    /// <summary>
    /// #1879 review HIGH 1: the failure shape the fixtures above CANNOT produce — a write that throws
    /// after some (or all) of its bytes have already reached the file. <see cref="FlakyAppender"/>
    /// throws at method entry, so every "written exactly once" assertion in this file was, before this,
    /// a statement about a sink that never touched the disk. Real sinks are not that tidy: a
    /// <c>FileStream.Write</c>/<c>Flush</c> pair can persist a prefix and then fail (ENOSPC, a removed
    /// device, a dropped network path).
    /// </summary>
    private sealed class PartialWriteAppender(int failuresToInject, int bytesToWriteBeforeThrowing = 1)
    {
        private int _remaining = failuresToInject;

        public void Append(string path, byte[] data)
        {
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var count = _remaining > 0 ? Math.Min(bytesToWriteBeforeThrowing, data.Length) : data.Length;
                fs.Write(data, 0, count);
                fs.Flush();
            }

            if (_remaining > 0)
            {
                _remaining--;
                throw new IOException($"There is not enough space on the disk. ('{path}')");
            }
        }
    }

    /// <summary>
    /// #1879 review HIGH 1, the other polarity: a partial write whose ROLLBACK is also refused. The
    /// sink keeps its own exclusive handle open, so the logger can still stat the file (metadata reads
    /// survive an exclusive lock) but cannot open it for write to cut it back — the on-disk tail is
    /// then of unknown shape, and the only honest move is to surrender the chunk as a declared loss
    /// rather than duplicate it into the stream.
    /// </summary>
    private sealed class ExclusivelyLockedPartialAppender : IDisposable
    {
        private FileStream? _held;

        public void Append(string path, byte[] data)
        {
            if (_held is not null)
            {
                // The lock is only taken for the first attempt; a later retry writes normally.
                _held.Dispose();
                _held = null;
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(data, 0, data.Length);
                fs.Flush();
                return;
            }

            _held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            _held.Write(data, 0, 1);
            _held.Flush(flushToDisk: true);
            throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
        }

        public void Dispose()
        {
            _held?.Dispose();
            _held = null;
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
    public void A_write_that_throws_after_persisting_a_prefix_is_rolled_back_before_the_retry()
    {
        var dir = NewDir("partial");
        try
        {
            // #1879 review HIGH 1. The sink persists "aa" of "aaaa\n" and then throws. Without the
            // rollback the retry appends the whole chunk on top of that prefix and the file reads
            // "aaaaaa\nb\n" -- a duplicated prefix, a malformed first line, and no marker to announce
            // either, since the retry ultimately "succeeded". The assertion is the polarity: exactly
            // the bytes the worker emitted, once.
            var appender = new PartialWriteAppender(failuresToInject: 1, bytesToWriteBeforeThrowing: 2);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("aaaa\n"));
            logger.AppendStdout(Bytes("b\n"));
            logger.MarkTerminal();

            Assert.Equal("aaaa\nb\n", StdoutText(dir));
            Assert.All(File.ReadAllLines(Path.Combine(dir, ExecutionStreamLogger.StdoutLogFileName)),
                line => Assert.Contains(line, new[] { "aaaa", "b" }));
            Assert.False(WriteFailureMarked(dir), "the rollback made the retry clean, so nothing was lost");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_write_that_throws_after_persisting_every_byte_is_not_written_twice()
    {
        var dir = NewDir("all-then-throw");
        try
        {
            // The ambiguous case the pre-#1879 catch could not tell from "nothing landed": all the
            // bytes reached the file and the failure came after. Rolled back to the pre-append length,
            // so the retry produces one copy rather than two.
            var appender = new PartialWriteAppender(failuresToInject: 1, bytesToWriteBeforeThrowing: int.MaxValue);
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("only-once\n"));
            logger.MarkTerminal();

            Assert.Equal("only-once\n", StdoutText(dir));
            Assert.False(WriteFailureMarked(dir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_partial_write_that_cannot_be_rolled_back_is_declared_lost_rather_than_duplicated()
    {
        var dir = NewDir("no-rollback");
        using var appender = new ExclusivelyLockedPartialAppender();
        try
        {
            // The polarity of the two arms above: bytes landed and the file cannot be cut back, so a
            // retry would replay on top of them. Surrendering the chunk is the honest outcome -- a gap
            // a reader is TOLD about, rather than a duplicate it cannot see.
            var logger = new ExecutionStreamLogger(dir, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("hello\n"));
            logger.AppendStdout(Bytes("after\n"));
            logger.MarkTerminal();

            Assert.True(WriteFailureMarked(dir), "an unrollbackable partial write is a gap and must be announced");
            var text = StdoutText(dir);
            Assert.DoesNotContain("hello\n", text, StringComparison.Ordinal);
            Assert.EndsWith("after\n", text, StringComparison.Ordinal);
        }
        finally
        {
            appender.Dispose();
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
            // A stderr failure costs neither stream its bytes: stderr's own chunk is recovered and
            // stdout's write is unaffected. Same cross-stream coupling #1525 F4 removed one layer down.
            // The BOUND and the LOSS DECLARATION being per-stream is the separate claim, and this arm
            // exercises neither of them (#1879 review LOW) -- the arm below it does.
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
    public void The_bound_and_the_loss_declaration_are_per_stream()
    {
        var dir = NewDir("split-bound");
        try
        {
            // #1879 review LOW: the claim the arm above was credited with but did not make. Only the
            // stderr sink is broken, and permanently, so stderr passes its (deliberately tiny) bound
            // and is declared lost -- while stdout, sharing the same logger and the same bound, keeps
            // its bytes and stays unmarked. Both polarities of the marker, one per stream.
            var appender = new FlakyAppender(
                failuresToInject: int.MaxValue,
                failWhen: (path, _) => path.EndsWith(ExecutionStreamLogger.StderrLogFileName, StringComparison.Ordinal));
            var logger = new ExecutionStreamLogger(dir, maxPendingBytes: 4, appendBytes: appender.Append);

            logger.AppendStderr(Bytes("eeeeeeee\n")); // 9 bytes against a bound of 4: surrendered
            logger.AppendStdout(Bytes("o1\n"));
            logger.MarkTerminal();

            Assert.True(File.Exists(Path.Combine(dir, ExecutionStreamLogger.StderrWriteFailureMarkerFileName)),
                "the stream that lost bytes must be marked");
            Assert.False(WriteFailureMarked(dir), "the stream that lost nothing must not be");
            Assert.Equal("o1\n", StdoutText(dir));
            Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(dir, ExecutionStreamLogger.StderrLogFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void Token_dimensions_survive_a_failure_on_the_chunk_carrying_the_terminal_usage_line()
    {
        // #1879 review MEDIUM: the failure is injected on the chunk that actually carries the terminal
        // usage record. The previous shape counted failures rather than choosing one, so it fired on
        // the assistant line and the terminal line -- the one every token dimension below comes from --
        // was written after recovery, never traversing the failed path at all.
        AssertTokenDimensionsSurviveAFailureOn(ClaudeTerminalLine, "terminal");
    }

    [Fact]
    public void Token_dimensions_survive_a_failure_on_an_earlier_chunk_of_the_same_stream()
    {
        // The second case, kept: the failure lands on the assistant line, whose LOSS is what would cost
        // the attempt its live-billed Σ (and so its whole reconciliation triple) rather than its
        // dimensions. Two chunks, two arms, one for each thing a lost chunk can take away.
        AssertTokenDimensionsSurviveAFailureOn(ClaudeAssistantLine, "assistant");
    }

    private static void AssertTokenDimensionsSurviveAFailureOn(string failingLine, string tag)
    {
        // The end-to-end claim the issue is actually about: a transient write failure on the chunk that
        // happens to carry the vendor's terminal usage record must not cost the attempt its token
        // reconciliation. Written through the real logger, read through the real projector.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1876-{tag}-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1876");
            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);

            var target = Bytes(failingLine + "\n");
            var appender = new FlakyAppender(
                failuresToInject: 1,
                failWhen: (_, data) => data.AsSpan().SequenceEqual(target));
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
    public void A_marker_write_that_fails_is_retried_on_the_next_successful_append_until_it_lands()
    {
        var dir = NewDir("marker-retry");
        var markerPath = Path.Combine(dir, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName);
        try
        {
            // #1879 review HIGH 2. The obstruction is real rather than injected: a DIRECTORY sitting on
            // the marker's own path, which File.Exists reads as absent and File.WriteAllBytes refuses
            // with UnauthorizedAccessException -- the same shape an ACL on the output directory
            // produces, and the reason the marker write cannot be assumed to succeed just because it is
            // small.
            Directory.CreateDirectory(markerPath);

            var appender = new FlakyAppender(failuresToInject: 1);
            var logger = new ExecutionStreamLogger(dir, maxPendingBytes: 0, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("lost\n"));

            // The loss is real and the announcement did not land. Pre-#1879 the latch was set here and
            // this was permanent: every later chunk landed around an unannounced gap.
            Assert.False(WriteFailureMarked(dir));

            Directory.Delete(markerPath);
            logger.AppendStdout(Bytes("kept\n"));
            logger.MarkTerminal();

            Assert.True(WriteFailureMarked(dir), "the pending announcement must be retried once writes work again");
            Assert.Equal(0, new FileInfo(markerPath).Length); // the marker's existence is its whole payload
            Assert.Equal("kept\n", StdoutText(dir));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_marker_that_never_lands_is_said_on_stderr_exactly_once()
    {
        // The limit of the retry above, stated rather than papered over: when the marker can never be
        // created, the projector -- an out-of-process reader of these files -- has nothing to read and
        // will report the reconciliation as complete. The only remaining channel is the operator's, so
        // the logger says so there, once, rather than per attempt.
        var dir = NewDir("marker-never");
        var markerPath = Path.Combine(dir, ExecutionStreamLogger.StdoutWriteFailureMarkerFileName);
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Directory.CreateDirectory(markerPath);
            Console.SetError(stderr);

            var appender = new FlakyAppender(failuresToInject: int.MaxValue);
            var logger = new ExecutionStreamLogger(dir, maxPendingBytes: 0, appendBytes: appender.Append);
            logger.AppendStdout(Bytes("lost\n"));
            logger.AppendStdout(Bytes("lost too\n"));
            logger.MarkTerminal();

            Console.SetError(originalError);

            Assert.False(WriteFailureMarked(dir));
            var written = stderr.ToString();
            Assert.Contains(ExecutionStreamLogger.StdoutWriteFailureMarkerFileName, written, StringComparison.Ordinal);
            Assert.Equal(1, written.Split("unannounced gap", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    [Fact]
    public void A_logger_that_could_not_initialize_reports_the_write_failure_reason()
    {
        // #1879 review HIGH 2, the other unrecorded path: initialization failed, so every append for
        // the whole execution is a silent no-op and the capture is empty rather than partial. Before
        // this the attempt reported wall-clock alone, indistinguishable from a worker that emitted
        // nothing -- which is a different fact with a different remedy.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1879-init-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1879-init");
            var outputDir = ArtifactManager.ResolveOutputDirectory(testRoot, executionId);
            Directory.CreateDirectory(outputDir);
            // A directory where the stream file has to go: the eager create throws, the logger disables
            // itself, and no .stdout.log will ever exist.
            Directory.CreateDirectory(Path.Combine(outputDir, ExecutionStreamLogger.StdoutLogFileName));

            var logger = new ExecutionStreamLogger(outputDir);
            logger.AppendStdout(Bytes(ClaudeTerminalLine + "\n"));
            logger.MarkTerminal();

            Assert.True(WriteFailureMarked(outputDir));

            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var view = Assert.Single(
                ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default)).Value;

            Assert.Equal("stream-truncated-by-write-failure", view.BilledReconciliationUnavailable);
            Assert.Null(view.TokensIn);
            Assert.Null(view.BilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void CONTROL_an_execution_with_no_stream_and_no_marker_reports_no_reason_at_all()
    {
        // The polarity of the arm above, one condition apart: same missing .stdout.log, no marker. This
        // is the pre-#1706 "nothing was read" case and it must stay reasonless -- a projector that
        // reported a write failure whenever a stream was absent would relabel every execution whose
        // vendor wrote nothing.
        var testRoot = Path.Combine(Path.GetTempPath(), $"usage-1879-nostream-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1879-nostream");
            Directory.CreateDirectory(ArtifactManager.ResolveOutputDirectory(testRoot, executionId));

            var start = DateTime.UtcNow;
            var entries = new List<LogEntry>
            {
                new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(AcceptedRequest(executionId))),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), start),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), start.AddSeconds(1)),
            };

            var view = Assert.Single(
                ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default)).Value;

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
