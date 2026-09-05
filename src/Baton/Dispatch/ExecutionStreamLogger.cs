using System.IO;
using Baton.Store;

namespace Baton.Dispatch;

/// <summary>
/// Appends worker stdout/stderr chunks as received to per-execution stream files (<c>.stdout.log</c>
/// and <c>.stderr.log</c>) in the execution's output directory.
/// Append-only while non-terminal; immutable after the terminal event.
/// Performs a single 8 MiB rollover per stream file (<c>.stdout.log.1</c> / <c>.stderr.log.1</c>).
/// </summary>
public sealed class ExecutionStreamLogger
{
    public const long DefaultMaxSizeBytes = 8 * 1024 * 1024; // 8 MiB

    /// <summary>
    /// #1876: how many bytes of chunks whose write FAILED this logger will hold in memory, waiting for
    /// a later chunk's open to succeed, before it gives up and declares the stream lossy. Bounded
    /// because the failure this exists for — a reader holding the file open, an AV scanner, a
    /// delete-pending handle — can also be permanent, and an unbounded retry queue on a multi-hour
    /// lane is a memory leak that ends the dispatch rather than degrading it.
    /// </summary>
    public const long DefaultMaxPendingBytes = 4 * 1024 * 1024; // 4 MiB

    public const string StdoutLogFileName = ".stdout.log";
    public const string StdoutRolloverFileName = ".stdout.log.1";
    public const string StderrLogFileName = ".stderr.log";
    public const string StderrRolloverFileName = ".stderr.log.1";

    /// <summary>
    /// #1706 review: written beside a stream that has rolled MORE THAN ONCE, i.e. whose earliest
    /// segments this logger has permanently discarded (each roll overwrites the single
    /// <c>.log.1</c>). Its presence is the only evidence a later reader has that the surviving files
    /// are not the whole stream — see <see cref="Baton.Status.ExecutionUsageProjector"/>, which
    /// withholds its live-billed Σ rather than reporting a Σ over a partial replay. Empty by design:
    /// the file's existence is the entire payload.
    /// </summary>
    public const string StdoutTruncationMarkerFileName = ".stdout.log.truncated";
    public const string StderrTruncationMarkerFileName = ".stderr.log.truncated";

    /// <summary>
    /// #1876: the same "these files are not the whole stream" sentinel as
    /// <see cref="StdoutTruncationMarkerFileName"/>, for the OTHER way this logger can lose bytes —
    /// chunks whose write failed and whose retry queue then hit <see cref="DefaultMaxPendingBytes"/>
    /// (or which were still queued when the execution went terminal). A separate file rather than a
    /// second use of the rollover marker so a reader can tell the two apart: a rollover gap is at a
    /// known place (the head of the retained window) and a write-failure gap is at an unknown one, and
    /// the two have different operator remedies. Empty by design, like the rollover marker.
    /// <para>
    /// Its ABSENCE after a transient failure is the load-bearing half: a failure the buffer absorbed
    /// wrote no marker, because nothing was lost — see <c>spec/baton.md</c> §3 for why that case gets
    /// no <c>billedReconciliationUnavailable</c> reason string of its own.
    /// </para>
    /// </summary>
    public const string StdoutWriteFailureMarkerFileName = ".stdout.log.write-failed";
    public const string StderrWriteFailureMarkerFileName = ".stderr.log.write-failed";

    /// <summary>
    /// The literal value of <c>Baton.Vendors.AgyWorkerAdapter.VerdictLedgerFileName</c>, duplicated
    /// rather than referenced (#1732 review sub-threshold): Architecture Rule 2 keeps this core layer
    /// from taking a project reference on <c>Baton.Vendors</c>, and from naming a vendor at all, so
    /// the one place record-once would normally point is unreachable from here. If that value ever
    /// changes, this constant is the other place it must change too.
    /// </summary>
    private const string AgyHookVerdictLedgerFileName = ".agy-hook-verdicts.ndjson";

    /// <summary>The truncation marker that belongs beside <paramref name="logFileName"/>.</summary>
    private static string TruncationMarkerFileNameFor(string logFileName) =>
        string.Equals(logFileName, StdoutLogFileName, StringComparison.Ordinal)
            ? StdoutTruncationMarkerFileName
            : StderrTruncationMarkerFileName;

    /// <summary>The write-failure marker that belongs beside <paramref name="logFileName"/>.</summary>
    private static string WriteFailureMarkerFileNameFor(string logFileName) =>
        string.Equals(logFileName, StdoutLogFileName, StringComparison.Ordinal)
            ? StdoutWriteFailureMarkerFileName
            : StderrWriteFailureMarkerFileName;

    /// <summary>
    /// True when <paramref name="fileName"/> is one of this logger's own stream files — the
    /// names declared above — or the agy hook verdict ledger's file name (#1732 review sub-threshold:
    /// same rationale, a different engine-owned mechanism artifact written into the same output
    /// directory by <c>Baton.Vendors.AgyWorkerAdapter</c>, not by this logger). This is the one place
    /// that question is answered (#1345); callers filter with it rather than restating which names
    /// are the engine's.
    /// <para>
    /// Why it exists: these files land in the execution's <em>output</em> directory, so anything
    /// enumerating that directory picks them up and presents AER's own capture of a run as though a
    /// worker had produced it. Decision
    /// <c>docs/decisions/0021-artifacts-are-files.md</c> draws exactly that line — the mechanism
    /// should be abstracted away, the documents should not — and a stream log is mechanism.
    /// </para>
    /// <para>
    /// Deliberately narrow rather than a dot-prefix rule, and the layering is worth stating because
    /// two other places sound broader than this one. A dot-prefixed name can never be a
    /// <em>declared</em> output: <see cref="Baton.Domain.WorkerContract"/>'s
    /// <c>ProducedOutput</c> constructor throws on one and <c>WorkflowDefinitionValidator</c> fails
    /// validation for one. But an <em>undeclared</em> file a worker happens to write into its output
    /// directory still reaches a surface, because that list is a directory read, not a contract — so
    /// a worker-written <c>.gitignore</c> is a deliverable this filter must not swallow, even though
    /// it could never have been declared. Narrow filter, broad declaration ban: both hold.
    /// </para>
    /// <para>
    /// #1351: this is the single filtered listing seam spec/baton.md's Fleet Glass section (§6, the
    /// C-11 entry) now names — a fact stated once, referenced from there rather than restated.
    /// <c>Baton.Cli.Daemon.FleetProjectionWriter</c> (#1557) is the first production caller that
    /// enumerates an execution's former output directory — walking a pruned execution's directory to
    /// size it for <c>pruned[].bytes</c> — and deliberately sums unfiltered rather than applying this
    /// filter; see that method's own comment for why. <c>Baton.Architecture.Tests.ExecutionOutputDirectoryListingTests</c>
    /// is the tripwire: it pins every raw file-listing call site in <c>src/</c> to a reviewed
    /// allowlist, so the next one that appears fails the build unless it either routes through a
    /// filtered listing using this method or is added to that allowlist with proof it does not read an
    /// execution's output directory (or, as here, a one-line justification for why it deliberately
    /// does not filter).
    /// </para>
    /// </summary>
    public static bool IsStreamLogFileName(string fileName) =>
        string.Equals(fileName, StdoutLogFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StdoutRolloverFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrLogFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrRolloverFileName, StringComparison.Ordinal)
        || string.Equals(fileName, AgyHookVerdictLedgerFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StdoutTruncationMarkerFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrTruncationMarkerFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StdoutWriteFailureMarkerFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrWriteFailureMarkerFileName, StringComparison.Ordinal);

    /// <summary>
    /// One chunk waiting for a retry. <see cref="Owned"/> distinguishes the caller's own array — safe
    /// to hold only for the duration of the <see cref="AppendChunk"/> call that handed it over — from a
    /// copy this logger made when the write failed and it decided to keep the bytes past that call.
    /// Copying only on the failure path keeps the success path allocation-free, which is every chunk of
    /// every healthy dispatch.
    /// </summary>
    private readonly record struct PendingChunk(byte[] Bytes, bool Owned);

    /// <summary>Everything that is per-stream rather than per-logger.</summary>
    private sealed class StreamState(string logFileName, string rolloverFileName)
    {
        public string LogFileName { get; } = logFileName;
        public string RolloverFileName { get; } = rolloverFileName;
        public long Size;
        public int Rollovers;
        public readonly List<PendingChunk> Pending = [];
        public long PendingBytes;
        public bool WriteLossDeclared;
    }

    private readonly string _outputDirectory;
    private readonly long _maxSizeBytes;
    private readonly long _maxPendingBytes;
    private readonly Action<string, byte[]> _appendBytes;
    private readonly object _lock = new();

    private readonly StreamState _stdout = new(StdoutLogFileName, StdoutRolloverFileName);
    private readonly StreamState _stderr = new(StderrLogFileName, StderrRolloverFileName);

    private bool _isTerminal;
    private bool _disabled;
    private bool _failedOnce;

    /// <param name="maxPendingBytes">
    /// See <see cref="DefaultMaxPendingBytes"/>. Zero reproduces the pre-#1876 behaviour exactly — a
    /// failed chunk is dropped on the spot — which is what the tests use as their control arm.
    /// </param>
    /// <param name="appendBytes">
    /// The raw "append these bytes to this path" step, injectable so a test can fail it deterministically
    /// N times without needing a real Windows sharing conflict. Null uses the real file append; nothing
    /// in <c>src/</c> passes anything else.
    /// </param>
    public ExecutionStreamLogger(
        string outputDirectory,
        long maxSizeBytes = DefaultMaxSizeBytes,
        long maxPendingBytes = DefaultMaxPendingBytes,
        Action<string, byte[]>? appendBytes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPendingBytes);
        _outputDirectory = outputDirectory;
        _maxSizeBytes = maxSizeBytes;
        _maxPendingBytes = maxPendingBytes;
        _appendBytes = appendBytes ?? AppendBytesToFile;

        try
        {
            var stdoutPath = Path.Combine(_outputDirectory, StdoutLogFileName);
            var stderrPath = Path.Combine(_outputDirectory, StderrLogFileName);

            // #1525: created eagerly, before the first chunk, the same create-regardless-of-content
            // reasoning CoreDispatcher.cs already applies to the #887 stdout artifact. A worker whose
            // vendor CLI buffers its own stdout (a plain-text, non-streaming print mode has nothing to
            // flush until it is done composing) can go the entire length of a long dispatch without a
            // single AppendChunk call -- RoomDetailTool's tail then read "no file" for the whole run,
            // which is indistinguishable from "the tee is broken" to an operator drilling into a live
            // lane. An empty file that exists from t=0 is the honest state: nothing has arrived yet,
            // not nothing ever will.
            Directory.CreateDirectory(_outputDirectory);
            if (!File.Exists(stdoutPath))
            {
                using var _ = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }

            if (!File.Exists(stderrPath))
            {
                using var _ = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }

            _stdout.Size = File.Exists(stdoutPath) ? new FileInfo(stdoutPath).Length : 0;
            _stderr.Size = File.Exists(stderrPath) ? new FileInfo(stderrPath).Length : 0;

            // #1724 item 3: `_stdoutRollovers` is otherwise instance state seeded to 0, so a second
            // logger constructed over a directory that has already rolled once (`.stdout.log.1` or the
            // truncation marker already on disk) would treat its own first destructive roll as roll #1
            // and never write the marker -- fail-open. Seeding from disk makes the count agree with
            // what actually happened to this directory, not just this instance's own history of it.
            var stdoutRolloverPath = Path.Combine(_outputDirectory, StdoutRolloverFileName);
            var stdoutMarkerPath = Path.Combine(_outputDirectory, StdoutTruncationMarkerFileName);
            _stdout.Rollovers = File.Exists(stdoutRolloverPath) || File.Exists(stdoutMarkerPath) ? 1 : 0;
        }
        catch (Exception ex)
        {
            _disabled = true;
            _failedOnce = true;
            Console.Error.WriteLine($"Warning: Failed to initialize execution stream logger for '{outputDirectory}': {ex.Message}. Stream logging disabled for this execution.");
        }
    }

    public bool IsTerminal
    {
        get
        {
            lock (_lock)
            {
                return _isTerminal;
            }
        }
    }

    public void AppendStdout(byte[] data) => AppendChunk(_stdout, data);

    public void AppendStderr(byte[] data) => AppendChunk(_stderr, data);

    /// <summary>
    /// #1876: flushes anything still queued from a failed write BEFORE latching terminal, because the
    /// append path below refuses to run afterwards and this is the last chance those bytes get. It is
    /// also where they matter most: a vendor's terminal usage line is the final chunk of the stream, so
    /// "the queue was still full at exit" and "the reconciler lost the only usage record" are the same
    /// event. Idempotent — <c>CoreDispatcher</c> calls this from both the Exited event and its own
    /// <c>finally</c>.
    /// </summary>
    public void MarkTerminal()
    {
        lock (_lock)
        {
            if (_isTerminal)
            {
                return;
            }

            if (!_disabled)
            {
                FlushAtTerminal(_stdout);
                FlushAtTerminal(_stderr);
            }

            _isTerminal = true;
        }
    }

    /// <summary>
    /// THE INVARIANT (#1876): what this logger writes to disk is a PREFIX of what the worker emitted —
    /// every byte in order, with no interior gap — unless a marker file beside it says otherwise. A
    /// failed write therefore queues its chunk rather than skipping it, and the only two ways a gap can
    /// appear are announced: <see cref="StdoutTruncationMarkerFileName"/> (rollover discarded a
    /// segment) and <see cref="StdoutWriteFailureMarkerFileName"/> (the retry queue overflowed, or was
    /// still full at terminal). That invariant is what
    /// <see cref="Baton.Status.ExecutionUsageProjector"/>'s replay rests on: a Σ accumulated over a
    /// stream with a silent hole is a fabricated under-read, indistinguishable from a real one, and it
    /// is the partial-attempt token count #1849's ledger is being built to consume.
    /// </summary>
    private void AppendChunk(StreamState stream, byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_disabled)
            {
                return;
            }

            if (_isTerminal)
            {
                throw new InvalidOperationException("Cannot append to stream log after execution has reached a terminal event.");
            }

            // Enqueued rather than written directly so that a chunk arriving while earlier ones are
            // still queued cannot overtake them: order is the queue's order, always. On the healthy
            // path the queue is empty, this appends one entry, and the flush below empties it again
            // before returning -- so `data` is never retained past this call unless a write failed,
            // which is the only branch that copies.
            stream.Pending.Add(new PendingChunk(data, Owned: false));
            stream.PendingBytes += data.Length;
            FlushPending(stream);
        }
    }

    /// <summary>
    /// Writes queued chunks oldest-first, dropping each from the queue only after ITS OWN write and
    /// flush have both returned. A chunk that throws stays queued and stops the drain, so a partial
    /// write can never be replayed on top of itself — "never duplicates a chunk" is a property of this
    /// per-chunk commit point, not of the caller.
    /// </summary>
    private void FlushPending(StreamState stream)
    {
        while (stream.Pending.Count > 0)
        {
            var chunk = stream.Pending[0];
            try
            {
                WriteOne(stream, chunk.Bytes);
            }
            catch (InvalidOperationException)
            {
                // Unchanged from #1525: this shape is a caller/contract error, not an IO blip, and it
                // has always propagated rather than being absorbed as a retryable write failure.
                throw;
            }
            catch (Exception ex)
            {
                OnWriteFailure(stream, ex);
                return;
            }

            stream.Pending.RemoveAt(0);
            stream.PendingBytes -= chunk.Bytes.Length;
        }
    }

    /// <summary>One chunk: roll if it would cross the size bound, then append it.</summary>
    private void WriteOne(StreamState stream, byte[] data)
    {
        var logPath = Path.Combine(_outputDirectory, stream.LogFileName);
        var rolloverPath = Path.Combine(_outputDirectory, stream.RolloverFileName);

        if (stream.Size > 0 && (stream.Size + data.Length > _maxSizeBytes))
        {
            if (File.Exists(logPath))
            {
                RetryingFileMove.Move(logPath, rolloverPath, overwrite: true);
            }

            stream.Rollovers++;
            if (stream.Rollovers > 1)
            {
                // #1706 review: this is the roll that DESTROYS data. The move above overwrote
                // the previous `.log.1`, so the segment it held is gone and no reader can
                // reconstruct the whole stream from what survives -- and no reader can INFER
                // that from the surviving files either (a once-rolled and a twice-rolled
                // `.log.1` are both a full-size file starting at an arbitrary offset). The
                // writer is the only party that knows, so it says so here, once, and
                // ExecutionUsageProjector reports its live Σ as unavailable rather than
                // fabricating an under-read out of a partial replay. Fail-closed: the marker's
                // ABSENCE is only trustworthy for streams written since this landed, which the
                // projector's own comment states.
                WriteMarker(TruncationMarkerFileNameFor(stream.LogFileName));
            }

            stream.Size = 0;
        }

        Directory.CreateDirectory(_outputDirectory);
        _appendBytes(logPath, data);
        stream.Size += data.Length;
    }

    /// <summary>
    /// #1525 F4, extended by #1876. Still NOT a permanent latch: every chunk opens, writes, flushes and
    /// closes its own handle, so a transient failure -- an AV scanner's momentary lock,
    /// RoomRetentionSweep racing a move, a delete-pending file, a momentary ENOSPC -- corrupts nothing
    /// and the next chunk gets a clean attempt. What #1876 changed is what happens to the chunk that
    /// failed: it is KEPT and retried, because the pre-#1876 behaviour skipped it and left a silent
    /// hole in a stream a reconciler later summed over. Bytes are only surrendered once the queue
    /// passes its bound, and that surrender is announced twice -- a distinct warning, and a marker file
    /// the reader can act on.
    /// </summary>
    private void OnWriteFailure(StreamState stream, Exception ex)
    {
        if (!_failedOnce)
        {
            _failedOnce = true;
            Console.Error.WriteLine($"Warning: Failed to persist execution stream log in '{_outputDirectory}': {ex.Message}. Buffering the chunk and retrying on subsequent chunks.");
        }

        if (stream.PendingBytes > _maxPendingBytes)
        {
            DeclareWriteLoss(stream);
            return;
        }

        // Past this point the queue outlives the AppendChunk call that handed the bytes over, so the
        // caller's array is no longer safe to hold: CoreDispatcher hands out the buffer its reader
        // filled, and nothing promises it will not fill it again. Copies are made here and nowhere
        // else, and re-copying an already-owned entry is skipped rather than paid for on every retry.
        for (var i = 0; i < stream.Pending.Count; i++)
        {
            if (!stream.Pending[i].Owned)
            {
                stream.Pending[i] = new PendingChunk((byte[])stream.Pending[i].Bytes.Clone(), Owned: true);
            }
        }
    }

    /// <summary>
    /// The stream is now provably not a prefix of what the worker emitted. Deliberately NOT gated on
    /// <c>_failedOnce</c>, which is already true by the time this can fire: "we retried and lost
    /// nothing" and "we have started discarding bytes" are different facts and an operator has to hear
    /// the second one even though they already heard the first.
    /// </summary>
    private void DeclareWriteLoss(StreamState stream)
    {
        stream.Pending.Clear();
        stream.PendingBytes = 0;

        if (!stream.WriteLossDeclared)
        {
            stream.WriteLossDeclared = true;
            WriteMarker(WriteFailureMarkerFileNameFor(stream.LogFileName));
            Console.Error.WriteLine($"Warning: Discarding buffered '{stream.LogFileName}' chunks in '{_outputDirectory}' after repeated write failures — this stream log now has a gap and its token reconciliation will report as unavailable.");
        }
    }

    /// <summary>
    /// Terminal-time drain. Anything the last attempt cannot land is lost for good — no later chunk is
    /// coming to retry it — so it is declared rather than left to look like a clean stream.
    /// </summary>
    private void FlushAtTerminal(StreamState stream)
    {
        FlushPending(stream);
        if (stream.Pending.Count > 0)
        {
            DeclareWriteLoss(stream);
        }
    }

    private static void AppendBytesToFile(string logPath, byte[] data)
    {
        using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(data, 0, data.Length);
        fs.Flush();
    }

    /// <summary>
    /// #1706 review: drops an empty sentinel next to a stream that has provably lost bytes — the
    /// rollover marker, or (#1876) the write-failure one. Deliberately best-effort and swallowed on
    /// failure — a stream log that cannot write its own chunks is already handled by the caller's
    /// warning arm, and throwing here would turn a retention detail into a dispatch failure. The cost
    /// of a missing marker is a reader that reports a live Σ it should have withheld, which is the
    /// pre-#1706 behaviour, not a worse one.
    /// </summary>
    private void WriteMarker(string markerFileName)
    {
        try
        {
            var markerPath = Path.Combine(_outputDirectory, markerFileName);
            if (!File.Exists(markerPath))
            {
                File.WriteAllBytes(markerPath, []);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Intentionally not rethrown -- see this method's own doc for why.
        }
    }
}
