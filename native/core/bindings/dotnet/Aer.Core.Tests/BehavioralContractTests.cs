using System.Text;

namespace Aer.Core.Tests;

/// <summary>
/// Integration tests proving the managed <see cref="AerTask"/> wrapper upholds the event-ordering
/// and exit-reason invariants from the behavioral spec (<c>spec/aer-core-behavioral-spec-v1.1.md</c>)
/// end-to-end, exercised exclusively through the managed surface — no direct <c>NativeMethods</c>
/// P/Invoke calls. Mirrors the Rust integration suite's spec assertions (<c>tests/integration_test.rs</c>)
/// through the .NET binding. See <see cref="AerTaskTests"/> for general wrapper unit coverage
/// (config validation, disposal, run-once enforcement, happy-path event shape, etc.); this file
/// focuses on the ordering/reason invariants specifically and does not duplicate what is already
/// covered there.
/// </summary>
public class BehavioralContractTests
{
    private static (string Program, string[] Args) ExitWithCode(int code) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"exit {code}"])
            : ("sh", ["-c", $"exit {code}"]);

    private static (string Program, string[] Args) LongRunning() =>
        OperatingSystem.IsWindows()
            ? ("ping", ["-n", "61", "127.0.0.1"])
            : ("sh", ["-c", "sleep 60"]);

    private static (string Program, string[] Args) EchoStdout(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"echo {text}"])
            : ("sh", ["-c", $"echo {text}"]);

    private static (string Program, string[] Args) EchoStderr(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"echo {text} 1>&2"])
            : ("sh", ["-c", $"echo {text} >&2"]);

    /// <summary>
    /// Ordering invariant #5 (no events after Exited): asserts the collected sequence contains
    /// exactly one <c>Exited</c> event and that it is the last event delivered.
    /// </summary>
    private static void AssertExitedIsLastAndUnique(IReadOnlyList<AerEventArgs> events)
    {
        Assert.NotEmpty(events);
        _ = Assert.Single(events, e => e.Kind == AerTaskEventKind.Exited);
        Assert.Equal(AerTaskEventKind.Exited, events[^1].Kind);
    }

    private static void AssertSeqStrictlyIncreasingFromZero(IEnumerable<AerEventArgs> chunksInStream)
    {
        ulong? prevSeq = null;
        foreach (AerEventArgs chunk in chunksInStream)
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
        List<AerEventArgs> events = [];

        using AerTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        _ = Assert.Single(events, e => e.Kind == AerTaskEventKind.Started);
        AssertExitedIsLastAndUnique(events);

        AerEventArgs started = events.Single(e => e.Kind == AerTaskEventKind.Started);
        Assert.True(started.Pid > 0, "Started.Pid must be > 0");
        Assert.True(events.IndexOf(started) < events.Count - 1, "Started must precede Exited");

        AerEventArgs exited = events[^1];
        Assert.Equal(42, exited.ExitCode);
        Assert.Equal(AerExitReason.Natural, exited.ExitReason);
    }

    // --- Spawn failure: typed exception, no events raised at all ---

    [Fact]
    public void Run_NonexistentBinary_ThrowsTypedExceptionAndRaisesNoEvents()
    {
        List<AerEventArgs> events = [];
        using AerTask task = new("definitely_not_a_real_binary_xyzzy_aer");
        task.EventRaised += (_, e) => events.Add(e);

        AerException ex = Assert.Throws<AerException>(task.Run);

        Assert.Equal(AerErrorCode.SpawnFailed, ex.ErrorCode);
        Assert.Empty(events);
    }

    // --- M2: timeout (AerTimeoutException; Exited present with TimedOut/-1; Started->Exited order) ---

    [Fact]
    public void Run_TimeoutElapses_ExitedEventCarriesTimedOutReasonAndNegativeOneCode()
    {
        (string prog, string[] args) = LongRunning();
        List<AerEventArgs> events = [];

        using AerTask task = new AerTask(prog, args).WithTimeout(TimeSpan.FromMilliseconds(300));
        task.EventRaised += (_, e) => events.Add(e);

        AerTimeoutException ex = Assert.Throws<AerTimeoutException>(task.Run);
        Assert.Equal(AerErrorCode.TimedOut, ex.ErrorCode);

        Assert.Equal(AerTaskEventKind.Started, events[0].Kind);
        AssertExitedIsLastAndUnique(events);

        AerEventArgs exited = events[^1];
        Assert.Equal(-1, exited.ExitCode);
        Assert.Equal(AerExitReason.TimedOut, exited.ExitReason);
    }

    // --- M4: observation tier (chunks between Started/Exited, per-stream seq from 0, bytes reassemble) ---

    [Fact]
    public void Run_CaptureEnabled_StdoutChunksArriveBetweenStartedAndExitedWithIncreasingSeq()
    {
        const string marker = "aer_behavioral_contract_stdout_marker";
        (string prog, string[] args) = EchoStdout(marker);
        List<AerEventArgs> events = [];

        using AerTask task = new AerTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        AssertExitedIsLastAndUnique(events);
        int startedIndex = events.FindIndex(e => e.Kind == AerTaskEventKind.Started);
        int exitedIndex = events.Count - 1;

        List<AerEventArgs> stdoutChunks = [.. events.Where(e => e.Kind == AerTaskEventKind.StdoutChunk)];
        Assert.NotEmpty(stdoutChunks);

        foreach (AerEventArgs chunk in stdoutChunks)
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
        List<AerEventArgs> events = [];

        using AerTask task = new AerTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        AssertExitedIsLastAndUnique(events);
        int startedIndex = events.FindIndex(e => e.Kind == AerTaskEventKind.Started);
        int exitedIndex = events.Count - 1;

        List<AerEventArgs> stderrChunks = [.. events.Where(e => e.Kind == AerTaskEventKind.StderrChunk)];
        Assert.NotEmpty(stderrChunks);

        foreach (AerEventArgs chunk in stderrChunks)
        {
            int i = events.IndexOf(chunk);
            Assert.True(i > startedIndex, "chunk must arrive after Started");
            Assert.True(i < exitedIndex, "chunk must arrive before Exited");
        }

        AssertSeqStrictlyIncreasingFromZero(stderrChunks);

        string output = Encoding.UTF8.GetString([.. stderrChunks.SelectMany(e => e.Data ?? [])]);
        Assert.Contains(marker, output, StringComparison.Ordinal);
    }

    // --- M4: cancellation (AerCancelException; Exited present with CancelRequested; ordering holds) ---

    [Fact]
    public async Task RunAsync_CancelledMidRun_ThrowsAerCancelExceptionAndExitedEventCarriesCancelRequestedReason()
    {
        (string prog, string[] args) = LongRunning();
        List<AerEventArgs> events = [];

        using AerTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        AerCancelException ex = await Assert.ThrowsAsync<AerCancelException>(() => task.RunAsync(cts.Token));
        Assert.Equal(AerErrorCode.Cancelled, ex.ErrorCode);

        Assert.Equal(AerTaskEventKind.Started, events[0].Kind);
        AssertExitedIsLastAndUnique(events);

        AerEventArgs exited = events[^1];
        Assert.Equal(-1, exited.ExitCode);
        Assert.Equal(AerExitReason.CancelRequested, exited.ExitReason);
    }
}
