using System.Diagnostics;
using Baton.Flow.CrashTestHost;
using Baton.Flow.Domain;
using Baton.Flow.Store;

namespace Baton.Flow.Tests.TestSupport;

/// <summary>
/// Launches <c>Baton.Flow.CrashTestHost</c> as a real, separate OS process and drives the two
/// operations every M10 Phase 4 crash-window test needs around it: waiting for a specific durable
/// fact to appear in the log it is writing, and killing it outright once that fact is observed.
/// </summary>
internal static class CrashTestHostLauncher
{
    // 60s, not a tight bound: this is a failure ceiling, not a fixed wait. A crash test returns the
    // instant its predicate is met (WaitForLogConditionAsync) or the host exits (WaitForExitAsync),
    // so a generous ceiling never slows the passing path -- it only stops false timeouts when a
    // loaded CI runner is slow to spawn/run the crash-host process. See issue #420.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Resolved via the host project's own <see cref="Scenarios"/> type rather than a hardcoded
    /// relative path: since <c>Baton.Flow.Tests</c> references <c>Baton.Flow.CrashTestHost</c> as a
    /// <c>ProjectReference</c>, MSBuild has already copied its built assembly next to this test
    /// assembly, and this is exactly the path it copied it to, in any build configuration.
    /// </summary>
    public static string HostDllPath { get; } = typeof(Scenarios).Assembly.Location;

    public static Process Launch(
        string pausePoint, string roomDirectory, string artifactsRoot, string logPath, string pauseSignalPath, string cancelSignalPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(HostDllPath);
        startInfo.ArgumentList.Add(pausePoint);
        startInfo.ArgumentList.Add(roomDirectory);
        startInfo.ArgumentList.Add(artifactsRoot);
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add(pauseSignalPath);
        startInfo.ArgumentList.Add(cancelSignalPath);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the crash test host process.");
    }

    /// <summary>
    /// Kills <paramref name="process"/> outright — never a graceful shutdown — and waits for the OS
    /// to finish tearing it down, so the concurrency guard it held is reliably released (the
    /// guard is a kernel-held file lock the OS releases the instant its owning process exits,
    /// crashed or not) before a caller tries to acquire it again.
    /// </summary>
    public static async Task KillAndWaitAsync(Process process)
    {
        process.Kill();
        await process.WaitForExitAsync(new CancellationTokenSource(DefaultTimeout).Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls <paramref name="logPath"/> until <paramref name="predicate"/> is satisfied against a
    /// fresh read of it, or throws once <paramref name="timeout"/> (defaulting to 15s) elapses.
    /// Reads concurrently with the host process still writing to the same file — safe, since
    /// <see cref="FlowEventLogReader"/> already tolerates a torn trailing line and only ever
    /// reports what is completely and durably written.
    /// </summary>
    public static async Task WaitForLogConditionAsync(
        string logPath, Func<EventLogSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var reader = new FlowEventLogReader(logPath);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            var snapshot = await reader.ReadSnapshotAsync().ConfigureAwait(false);
            if (predicate(snapshot))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for the expected condition in '{logPath}'.");
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort cleanup for a real orphaned child a crash test's killed run may have left
    /// running. <paramref name="processId"/> is the PID <see cref="CoreEvent.ExecutionStarted"/>
    /// recorded for aer-core's immediate child — on Unix that process called <c>setsid()</c>
    /// (aer-core's process-tree containment), making it its own process group leader, so a shell
    /// worker command like <c>sh -c "sleep 120"</c> that forks rather than execs directly (as this
    /// platform's <c>/bin/sh</c> does) leaves a grandchild sharing that same PGID — killing only
    /// <paramref name="processId"/> itself would leave that grandchild running. Killing the whole
    /// process group (a negative PID in POSIX <c>kill</c>) takes down the leader and every
    /// descendant sharing its group in one call, mirroring aer-core's own <c>killpg</c>-based
    /// cleanup rather than reimplementing tree discovery here.
    /// </summary>
    public static void TryKillOrphanedChild(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No POSIX process-group equivalent; Windows' own Job Object containment already
                // reliably took the whole tree down alongside the killed host process (see
                // CrashRecoveryEndToEndTests' orphan test remarks), so this is expected to be a
                // no-op here, not a real cleanup path.
                Process.GetProcessById(processId).Kill();
                return;
            }

            using var killProcessGroup = Process.Start(new ProcessStartInfo("kill", ["-9", "--", $"-{processId}"])
            {
                UseShellExecute = false,
            });
            killProcessGroup?.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Exited between GetProcessById and Kill.
        }
    }
}
