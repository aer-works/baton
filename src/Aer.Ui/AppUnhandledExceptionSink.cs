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

            var entry = $"[{DateTimeOffset.UtcNow:O}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // The sink itself must never throw out of the guard.
        }
    }
}
