using System.IO;
using Aer.Flow.Store;

namespace Aer.Flow.Dispatch;

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
    /// True when <paramref name="fileName"/> is one of this logger's own stream files — the four
    /// names declared above, and nothing else. This is the one place that question is answered
    /// (#1345); callers filter with it rather than restating which names are the engine's.
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
    /// <em>declared</em> output: <see cref="Aer.Flow.Domain.WorkerContract"/>'s
    /// <c>ProducedOutput</c> constructor throws on one and <c>WorkflowDefinitionValidator</c> fails
    /// validation for one. But an <em>undeclared</em> file a worker happens to write into its output
    /// directory still reaches a surface, because that list is a directory read, not a contract — so
    /// a worker-written <c>.gitignore</c> is a deliverable this filter must not swallow, even though
    /// it could never have been declared. Narrow filter, broad declaration ban: both hold.
    /// </para>
    /// </summary>
    public static bool IsStreamLogFileName(string fileName) =>
        string.Equals(fileName, StdoutLogFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StdoutRolloverFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrLogFileName, StringComparison.Ordinal)
        || string.Equals(fileName, StderrRolloverFileName, StringComparison.Ordinal);

    private readonly string _outputDirectory;
    private readonly long _maxSizeBytes;
    private readonly object _lock = new();

    private bool _isTerminal;
    private long _stdoutSize;
    private long _stderrSize;

    public ExecutionStreamLogger(string outputDirectory, long maxSizeBytes = DefaultMaxSizeBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        _outputDirectory = outputDirectory;
        _maxSizeBytes = maxSizeBytes;

        var stdoutPath = Path.Combine(_outputDirectory, StdoutLogFileName);
        var stderrPath = Path.Combine(_outputDirectory, StderrLogFileName);

        _stdoutSize = File.Exists(stdoutPath) ? new FileInfo(stdoutPath).Length : 0;
        _stderrSize = File.Exists(stderrPath) ? new FileInfo(stderrPath).Length : 0;
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
        AppendChunk(StdoutLogFileName, StdoutRolloverFileName, data, ref _stdoutSize);
    }

    public void AppendStderr(byte[] data)
    {
        AppendChunk(StderrLogFileName, StderrRolloverFileName, data, ref _stderrSize);
    }

    public void MarkTerminal()
    {
        lock (_lock)
        {
            _isTerminal = true;
        }
    }

    private void AppendChunk(string logFileName, string rolloverFileName, byte[] data, ref long currentSize)
    {
        if (data is null || data.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_isTerminal)
            {
                throw new InvalidOperationException("Cannot append to stream log after execution has reached a terminal event.");
            }

            var logPath = Path.Combine(_outputDirectory, logFileName);
            var rolloverPath = Path.Combine(_outputDirectory, rolloverFileName);

            if (currentSize > 0 && (currentSize + data.Length > _maxSizeBytes))
            {
                if (File.Exists(logPath))
                {
                    RetryingFileMove.Move(logPath, rolloverPath, overwrite: true);
                }

                currentSize = 0;
            }

            Directory.CreateDirectory(_outputDirectory);
            using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush();
            }

            currentSize += data.Length;
        }
    }
}
