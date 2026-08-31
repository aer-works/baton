using Baton.Core;

namespace Baton.Tests.Core;

/// <summary>
/// Two behaviors named in #1474's port that have no Rust-suite equivalent to port from (aer-core never
/// needed them; the managed <see cref="System.Diagnostics.Process"/> spawn path has different edges),
/// so they are new coverage rather than a mapped-over test:
/// <list type="bullet">
/// <item>Spawn resolution only ever finds <c>name</c>/<c>name.exe</c> on PATH, never a <c>.cmd</c>/
/// <c>.bat</c> shim -- the CVE-2024-24576 stance <c>ClaudeWorkerAdapter</c> and
/// <c>scripts/verify-pack-roundtrip.sh</c> document, previously true because aer-core's Rust
/// <c>Command::new</c> refuses batch-file resolution by design. Empirically verified here that
/// <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute = false</c> preserves the same
/// refusal (Win32's <c>CreateProcess</c> auto-appends <c>.exe</c> to an extension-less name; it never
/// consults <c>PATHEXT</c>, which is a <c>cmd.exe</c>-shell behavior this spawn path does not go
/// through).</item>
/// <item>The child's stdin is closed immediately at spawn, so a child that tries to read it observes
/// EOF rather than blocking -- the managed equivalent of aer-core's native <c>Stdio::null()</c>.</item>
/// </list>
/// </summary>
public class SpawnResolutionTests
{
    /// <summary>
    /// Places a <c>.cmd</c> shim (no same-named <c>.exe</c>) on <c>PATH</c> and proves a bare,
    /// extension-less <see cref="BatonTask"/> spawn does not resolve to it -- it must fail exactly the
    /// way it would if nothing on PATH matched at all.
    /// </summary>
    [Fact]
    public void Run_BareNameWithOnlyCmdShimOnPath_DoesNotResolveAndFailsToSpawn()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"baton_cmdshim_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string shimName = $"baton_test_shim_{Guid.NewGuid():N}";
        string shimPath = Path.Combine(dir, shimName + ".cmd");
        File.WriteAllText(shimPath, "@echo off\r\necho ran the cmd shim\r\n");

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + originalPath);

            using BatonTask task = new(shimName);
            BatonException ex = Assert.Throws<BatonException>(task.Run);
            Assert.Equal(BatonErrorCode.SpawnFailed, ex.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    /// <summary>
    /// The child's stdin must read as EOF immediately rather than block -- proves the spawn path
    /// redirects+closes it up front rather than leaving it connected to something the child could
    /// wait on. <c>findstr</c> reads all of stdin before printing anything, so a hang here would time
    /// the test out rather than fail an assertion softly.
    /// </summary>
    [Fact]
    public void Run_ChildReadingStdin_ObservesImmediateEofRatherThanBlocking()
    {
        List<BatonEventArgs> events = [];
        using BatonTask task = new BatonTask("findstr", "^").WithCaptureOutput().WithTimeout(TimeSpan.FromSeconds(10));
        task.EventRaised += (_, e) => events.Add(e);

        task.Run(); // would throw BatonTimeoutException if stdin were left open/blocking

        Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
    }
}
