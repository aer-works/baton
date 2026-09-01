using System.Diagnostics;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// Shared by every <c>Baton.Cli.Tests</c> fixture that needs a genuinely-dead process identity for
/// <see cref="Baton.Outcomes.EngineLivenessProbe"/>: capture a real process's pid and start time
/// while it is provably alive, then kill it, so the probe's OS-level checks (start-time match,
/// <c>HasExited</c>) see an OS-confirmed-dead PID rather than a fabricated one that might
/// coincidentally collide with something else running on the host. On Linux <see cref="Process.StartTime"/>
/// is a live read of <c>/proc/&lt;pid&gt;/stat</c>, so an immediately-exiting child (<c>true</c>) races
/// the read and intermittently kills the test with a <c>Win32Exception</c> before any assertion (#843,
/// measured on PR #841's CI) — a long-sleeping child killed after capture is deterministic.
/// </summary>
public static class ProcessIdentityFixture
{
    public static (int Pid, DateTimeOffset StartTime) DeadProcessIdentity()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "30") { CreateNoWindow = true };

        using var process = Process.Start(psi)!;
        try
        {
            return (process.Id, new DateTimeOffset(process.StartTime).ToUniversalTime());
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }
}
