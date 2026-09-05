using System.Text;
using System.Text.Json;
using Baton.Domain;
using Baton.Store;

namespace Baton.Tests.Store;

public class FlowEventLogWriterTests
{
    private static FlowEvent.ExecutionSucceeded MakeEvent(string id) => new(new ExecutionId(id));

    [Fact]
    public async Task AppendAsync_writes_one_complete_newline_terminated_line_per_event()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
        await writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.True(text.EndsWith('\n'));
        Assert.Equal("exec-1", DeserializeExecutionSucceeded(lines[0]).ExecutionId.Value);
        Assert.Equal("exec-2", DeserializeExecutionSucceeded(lines[1]).ExecutionId.Value);
    }

    [Fact]
    public async Task AppendAsync_round_trips_through_a_real_file_across_multiple_appends()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(path))
            {
                await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken);
                await writer.AppendAsync(MakeEvent("exec-3"), TestContext.Current.CancellationToken);
            }

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            var ids = lines.Select(l => DeserializeExecutionSucceeded(l).ExecutionId.Value).ToArray();

            Assert.Equal(new[] { "exec-1", "exec-2", "exec-3" }, ids);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task AppendAsync_fsyncs_to_disk_before_returning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var stream = new FlushTrackingFileStream(path);
            await using var writer = new FlowEventLogWriter(stream, leaveOpen: true);

            await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);

            Assert.True(stream.FlushedToDisk);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_torn_write_never_leaves_a_partial_event_observable_as_a_complete_line()
    {
        using var buffer = new MemoryStream();

        await using var faulting = new TornWriteStream(buffer, callsBeforeFault: 1, truncateToBytes: 5);
        await using var writer = new FlowEventLogWriter(faulting, leaveOpen: true);

        await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(MakeEvent("exec-2"), TestContext.Current.CancellationToken));

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var lastNewline = text.LastIndexOf('\n');
        var completeLines = lastNewline >= 0
            ? text[..(lastNewline + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

        // The second write is torn (only a few bytes land, then the writer throws). A reader
        // tailing the file splits on '\n' — the dangling fragment has no terminator, so it must
        // never surface as a complete second event.
        Assert.Single(completeLines);
        var firstEntry = Assert.IsType<LogEntry.FlowLogEntry>(JsonSerializer.Deserialize<LogEntry>(completeLines[0], FlowEventLogJson.Options));
        var firstEvent = Assert.IsType<FlowEvent.ExecutionSucceeded>(firstEntry.Event);
        Assert.Equal("exec-1", firstEvent.ExecutionId.Value);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public async Task Concurrent_AppendAsync_calls_do_not_interleave_or_corrupt_lines()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        var appends = Enumerable.Range(0, 20)
            .Select(i => writer.AppendAsync(MakeEvent($"exec-{i}")));
        await Task.WhenAll(appends);

        var lines = Encoding.UTF8.GetString(buffer.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(20, lines.Length);
        Assert.All(lines, line => DeserializeExecutionSucceeded(line));
    }

    [Fact]
    public void Constructing_over_a_journal_already_held_open_by_another_writer_throws_FlowJournalHeldException_not_a_raw_IOException()
    {
        // #816: a live 'baton run' engine holds flow.jsonl open (FileAccess.Write, FileShare.Read)
        // for its whole pump duration, so a second command opening the same path the same way
        // must get the typed refusal, not the raw sharing-violation IOException that used to
        // crash 'baton decide' before validation ever ran.
        var path = Path.Combine(Path.GetTempPath(), $"flow-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var liveEngineHolder = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);

            var ex = Assert.Throws<FlowJournalHeldException>(() => new FlowEventLogWriter(path));

            Assert.IsType<IOException>(ex.InnerException);
            Assert.Contains("held open by another process", ex.Message);
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void A_genuinely_different_IOException_surfaces_as_itself_not_the_journal_held_refusal()
    {
        // Discriminating control for the sharing-violation-only catch above: a path whose parent
        // segment is itself an existing FILE (not a directory) makes Directory.CreateDirectory
        // throw an IOException that has nothing to do with another process holding the journal
        // open. If the catch above ever widened to "any IOException", this would start reporting
        // the misleading "held by another process" refusal for a completely different failure.
        var root = Path.Combine(Path.GetTempPath(), $"flow-control-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(root, []);
            var path = Path.Combine(root, "flow.jsonl");

            var ex = Assert.Throws<IOException>(() => new FlowEventLogWriter(path));

            Assert.IsNotType<FlowJournalHeldException>(ex);
        }
        finally
        {
            FileCleanup.Delete(root);
        }
    }

    [Fact]
    public async Task AppendAsync_rejects_a_null_event()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.AppendAsync((FlowEvent)null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_rejects_a_null_core_event()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.AppendAsync((CoreEvent)null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_writes_flow_and_core_events_as_distinct_owner_tagged_lines_on_one_stream()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 123), TestContext.Current.CancellationToken);

        var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        var flowEntry = Assert.IsType<LogEntry.FlowLogEntry>(JsonSerializer.Deserialize<LogEntry>(lines[0], FlowEventLogJson.Options));
        Assert.IsType<FlowEvent.ExecutionSucceeded>(flowEntry.Event);
        var coreEntry = Assert.IsType<LogEntry.CoreLogEntry>(JsonSerializer.Deserialize<LogEntry>(lines[1], FlowEventLogJson.Options));
        var started = Assert.IsType<CoreEvent.ExecutionStarted>(coreEntry.Event);
        Assert.Equal(123u, started.Pid);
    }

    [Fact]
    public async Task AppendAsync_stamps_WriterUtcTimestamp_on_flow_events()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        var before = DateTime.UtcNow;
        await writer.AppendAsync(MakeEvent("exec-1"), TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow;

        var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entry = Assert.IsType<LogEntry.FlowLogEntry>(JsonSerializer.Deserialize<LogEntry>(lines[0], FlowEventLogJson.Options));

        Assert.NotNull(entry.WriterUtcTimestamp);
        Assert.True(entry.WriterUtcTimestamp >= before && entry.WriterUtcTimestamp <= after);
    }

    [Fact]
    public async Task AppendAsync_stamps_WriterUtcTimestamp_on_core_events()
    {
        using var buffer = new MemoryStream();
        await using var writer = new FlowEventLogWriter(buffer, leaveOpen: true);

        var before = DateTime.UtcNow;
        await writer.AppendAsync(new CoreEvent.ExecutionStarted(new ExecutionId("exec-1"), Pid: 123), TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow;

        var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entry = Assert.IsType<LogEntry.CoreLogEntry>(JsonSerializer.Deserialize<LogEntry>(lines[0], FlowEventLogJson.Options));

        Assert.NotNull(entry.WriterUtcTimestamp);
        Assert.True(entry.WriterUtcTimestamp >= before && entry.WriterUtcTimestamp <= after);
    }

    private static FlowEvent.ExecutionSucceeded DeserializeExecutionSucceeded(string line)
    {
        var entry = Assert.IsType<LogEntry.FlowLogEntry>(JsonSerializer.Deserialize<LogEntry>(line, FlowEventLogJson.Options));
        return Assert.IsType<FlowEvent.ExecutionSucceeded>(entry.Event);
    }

    /// <summary>Wraps a stream and, after a configured number of calls, writes only a truncated
    /// prefix of the buffer before throwing — simulating a crash mid-write.</summary>
    private sealed class TornWriteStream(Stream inner, int callsBeforeFault, int truncateToBytes) : Stream
    {
        private int _callCount;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount > callsBeforeFault)
            {
                await inner.WriteAsync(buffer[..Math.Min(truncateToBytes, buffer.Length)], cancellationToken);
                throw new IOException("simulated crash mid-write");
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A real <see cref="FileStream"/> that records whether a durable (fsync) flush happened.</summary>
    private sealed class FlushTrackingFileStream(string path)
        : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true)
    {
        public bool FlushedToDisk { get; private set; }

        public override void Flush(bool flushToDisk)
        {
            base.Flush(flushToDisk);
            if (flushToDisk)
            {
                FlushedToDisk = true;
            }
        }
    }
}
