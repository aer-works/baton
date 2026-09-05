using System.Runtime.InteropServices;

namespace Baton.Vendors.Tests;

/// <summary>
/// Builds a throwaway child-process command line for the usage sources' internal program/args seam,
/// so a test can pin a REAL exit code and a REAL stdout through <c>BatonTask</c>'s own event stream
/// (#1869 review: the defect was that nothing subscribed to <c>Exited</c>, which a stubbed runner
/// cannot exercise). Shells out to <c>cmd</c> on Windows and <c>sh</c> elsewhere, matching the
/// platform split <c>AgyHookLivenessProbe</c> already uses in <c>src/</c>.
/// </summary>
/// <remarks>
/// On Windows the command is handed to <c>cmd</c> as SEPARATE arguments rather than one joined
/// string. <c>BatonTask</c> quotes any argument containing a space the Win32 CommandLineToArgvW way
/// (inner quotes backslash-escaped), and <c>cmd</c> does not understand <c>\"</c> — a temp path with
/// a space in it silently produced empty stdout when the whole command was pre-joined. Passing the
/// path as its own argument lets the standard quoting do the right thing.
/// </remarks>
internal static class UsageSourceShell
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string Program => IsWindows ? "cmd" : "sh";

    /// <summary>Writes junk to stdout, then exits with exactly <paramref name="exitCode"/>.</summary>
    public static string[] JunkThenExit(int exitCode) =>
        IsWindows
            ? ["/c", "echo", "junk-not-a-usage-report", "&", "exit", "/b", exitCode.ToString()]
            : ["-c", $"echo junk-not-a-usage-report; exit {exitCode}"];

    /// <summary>Exits 0 having written a file's bytes to stdout verbatim — tabs and all, unlike an <c>echo</c>.</summary>
    public static string[] PrintFile(string path) =>
        IsWindows ? ["/c", "type", path] : ["-c", $"cat '{path}'"];

    /// <summary>Exits 0 having written nothing at all to stdout.</summary>
    public static string[] PrintNothing() =>
        IsWindows ? ["/c", "rem"] : ["-c", ":"];

    /// <summary>Exits 0 having written a single blank line and nothing else.</summary>
    public static string[] PrintBlankLine() =>
        IsWindows ? ["/c", "echo."] : ["-c", "echo"];
}
