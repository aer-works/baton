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
    /// C-11 entry) now names — a fact stated once, referenced from there rather than restated. As of
    /// #1351, no production caller enumerates an execution's output directory at all (nothing to
    /// filter yet), and <c>Baton.Architecture.Tests.ExecutionOutputDirectoryListingTests</c> is the
    /// tripwire: it pins every raw file-listing call site in <c>src/</c> to a reviewed allowlist, so
    /// the next one that appears fails the build unless it either routes through a filtered listing
    /// using this method or is added to that allowlist with proof it does not read an execution's
    /// output directory.
    /// </para>
    /// </summary>
    public static bool IsStreamLogFileName(string fileName) =>
        string.Equals(fileName, StdoutLogFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StdoutRolloverFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrLogFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrRolloverFileName, StringComparison.Ordinal)
        || string.Equals(fileName, AgyHookVerdictLedgerFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StdoutTruncationMarkerFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrTruncationMarkerFileName, StringComparison.Ordinal);

    private readonly string _outputDirectory;
    private readonly long _maxSizeBytes;
    private readonly object _lock = new();

    private bool _isTerminal;
    private bool _disabled;
    private bool _failedOnce;
    private long _stdoutSize;
    private long _stderrSize;
    private int _stdoutRollovers;
    private int _stderrRollovers;

    public ExecutionStreamLogger(string outputDirectory, long maxSizeBytes = DefaultMaxSizeBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        _outputDirectory = outputDirectory;
        _maxSizeBytes = maxSizeBytes;

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

            _stdoutSize = File.Exists(stdoutPath) ? new FileInfo(stdoutPath).Length : 0;
            _stderrSize = File.Exists(stderrPath) ? new FileInfo(stderrPath).Length : 0;

            // #1724 item 3: `_stdoutRollovers` is otherwise instance state seeded to 0, so a second
            // logger constructed over a directory that has already rolled once (`.stdout.log.1` or the
            // truncation marker already on disk) would treat its own first destructive roll as roll #1
            // and never write the marker -- fail-open. Seeding from disk makes the count agree with
            // what actually happened to this directory, not just this instance's own history of it.
            var stdoutRolloverPath = Path.Combine(_outputDirectory, StdoutRolloverFileName);
            var stdoutMarkerPath = Path.Combine(_outputDirectory, StdoutTruncationMarkerFileName);
            _stdoutRollovers = File.Exists(stdoutRolloverPath) || File.Exists(stdoutMarkerPath) ? 1 : 0;
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

    public void AppendStdout(byte[] data)
    {
        AppendChunk(StdoutLogFileName, StdoutRolloverFileName, data, ref _stdoutSize, ref _stdoutRollovers);
    }

    public void AppendStderr(byte[] data)
    {
        AppendChunk(StderrLogFileName, StderrRolloverFileName, data, ref _stderrSize, ref _stderrRollovers);
    }

    public void MarkTerminal()
    {
        lock (_lock)
        {
            _isTerminal = true;
        }
    }

    private void AppendChunk(string logFileName, string rolloverFileName, byte[] data, ref long currentSize, ref int rolloverCount)
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

            try
            {
                var logPath = Path.Combine(_outputDirectory, logFileName);
                var rolloverPath = Path.Combine(_outputDirectory, rolloverFileName);

                if (currentSize > 0 && (currentSize + data.Length > _maxSizeBytes))
                {
                    if (File.Exists(logPath))
                    {
                        RetryingFileMove.Move(logPath, rolloverPath, overwrite: true);
                    }

                    rolloverCount++;
                    if (rolloverCount > 1)
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
                        WriteTruncationMarker(logFileName);
                    }

                    currentSize = 0;
                }

                Directory.CreateDirectory(_outputDirectory);
                using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush();
                }

                currentSize += data.Length;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // #1525 F4: NOT a permanent latch. Every chunk opens, writes, flushes, and closes its
                // own handle (no state survives between calls), so a transient failure here -- an AV
                // scanner's momentary lock, RoomRetentionSweep racing a move, a delete-pending file
                // FileShare.Delete now makes reachable, a momentary ENOSPC -- corrupts nothing and the
                // next chunk gets a clean attempt. Latching used to blind BOTH streams for the rest of
                // what can be a multi-hour lane over one such blip; skipping the failed chunk keeps the
                // tail surface alive instead. The warning still logs only once per stream, so a
                // persistently broken sink (e.g. the directory-obstruction case below) does not spam.
                if (!_failedOnce)
                {
                    _failedOnce = true;
                    Console.Error.WriteLine($"Warning: Failed to persist execution stream log in '{_outputDirectory}': {ex.Message}. Continuing to retry on subsequent chunks.");
                }
            }
        }
    }

    /// <summary>
    /// #1706 review: drops the empty sentinel next to the stream whose second (or later) rollover just
    /// discarded a segment. Deliberately best-effort and swallowed on failure — a stream log that
    /// cannot write its own chunks is already handled by the caller's warning arm, and throwing here
    /// would turn a retention detail into a dispatch failure. The cost of a missing marker is a
    /// reader that reports a live Σ it should have withheld, which is the pre-#1706 behaviour, not a
    /// worse one.
    /// </summary>
    private void WriteTruncationMarker(string logFileName)
    {
        try
        {
            var markerPath = Path.Combine(_outputDirectory, TruncationMarkerFileNameFor(logFileName));
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
