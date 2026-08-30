using System.Text;
using System.Text.Json;
using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// Appends <see cref="RoomEvent"/> lines to <c>room.jsonl</c> (#798) with single-writer
/// discipline and fsync crash durability.
/// </summary>
public sealed class RoomEventLogWriter : IRoomEventLogWriter, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RoomEventLogWriter(string logFilePath)
        : this(OpenAppendStream(logFilePath))
    {
    }

    public RoomEventLogWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// #880: how long a writer waits for a competing writer to let go before giving up. Sized
    /// against the holder it loses to — the daemon's room sweep, whose append is one line — and
    /// bounded so a genuinely stuck writer still surfaces instead of hanging the caller.
    /// </summary>
    private static readonly TimeSpan OpenContentionBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// #880: <see cref="FileShare.Read"/> is deliberate and stays. Two concurrent appenders to one
    /// JSONL would interleave partial lines, so exactly one writer at a time is the invariant this
    /// file exists to keep — this is the one member of the sharing-violation family (#839, #840,
    /// #842, #843, #872) where tolerant share flags would be the <b>wrong</b> fix.
    /// <para>
    /// What was wrong is that losing that race was terminal. The daemon's resolve endpoint opens
    /// its writer before the <c>ConcurrencyGuard</c> that is supposed to serialise it against the
    /// room sweep, so the two collided here and the <see cref="IOException"/> escaped as a 500 —
    /// measured on CI, not theorised. A loser now waits out a hold measured in milliseconds and
    /// then proceeds, which keeps the invariant and removes the false failure.
    /// </para>
    /// <para>
    /// Windows-specific in practice, and worth stating rather than leaving for the next reader to
    /// rediscover: <see cref="FileShare"/> is only OS-enforced on Windows, so on Linux and macOS the
    /// second open simply succeeds and this retry never engages. The single-writer invariant is
    /// therefore <b>not</b> enforced by the filesystem there — it rests on callers going through the
    /// room's <c>ConcurrencyGuard</c>, which is the same reason #857 exists.
    /// </para>
    /// </summary>
    private static FileStream OpenAppendStream(string logFilePath)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Monotonic, so a backward wall-clock step cannot stretch the wait past its budget.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return new FileStream(
                    logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1,
                    useAsync: true);
            }
            catch (IOException) when (elapsed.Elapsed < OpenContentionBudget)
            {
                // Thread.Sleep rather than Task.Delay: this is a constructor path, and a caller on a
                // starved pool would otherwise have a retry that cannot be scheduled.
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
        }
    }

    public Task AppendAsync(RoomEvent roomEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roomEvent);
        return AppendEntryAsync(new LogEntry.RoomLogEntry(roomEvent, DateTime.UtcNow), cancellationToken);
    }

    private async Task AppendEntryAsync(LogEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry, typeof(LogEntry), FlowEventLogJson.Options);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (_stream is FileStream fileStream)
            {
                fileStream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
