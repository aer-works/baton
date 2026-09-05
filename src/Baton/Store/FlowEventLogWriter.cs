using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Baton.Concurrency;
using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// Appends <see cref="LogEntry"/> lines to the combined <c>flow.jsonl</c> with the
/// crash-durability guarantees required:
/// <list type="bullet">
/// <item>Each entry is serialized to one newline-terminated line and written in a single call,
/// so a reader tailing the file can only ever observe a complete line or nothing yet —
/// never a torn one.</item>
/// <item>Every write is fsync'd (or the equivalent durable flush) before either
/// <c>AppendAsync</c> overload returns, so a caller cannot proceed to the next write-sequence
/// step — e.g. dispatching an <see cref="ExecutionRequest"/> to Core — before the preceding
/// intent is durable.</item>
/// </list>
/// Implements both <see cref="IEventLogWriter"/> (Flow's own events) and
/// <see cref="ICoreEventLogWriter"/> (Core-originated lifecycle events, M7 Phase 6) over one
/// shared, gated stream — the single physical file the dual-log-ownership decision requires,
/// with ownership enforced by <see cref="LogEntry"/>'s type split rather than by which writer
/// interface a caller happens to hold.
/// </summary>
public sealed class FlowEventLogWriter : IEventLogWriter, ICoreEventLogWriter, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FlowEventLogWriter(string logFilePath)
        : this(OpenAppendStream(logFilePath))
    {
    }

    /// <summary>Writes to an already-open stream instead of opening a file. Exposed for testing.</summary>
    public FlowEventLogWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// #1650 F1: bounded rather than fail-fast on a sharing violation. The holder this open usually
    /// loses to is a live <c>baton run</c> pump on its way out, which releases <c>flow.lock</c> and
    /// only <em>then</em> disposes its own writer — so this handle is the LAST of the room's
    /// resources to clear, and a sibling command that has already waited out the lock still finds
    /// the journal held for the remainder of that same tail. Failing fast here made #1646's fix
    /// rename the flake rather than close it. See <see cref="RoutineHoldBudget"/> for what the wait
    /// is sized against; a hold that outlasts it is not the routine tail and still refuses.
    /// </summary>
    /// <exception cref="FlowJournalHeldException">See that type's own docs for why (#816).</exception>
    private static FileStream OpenAppendStream(string logFilePath)
    {
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Stopwatch, not DateTime.UtcNow, for the monotonic-deadline reason ConcurrencyGuard's own
        // AcquireWithinCore gives: a wall clock can step backwards and silently stretch this wait.
        var elapsed = Stopwatch.StartNew();

        // What keeps a non-sharing IOException fail-fast is the IsSharingViolation predicate on BOTH
        // filters below, not the loop's placement — the control test for this catch
        // (A_genuinely_different_IOException_surfaces_as_itself_not_the_journal_held_refusal, a parent
        // segment that is itself a file) propagates on the first pass either way. The placement buys
        // something narrower: Directory.CreateDirectory sits outside both the loop and the Stopwatch,
        // so it can neither be retried nor spend any of the budget.
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
            catch (IOException ex) when (FileHolderProbe.IsSharingViolation(ex) && elapsed.Elapsed < RoutineHoldBudget.Duration)
            {
                // Thread.Sleep rather than Task.Delay, matching ConcurrencyGuard: this runs from a
                // constructor on a possibly-starved pool, and a retry that cannot be scheduled is a
                // retry that does not happen.
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
            catch (IOException ex) when (FileHolderProbe.IsSharingViolation(ex))
            {
                // Name the holder while it is still held (the probe runs here, in-process, not in a
                // post-hoc step where a transient holder would already be gone). This turns the #398
                // Windows-CI flake from "used by another process, holder unknown" into a named culprit.
                // Only on the give-up path: DescribeHolders costs hundreds of milliseconds, so paying
                // it per retry would eat the budget it is supposed to be spent outside of.
                throw new FlowJournalHeldException(
                    $"'{logFilePath}' is held open by another process — usually this room's live " +
                    "'baton run' engine, which keeps the ledger open for its whole run, though any " +
                    "sibling baton command mid-append briefly holds it too. Still held after waiting " +
                    // The measured wait, not RoutineHoldBudget.Duration. This branch is only reachable
                    // once the budget is spent, so the two agree today — but a message that reports what
                    // it actually did cannot go quietly false if that ever stops being true, which is
                    // the same defect class as #1650 F4 one level down.
                    $"{elapsed.Elapsed.TotalMilliseconds:0}ms, so this is not the brief tail of a " +
                    "pump on its way out. Retry once nothing else holds the ledger; for a decision, the " +
                    "workflow's latest attempt must be Paused with no live 'baton run' (see 'baton status'). " +
                    $"Current holder: {FileHolderProbe.DescribeHolders(logFilePath)}",
                    ex);
            }
        }
    }

    public Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flowEvent);
        return AppendEntryAsync(new LogEntry.FlowLogEntry(flowEvent, DateTime.UtcNow), cancellationToken);
    }

    public Task AppendAsync(CoreEvent coreEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coreEvent);
        return AppendEntryAsync(new LogEntry.CoreLogEntry(coreEvent, DateTime.UtcNow), cancellationToken);
    }

    /// <inheritdoc />
    public Task AppendStreamLogLossAsync(
        FlowEvent.StreamLogLossDeclared streamLogLoss,
        CancellationToken cancellationToken = default) =>
        AppendAsync(streamLogLoss, cancellationToken);

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
