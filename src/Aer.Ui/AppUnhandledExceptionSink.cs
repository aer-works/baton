using Aer.Adapters;

namespace Aer.Ui;

/// <summary>
/// Minimal durable append-only sink for desktop app-level unhandled exceptions (#1176).
/// Resolves the log path dynamically under <see cref="AerPaths.Root"/> on every write
/// (never cached in a static readonly field, matching AerPaths discipline).
/// The sink itself catches all exceptions so it never throws out of the exception guard.
/// </summary>
public static class AppUnhandledExceptionSink
{
    // #1189: the two guards write from different threads — the dispatcher's from the UI thread, the
    // unobserved-task one from a finalizer thread — and File.AppendAllText opens the file each
    // time. Two overlapping opens are a sharing violation, which the catch below would turn into a
    // silently missing entry: precisely the disappearance this sink exists to end. Writes are rare
    // and short, so a lock is the whole answer.
    private static readonly Lock WriteLock = new();

    /// <summary>
    /// Path to the durable exception log under AER home.
    /// Resolves fresh on every access so <c>AER_HOME</c> overrides (e.g. in test runs) are honored.
    /// </summary>
    public static string LogPath => Path.Combine(AerPaths.Root, "logs", "ui-exceptions.log");

    public static void LogException(Exception ex)
    {
        try
        {
            var logPath = LogPath;
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // ToString rather than Message + StackTrace: for an AggregateException that is the
            // difference between recording every fault it carries and recording only the first.
            var entry = $"[{DateTimeOffset.UtcNow:O}] {ex}{Environment.NewLine}{Environment.NewLine}";
            lock (WriteLock)
            {
                // #1201: NOT File.AppendAllText — it opens with FileShare.Read, so anyone else
                // holding the file open for reading (a person tailing it, a second Baton process,
                // the test that polls it) makes the write fail with a sharing violation, which the
                // catch below turns into a silently missing entry. FileShare.ReadWrite lets the
                // write through instead. The lock above is still what orders this app's own two
                // writer threads; sharing is the other half, not a replacement for it.
                using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write(entry);
            }
        }
        catch
        {
            // The sink itself must never throw out of the guard.
        }
    }
}
