using System.Text;
using Baton.Core;

namespace Baton.Tests.Core;

/// <summary>
/// Proves the managed <see cref="BatonTask"/> upholds the event-ordering and exit-reason invariants
/// from the behavioral spec that used to be <c>native/core/spec/aer-core-behavioral-spec-v1.1.md</c>
/// (folded into <c>spec/baton.md</c>'s history; the invariants themselves are unchanged by #1474's
/// port). Ported from the deleted aer-core .NET binding's own
/// <c>Baton.Core.Tests\BehavioralContractTests.cs</c>, itself written to mirror the Rust integration
/// suite's spec assertions (<c>tests/integration_test.rs</c>, also deleted with <c>native/core</c>) —
/// Windows-only now (spec/baton.md C-10). See <see cref="BatonTaskTests"/> for general wrapper unit
/// coverage; this file focuses on the ordering/reason invariants specifically.
/// </summary>
public class BehavioralContractTests
{
    private static (string Program, string[] Args) ExitWithCode(int code) => ("cmd", ["/c", $"exit {code}"]);

    private static (string Program, string[] Args) LongRunning() => ("ping", ["-n", "61", "127.0.0.1"]);

    private static (string Program, string[] Args) EchoStdout(string text) => ("cmd", ["/c", $"echo {text}"]);

    private static (string Program, string[] Args) EchoStderr(string text) => ("cmd", ["/c", $"echo {text} 1>&2"]);

    /// <summary>
    /// Ordering invariant #5 (no events after Exited): asserts the collected sequence contains
    /// exactly one <c>Exited</c> event and that it is the last event delivered.
    /// </summary>
    private static void AssertExitedIsLastAndUnique(IReadOnlyList<BatonEventArgs> events)
    {
        Assert.NotEmpty(events);
        _ = Assert.Single(events, e => e.Kind == BatonTaskEventKind.Exited);
        Assert.Equal(BatonTaskEventKind.Exited, events[^1].Kind);
    }

    private static void AssertSeqStrictlyIncreasingFromZero(IEnumerable<BatonEventArgs> chunksInStream)
    {
        ulong? prevSeq = null;
        foreach (BatonEventArgs chunk in chunksInStream)
        {
            if (prevSeq is { } prev)
            {
                Assert.True(chunk.Seq > prev, $"seq must be strictly increasing: got {chunk.Seq} after {prev}");
            }
            else
            {
                Assert.Equal(0UL, chunk.Seq);
            }

            prevSeq = chunk.Seq;
        }
    }

    // --- M1: lifecycle (Started precedes Exited; exactly one of each; pid > 0; exit code propagated) ---

    [Fact]
    public void Run_NaturalExit_EmitsExactlyOneStartedThenOneExitedWithPropagatedCode()
    {
        (string prog, string[] args) = ExitWithCode(42);
        List<BatonEventArgs> events = [];

        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        _ = Assert.Single(events, e => e.Kind == BatonTaskEventKind.Started);
        AssertExitedIsLastAndUnique(events);

        BatonEventArgs started = events.Single(e => e.Kind == BatonTaskEventKind.Started);
        Assert.True(started.Pid > 0, "Started.Pid must be > 0");
        Assert.True(events.IndexOf(started) < events.Count - 1, "Started must precede Exited");

        BatonEventArgs exited = events[^1];
        Assert.Equal(42, exited.ExitCode);
        Assert.Equal(BatonExitReason.Natural, exited.ExitReason);
    }

    /// <summary>Rust equivalent: <c>large_output_does_not_deadlock</c> (pipe-buffer deadlock guard).</summary>
    [Fact]
    public void Run_LargeOutput_DoesNotDeadlock()
    {
        (string prog, string[] args) = ("cmd", ["/c", "for /L %i in (1,1,1000) do @echo line %i"]);
        List<BatonEventArgs> events = [];

        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        AssertExitedIsLastAndUnique(events);
        Assert.Equal(0, events[^1].ExitCode);
    }

    // --- Spawn failure: typed exception, no events raised at all ---

    [Fact]
    public void Run_NonexistentBinary_ThrowsTypedExceptionAndRaisesNoEvents()
    {
        List<BatonEventArgs> events = [];
        using BatonTask task = new("definitely_not_a_real_binary_xyzzy_aer");
        task.EventRaised += (_, e) => events.Add(e);

        BatonException ex = Assert.Throws<BatonException>(task.Run);

        Assert.Equal(BatonErrorCode.SpawnFailed, ex.ErrorCode);
        Assert.Empty(events);
    }

    // --- M2: timeout (BatonTimeoutException; Exited present with TimedOut/-1; Started->Exited order) ---

    [Fact]
    public void Run_TimeoutElapses_ExitedEventCarriesTimedOutReasonAndNegativeOneCode()
    {
        (string prog, string[] args) = LongRunning();
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithTimeout(TimeSpan.FromMilliseconds(300));
        task.EventRaised += (_, e) => events.Add(e);

        BatonTimeoutException ex = Assert.Throws<BatonTimeoutException>(task.Run);
        Assert.Equal(BatonErrorCode.TimedOut, ex.ErrorCode);

        Assert.Equal(BatonTaskEventKind.Started, events[0].Kind);
        AssertExitedIsLastAndUnique(events);

        BatonEventArgs exited = events[^1];
        Assert.Equal(-1, exited.ExitCode);
        Assert.Equal(BatonExitReason.TimedOut, exited.ExitReason);
    }

    /// <summary>Rust equivalent: <c>timeout_does_not_fire_for_fast_process</c>.</summary>
    [Fact]
    public void Run_FastProcessWithLongTimeout_CompletesNormallyWithoutTimingOut()
    {
        (string prog, string[] args) = ExitWithCode(0);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithTimeout(TimeSpan.FromSeconds(30));
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        Assert.Equal(0, events[^1].ExitCode);
        Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
    }

    // --- M4: observation tier (chunks between Started/Exited, per-stream seq from 0, bytes reassemble) ---

    [Fact]
    public void Run_CaptureEnabled_StdoutChunksArriveBetweenStartedAndExitedWithIncreasingSeq()
    {
        const string marker = "aer_behavioral_contract_stdout_marker";
        (string prog, string[] args) = EchoStdout(marker);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        AssertExitedIsLastAndUnique(events);
        int startedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Started);
        int exitedIndex = events.Count - 1;

        List<BatonEventArgs> stdoutChunks = [.. events.Where(e => e.Kind == BatonTaskEventKind.StdoutChunk)];
        Assert.NotEmpty(stdoutChunks);

        foreach (BatonEventArgs chunk in stdoutChunks)
        {
            int i = events.IndexOf(chunk);
            Assert.True(i > startedIndex, "chunk must arrive after Started");
            Assert.True(i < exitedIndex, "chunk must arrive before Exited");
        }

        AssertSeqStrictlyIncreasingFromZero(stdoutChunks);

        string output = Encoding.UTF8.GetString([.. stdoutChunks.SelectMany(e => e.Data ?? [])]);
        Assert.Contains(marker, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_CaptureEnabled_StderrChunksArriveBetweenStartedAndExitedWithIncreasingSeq()
    {
        const string marker = "aer_behavioral_contract_stderr_marker";
        (string prog, string[] args) = EchoStderr(marker);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        AssertExitedIsLastAndUnique(events);
        int startedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Started);
        int exitedIndex = events.Count - 1;

        List<BatonEventArgs> stderrChunks = [.. events.Where(e => e.Kind == BatonTaskEventKind.StderrChunk)];
        Assert.NotEmpty(stderrChunks);

        foreach (BatonEventArgs chunk in stderrChunks)
        {
            int i = events.IndexOf(chunk);
            Assert.True(i > startedIndex, "chunk must arrive after Started");
            Assert.True(i < exitedIndex, "chunk must arrive before Exited");
        }

        AssertSeqStrictlyIncreasingFromZero(stderrChunks);

        string output = Encoding.UTF8.GetString([.. stderrChunks.SelectMany(e => e.Data ?? [])]);
        Assert.Contains(marker, output, StringComparison.Ordinal);
    }

    /// <summary>Rust equivalent: <c>capture_output_off_emits_no_chunks</c>.</summary>
    [Fact]
    public void Run_CaptureDisabled_EmitsNoChunkEvents()
    {
        (string prog, string[] args) = ("cmd", ["/c", "for /L %i in (1,1,100) do @echo line %i"]);
        List<BatonEventArgs> events = [];

        using BatonTask task = new(prog, args); // no WithCaptureOutput
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        Assert.DoesNotContain(events, e => e.Kind is BatonTaskEventKind.StdoutChunk or BatonTaskEventKind.StderrChunk);
        Assert.Equal(2, events.Count);
    }

    /// <summary>
    /// Rust equivalent: <c>capture_delivers_chunks_while_process_is_alive</c> (#72 regression) — chunks
    /// must be delivered live as bytes arrive, not buffered until the process exits.
    /// </summary>
    [Fact]
    public void Run_CaptureEnabled_DeliversChunksWhileProcessIsStillAlive()
    {
        (string prog, string[] args) = ("cmd", ["/c", "echo hello & ping -n 4 127.0.0.1 >nul"]);
        List<(DateTime Time, BatonEventArgs Args)> timestamps = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        DateTime start = DateTime.UtcNow;
        task.EventRaised += (_, e) => timestamps.Add((DateTime.UtcNow, e));

        task.Run();

        DateTime exitedAt = timestamps.Single(t => t.Args.Kind == BatonTaskEventKind.Exited).Time;
        List<DateTime> stdoutChunkTimes = [.. timestamps
            .Where(t => t.Args.Kind == BatonTaskEventKind.StdoutChunk)
            .Select(t => t.Time)];
        Assert.NotEmpty(stdoutChunkTimes);

        TimeSpan total = exitedAt - start;
        Assert.True(total >= TimeSpan.FromMilliseconds(2500), $"process exited too quickly ({total}) for this test to be meaningful");

        TimeSpan gap = exitedAt - stdoutChunkTimes[0];
        Assert.True(gap >= TimeSpan.FromMilliseconds(1500), $"first stdout chunk arrived only {gap} before Exited -- chunks are not being delivered live");

        Assert.All(timestamps.Where(t => t.Args.Kind is BatonTaskEventKind.StdoutChunk or BatonTaskEventKind.StderrChunk),
            t => Assert.True(t.Time <= exitedAt, "chunk timestamped after Exited"));
    }

    /// <summary>
    /// Rust equivalent: <c>capture_output_with_timeout_delivers_chunks_then_timed_out_exit</c> (#79) --
    /// chunks emitted before a timeout kill must still be delivered, not discarded.
    /// <para>
    /// #1588: the child must be one that CANNOT finish on its own (<c>ping -n 61</c>, ~60s), because
    /// what this pins is what a <em>timeout kill</em> does to already-emitted chunks -- so the timeout
    /// has to be the thing that ends the run, on every machine. The former child (<c>ping -n 4</c>,
    /// ~3s) left only a 2.5s margin over the deadline, and under build-lock contention that margin
    /// closed in both directions. Both were seen on 2026-09-01, within an hour of each other, and
    /// naming which was seen where matters -- a future reader uses this to tell a timer-starvation
    /// recurrence from a delivery one:
    /// <list type="bullet">
    /// <item><c>Assert.Throws</c> ("no exception was thrown") -- <b>on <c>main</c>, in CI.</b> The
    /// child finished naturally before a starved timer fired, so the timeout kill this test exists to
    /// observe never happened at all.</item>
    /// <item><c>Assert.NotEmpty</c> ("collection was empty") -- <b>locally</b>, under
    /// <c>pixi run gates-quiet</c> with concurrent lanes holding the build lock. No stdout chunk had
    /// been delivered by the time the deadline fired.</item>
    /// </list>
    /// The second is recorded as an observation, deliberately without a mechanism: <c>echo hello</c>
    /// is the only stdout write in this run (<c>&gt;nul</c> binds to <c>ping</c> alone), so either it
    /// had not been written yet -- <c>cmd.exe</c> not having reached <c>echo</c> within 500ms under
    /// contention -- or it was written and dropped. Reading <c>BatonProcessRunner</c>'s drain path
    /// makes the second look impossible, and if it ever were true a longer deadline would <em>hide</em>
    /// a <c>Baton.Core</c> defect rather than fix it. So the claim here stops at what was measured.
    /// The deadline is 2s rather than 500ms because it must comfortably exceed how long a
    /// <em>loaded</em> machine takes to produce and deliver that chunk, which is this test's premise
    /// rather than its subject. Neither number weakens what is asserted -- a build that failed to
    /// deliver chunks from a killed run still fails <c>Assert.NotEmpty</c>, and one that delivered
    /// them after <c>Exited</c> still fails the ordering check.
    /// </para>
    /// </summary>
    [Fact]
    public void Run_CaptureEnabledWithTimeout_DeliversChunksThenTimedOutExit()
    {
        (string prog, string[] args) = ("cmd", ["/c", "echo hello & ping -n 61 127.0.0.1 >nul"]);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput().WithTimeout(TimeSpan.FromSeconds(2));
        task.EventRaised += (_, e) => events.Add(e);

        BatonTimeoutException ex = Assert.Throws<BatonTimeoutException>(task.Run);
        Assert.Equal(BatonErrorCode.TimedOut, ex.ErrorCode);

        int exitedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Exited);
        Assert.True(exitedIndex >= 0);
        Assert.Equal(-1, events[exitedIndex].ExitCode);
        Assert.Equal(BatonExitReason.TimedOut, events[exitedIndex].ExitReason);

        List<int> stdoutPositions = [.. events
            .Select((e, i) => (e, i))
            .Where(t => t.e.Kind == BatonTaskEventKind.StdoutChunk)
            .Select(t => t.i)];
        Assert.NotEmpty(stdoutPositions);
        Assert.All(stdoutPositions, i => Assert.True(i < exitedIndex));
    }

    // --- M4: cancellation (BatonCancelException; Exited present with CancelRequested; ordering holds) ---

    [Fact]
    public async Task RunAsync_CancelledMidRun_ThrowsBatonCancelExceptionAndExitedEventCarriesCancelRequestedReason()
    {
        (string prog, string[] args) = LongRunning();
        List<BatonEventArgs> events = [];

        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        BatonCancelException ex = await Assert.ThrowsAsync<BatonCancelException>(() => task.RunAsync(cts.Token));
        Assert.Equal(BatonErrorCode.Cancelled, ex.ErrorCode);

        Assert.Equal(BatonTaskEventKind.Started, events[0].Kind);
        AssertExitedIsLastAndUnique(events);

        BatonEventArgs exited = events[^1];
        Assert.Equal(-1, exited.ExitCode);
        Assert.Equal(BatonExitReason.CancelRequested, exited.ExitReason);
    }

    /// <summary>
    /// Rust equivalent: <c>capture_output_with_cancel_delivers_chunks_then_cancel_requested_exit</c>
    /// (#79) -- chunks emitted before a cancel kill must still be delivered.
    /// <para>
    /// #1588: same reasoning as <see cref="Run_CaptureEnabledWithTimeout_DeliversChunksThenTimedOutExit"/>,
    /// with cancellation in place of the deadline -- the child cannot be allowed to finish on its own,
    /// or the run ends without the cancel kill this exists to observe. Swept here rather than left for
    /// the next red build: this case had not failed yet in CI, but it is the identical race, and fixing
    /// only the one that happened to go red first is how the flake returns under a different name.
    /// </para>
    /// <para>
    /// The 300ms cancel became 2s for a reason worth recording, because it was measured rather than
    /// assumed: with only the child lengthened, this case failed <c>Assert.NotEmpty</c> on the very
    /// next run. 300ms is not reliably enough, on a loaded machine, for this run's single stdout write
    /// to be produced and delivered -- so the cancel was firing before the delivery this test exists to
    /// observe could happen, and the short child had been masking it by making the whole run short
    /// enough to look deliberate. Same premise-versus-subject split as the timeout case: that a chunk
    /// exists before the kill is the setup, and only what happens to it afterwards is the assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_CaptureEnabledCancelledMidRun_DeliversChunksThenCancelRequestedExit()
    {
        (string prog, string[] args) = ("cmd", ["/c", "echo hello & ping -n 61 127.0.0.1 >nul"]);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        BatonCancelException ex = await Assert.ThrowsAsync<BatonCancelException>(() => task.RunAsync(cts.Token));
        Assert.Equal(BatonErrorCode.Cancelled, ex.ErrorCode);

        int exitedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Exited);
        Assert.True(exitedIndex >= 0);
        Assert.Equal(BatonExitReason.CancelRequested, events[exitedIndex].ExitReason);

        List<int> stdoutPositions = [.. events
            .Select((e, i) => (e, i))
            .Where(t => t.e.Kind == BatonTaskEventKind.StdoutChunk)
            .Select(t => t.i)];
        Assert.NotEmpty(stdoutPositions);
        Assert.All(stdoutPositions, i => Assert.True(i < exitedIndex));
    }

    /// <summary>Rust equivalent: <c>cancel_after_exit_is_noop</c> (#73 regression).</summary>
    [Fact]
    public async Task RunAsync_TokenCancelledAfterNaturalExit_DoesNotChangeReportedOutcome()
    {
        (string prog, string[] args) = ExitWithCode(0);
        List<BatonEventArgs> events = [];
        using CancellationTokenSource cts = new();

        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        await task.RunAsync(cts.Token);

        Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
        Assert.Equal(0, events[^1].ExitCode);

        // The process has already exited; cancelling now must be observably a no-op.
        cts.Cancel();
        Assert.Equal(BatonExitReason.Natural, events[^1].ExitReason);
        Assert.Equal(0, events[^1].ExitCode);
    }
}
