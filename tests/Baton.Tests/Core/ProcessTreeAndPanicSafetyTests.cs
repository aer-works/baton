using System.Diagnostics;
using Baton.Core;

namespace Baton.Tests.Core;

/// <summary>
/// Ports the process-tree (M3) and panic-safety (#75) coverage from the deleted aer-core Rust
/// integration suite (<c>native/core/tests/integration_test.rs</c>) onto the managed Job Object
/// implementation (#1474). These are the tests that actually exercise the no-orphans guarantee
/// (aer-core's Ordering Invariant 7) rather than merely asserting on the exit-code/reason shape --
/// they spawn a grandchild that outlives its immediate parent and prove the whole tree, not just the
/// root, is dead once <see cref="BatonTask.Run"/> returns.
/// </summary>
public class ProcessTreeAndPanicSafetyTests
{
    /// <summary>
    /// A command that spawns a long-lived grandchild (via PowerShell's <c>Start-Process</c>, itself
    /// spawned by <c>cmd</c>), writes its PID to <paramref name="pidFile"/>, then exits immediately.
    /// Mirrors the Rust suite's <c>orphan_cmd</c> helper.
    /// </summary>
    private static (string Program, string[] Args) OrphanCmd(string pidFile) => (
        "powershell",
        [
            "-NoProfile",
            "-Command",
            "$p = Start-Process -PassThru -NoNewWindow -FilePath ping " +
            $"-ArgumentList @('-n','9999','127.0.0.1'); $p.Id | Out-File -FilePath '{pidFile}' -Encoding ascii",
        ]);

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int ReadGrandchildPid(string pidFile)
    {
        string raw = File.ReadAllText(pidFile);
        return int.Parse(raw.Trim());
    }

    /// <summary>Rust equivalent: <c>process_tree_is_cleaned_up</c>.</summary>
    [Fact]
    public void Run_GrandchildOutlivesRoot_TreeIsFullyCleanedUpOnReturn()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"baton_test_grandchild_pid_{Guid.NewGuid():N}.txt");
        try
        {
            (string prog, string[] args) = OrphanCmd(pidFile);
            using BatonTask task = new(prog, args);

            DateTime start = DateTime.UtcNow;
            task.Run();
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Run() took {elapsed} -- process tree cleanup looks deadlocked");

            // wait-ok: a fixed OS-settle delay after Run()'s synchronous teardown, not a poll ceiling.
            Thread.Sleep(200);

            int grandchildPid = ReadGrandchildPid(pidFile);
            Assert.False(ProcessIsAlive(grandchildPid), $"grandchild PID {grandchildPid} is still alive -- process tree cleanup failed");
        }
        finally
        {
            FileCleanup.Delete(pidFile);
        }
    }

    /// <summary>
    /// Regression coverage mirroring Rust's <c>timeout_with_process_tree_reports_natural_exit</c> (#71):
    /// a generous timeout must not defeat process-tree cleanup at root-exit -- the timeout monitor
    /// holding its own reference to the job object must not keep the tree alive until the deadline.
    /// </summary>
    [Fact]
    public void Run_TimeoutSetButProcessExitsNaturally_ReturnsPromptlyWithNaturalExit()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"baton_test_grandchild_pid_timeout_{Guid.NewGuid():N}.txt");
        try
        {
            (string prog, string[] args) = OrphanCmd(pidFile);
            List<BatonEventArgs> events = [];
            using BatonTask task = new BatonTask(prog, args).WithTimeout(TimeSpan.FromSeconds(30));
            task.EventRaised += (_, e) => events.Add(e);

            DateTime start = DateTime.UtcNow;
            task.Run();
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(10),
                $"Run() took {elapsed} -- the timeout monitor's job reference is blocking cleanup until the 30s deadline instead of NaturalExit firing promptly");
            Assert.Equal(0, events[^1].ExitCode);
            Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
        }
        finally
        {
            FileCleanup.Delete(pidFile);
        }
    }

    /// <summary>
    /// Regression coverage mirroring Rust's <c>cancel_handle_with_process_tree_returns_promptly</c>
    /// (#71): an observed-but-never-fired <see cref="CancellationToken"/> registration must not defeat
    /// process-tree cleanup at root-exit (a circular wait if cleanup relied on every reference being
    /// dropped rather than an explicit terminate-at-wait-return).
    /// </summary>
    [Fact]
    public async Task RunAsync_CancellableTokenNeverCancelled_ReturnsPromptlyWithNaturalExit()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"baton_test_grandchild_pid_cancel_{Guid.NewGuid():N}.txt");
        try
        {
            (string prog, string[] args) = OrphanCmd(pidFile);
            List<BatonEventArgs> events = [];
            using BatonTask task = new(prog, args);
            task.EventRaised += (_, e) => events.Add(e);
            using CancellationTokenSource cts = new(); // cancellable, but never cancelled

            DateTime start = DateTime.UtcNow;
            await task.RunAsync(cts.Token);
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"RunAsync() took {elapsed} -- looks like a circular wait");
            Assert.Equal(0, events[^1].ExitCode);
            Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
        }
        finally
        {
            FileCleanup.Delete(pidFile);
        }
    }

    /// <summary>Rust equivalent: <c>capture_output_with_process_tree_returns_promptly_with_natural_exit</c> (#79).</summary>
    [Fact]
    public void Run_CaptureEnabledWithProcessTree_ReturnsPromptlyWithNaturalExit()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), $"baton_test_grandchild_pid_capture_{Guid.NewGuid():N}.txt");
        try
        {
            (string prog, string[] args) = OrphanCmd(pidFile);
            List<BatonEventArgs> events = [];
            using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
            task.EventRaised += (_, e) => events.Add(e);

            DateTime start = DateTime.UtcNow;
            task.Run();
            TimeSpan elapsed = DateTime.UtcNow - start;

            Assert.True(elapsed < TimeSpan.FromSeconds(10),
                $"Run() took {elapsed} -- the capture path's live-delivery loop may be deadlocked on the grandchild holding stdout/stderr open");
            Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
            Assert.Equal(0, events[^1].ExitCode);

            int exitedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Exited);
            Assert.All(
                events.Select((e, i) => (e, i)).Where(t => t.e.Kind is BatonTaskEventKind.StdoutChunk or BatonTaskEventKind.StderrChunk),
                t => Assert.True(t.i < exitedIndex));
        }
        finally
        {
            FileCleanup.Delete(pidFile);
        }
    }

    /// <summary>
    /// Rust equivalent: <c>panicking_callback_does_not_orphan_the_process</c> (#75 regression). Before
    /// the fix, an exception thrown out of the caller's event callback unwound with the process tree
    /// still armed and no cleanup. The internal process runner's armed/disarmed guard (mirroring
    /// aer-core's <c>KillOnDropGuard</c>) must kill the tree from its <c>finally</c> when still armed
    /// at unwind time.
    /// </summary>
    [Fact]
    public void Run_EventHandlerThrows_StillKillsTheProcess()
    {
        (string prog, string[] args) = ("ping", ["-n", "31", "127.0.0.1"]);
        using BatonTask task = new(prog, args);

        int recordedPid = 0;
        task.EventRaised += (_, e) =>
        {
            if (e.Kind == BatonTaskEventKind.Started)
            {
                recordedPid = (int)e.Pid;
                throw new InvalidOperationException("simulated callback failure (#75 regression test)");
            }
        };

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(task.Run);
        Assert.Contains("simulated callback failure", thrown.Message, StringComparison.Ordinal);
        Assert.True(recordedPid > 0, "pid was not recorded before the callback threw");

        // Polls for the OS to finish tearing the process down after a synchronous Terminate() call
        // already fired inside Run()'s finally -- not a flake-prone wait for an unrelated async
        // event, so a short poll interval and ceiling are the honest ones.
        DateTime start = DateTime.UtcNow;
        while (ProcessIsAlive(recordedPid) && DateTime.UtcNow - start < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(100); // wait-ok: poll interval for the teardown check above, not a flaky wait
        }

        Assert.False(ProcessIsAlive(recordedPid), $"process {recordedPid} is still alive after the callback threw -- the process tree was orphaned");
    }
}
