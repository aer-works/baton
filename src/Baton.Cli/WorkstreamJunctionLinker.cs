using System.ComponentModel;
using System.Diagnostics;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// The navigational half of #1619's ruling (issue #1614): <c>BatonPaths.ByWorkstream</c>
/// (<c>~/.baton/by-workstream/</c>) holds one Windows directory junction per room dispatched with a
/// <c>--workstream</c> slug, filed under a subdirectory named for that slug and pointing at the
/// room's real directory under <see cref="BatonPaths.Rooms"/> — so <c>cd
/// ~/.baton/by-workstream/1619</c> lists every room in that workstream without moving a single file
/// on disk (<c>DispatchCommand</c>'s and <c>RedispatchCommand</c>'s <c>bindings.json</c> stay the
/// single source of the room ↔ workstream mapping; this is a read-time convenience over it).
/// </summary>
/// <remarks>
/// Junctions (<c>mklink /J</c>), not symlinks: junction creation needs no elevation or Developer Mode
/// on Windows — the only supported platform (`ci.yml`, #1405) — while a directory symlink does. There
/// is no managed junction API in .NET, and adding a raw reparse-point P/Invoke would be a new Win32
/// surface Architecture Rule 3 reserves for the Job Object containment it already owns — so this
/// shells out to <c>cmd.exe /c mklink /J</c>, the same pattern <c>WorktreeProvisioner</c> already uses
/// for git.
/// </remarks>
public static class WorkstreamJunctionLinker
{
    /// <summary>
    /// Creates the junction for <paramref name="roomDirectoryPath"/> under its workstream's link
    /// directory. A no-op when <paramref name="workstream"/> is null — the default, unlabeled case
    /// <c>--label</c> itself has, and every room minted before #1619. <b>Never throws</b>: a failed
    /// junction (a machine policy that refuses <c>mklink</c>, a name collision with a stale entry)
    /// degrades to a stderr warning rather than failing a dispatch whose room already exists on disk
    /// and is already fully functional without the shortcut — this is a convenience link, not the
    /// room's identity.
    /// </summary>
    public static void CreateIfRequested(string? workstream, string roomDirectoryPath)
    {
        if (workstream is null)
        {
            return;
        }

        var linkDirectory = Path.Combine(BatonPaths.ByWorkstream, workstream);
        var roomName = Path.GetFileName(Path.TrimEndingDirectorySeparator(roomDirectoryPath));
        var linkPath = Path.Combine(linkDirectory, roomName);

        try
        {
            // mklink /J requires the link's parent directory to already exist.
            Directory.CreateDirectory(linkDirectory);

            if (Directory.Exists(linkPath))
            {
                // A fresh dispatch never reuses a room name (DispatchOptionsParser's own uniqueness
                // guarantee) -- this only fires on a retry against an already-linked room.
                return;
            }

            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // #466: the null-encoding default decodes the pipe with the console code page (OEM
                // cp437 under a default console), mangling non-ASCII output -- pin UTF-8 explicitly.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(Path.GetFullPath(roomDirectoryPath));

            using var process = Process.Start(startInfo)
                ?? throw new Win32Exception("could not start 'cmd.exe'");
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"Warning: could not create the by-workstream link at '{linkPath}' (mklink /J exit "
                    + $"{process.ExitCode}): {stderrTask.Result.Trim()}. The room itself is unaffected — "
                    + $"it stays reachable at '{roomDirectoryPath}'.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Console.Error.WriteLine(
                $"Warning: could not create the by-workstream link at '{linkPath}': {ex.Message}. The room "
                + $"itself is unaffected — it stays reachable at '{roomDirectoryPath}'.");
        }
    }
}
