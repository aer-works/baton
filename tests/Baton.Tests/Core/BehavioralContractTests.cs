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
    /// </summary>
    [Fact]
    public void Run_CaptureEnabledWithTimeout_DeliversChunksThenTimedOutExit()
    {
        (string prog, string[] args) = ("cmd", ["/c", "echo hello & ping -n 4 127.0.0.1 >nul"]);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput().WithTimeout(TimeSpan.FromMilliseconds(500));
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
    /// </summary>
    [Fact]
    public async Task RunAsync_CaptureEnabledCancelledMidRun_DeliversChunksThenCancelRequestedExit()
    {
        (string prog, string[] args) = ("cmd", ["/c", "echo hello & ping -n 4 127.0.0.1 >nul"]);
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

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
