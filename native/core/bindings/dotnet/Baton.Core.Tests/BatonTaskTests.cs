using System.Text;

namespace Baton.Core.Tests;

/// <summary>
/// Exercises the managed <see cref="BatonTask"/> surface exclusively — no direct <c>NativeMethods</c>
/// P/Invoke calls. See <see cref="NativeAbiTests"/>/<see cref="CallbackMarshallingTests"/>/
/// <see cref="EnvironmentAndWorkingDirectoryTests"/>/<see cref="SafeHandleTests"/> for coverage of the
/// raw P/Invoke layer itself.
/// </summary>
public class BatonTaskTests
{
    private static (string Program, string[] Args) EchoHello() =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "echo", "hello"])
            : ("echo", ["hello"]);

    private static (string Program, string[] Args) ExitZero() =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "exit 0"])
            : ("sh", ["-c", "exit 0"]);

    private static (string Program, string[] Args) LongRunning() =>
        OperatingSystem.IsWindows()
            ? ("ping", ["-n", "61", "127.0.0.1"])
            : ("sh", ["-c", "sleep 60"]);

    private static (string Program, string[] Args) EchoEnvVar(string var) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"echo %{var}%"])
            : ("sh", ["-c", $"echo ${var}"]);

    private static (string Program, string[] Args) PrintCwd() =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "cd"])
            : ("sh", ["-c", "pwd"]);

    private static string DecodeChunks(IEnumerable<BatonEventArgs> events) =>
        Encoding.UTF8.GetString(
            [.. events.Where(e => e.Kind == BatonTaskEventKind.StdoutChunk).SelectMany(e => e.Data ?? [])]);

    [Fact]
    public void Constructor_NullProgram_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new BatonTask(null!));
    }

    [Fact]
    public void Constructor_NullArgs_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new BatonTask("cmd", null!));
    }

    [Fact]
    public void Constructor_TooManyArgs_ThrowsAerException()
    {
        string[] tooManyArgs = [.. Enumerable.Repeat("x", 65_537)];

        BatonException ex = Assert.Throws<BatonException>(() => new BatonTask("cmd", tooManyArgs));
        Assert.Equal(BatonErrorCode.NullPointer, ex.ErrorCode);
    }

    [Fact]
    public void WithEnv_EmptyKey_ThrowsAerExceptionWithInvalidArgument()
    {
        using BatonTask task = new(OperatingSystem.IsWindows() ? "cmd" : "echo");

        BatonException ex = Assert.Throws<BatonException>(() => task.WithEnv(string.Empty, "value"));
        Assert.Equal(BatonErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithEnv_KeyContainingEquals_ThrowsAerExceptionWithInvalidArgument()
    {
        using BatonTask task = new(OperatingSystem.IsWindows() ? "cmd" : "echo");

        BatonException ex = Assert.Throws<BatonException>(() => task.WithEnv("BAD=KEY", "value"));
        Assert.Equal(BatonErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithCwd_EmptyPath_ThrowsAerExceptionWithInvalidArgument()
    {
        using BatonTask task = new(OperatingSystem.IsWindows() ? "cmd" : "echo");

        BatonException ex = Assert.Throws<BatonException>(() => task.WithCwd(string.Empty));
        Assert.Equal(BatonErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithCwd_InvalidDirectory_RunThrowsAerExceptionWithSpawnFailed()
    {
        (string prog, string[] args) = ExitZero();
        using BatonTask task = new BatonTask(prog, args).WithCwd("definitely_not_a_real_directory_xyzzy_aer");

        BatonException ex = Assert.Throws<BatonException>(task.Run);
        Assert.Equal(BatonErrorCode.SpawnFailed, ex.ErrorCode);
    }

    [Fact]
    public void Run_CalledTwice_ThrowsInvalidOperationException()
    {
        (string prog, string[] args) = EchoHello();
        using BatonTask task = new(prog, args);

        task.Run();

        _ = Assert.Throws<InvalidOperationException>(task.Run);
    }

    [Fact]
    public void Run_HappyPath_RaisesStartedThenChunksThenExited()
    {
        (string prog, string[] args) = EchoHello();
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        Assert.NotEmpty(events);
        Assert.Equal(BatonTaskEventKind.Started, events[0].Kind);
        Assert.Equal(BatonTaskEventKind.Exited, events[^1].Kind);

        int startedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Started);
        int exitedIndex = events.FindIndex(e => e.Kind == BatonTaskEventKind.Exited);
        foreach (int chunkIndex in events
            .Select((e, i) => (e, i))
            .Where(t => t.e.Kind == BatonTaskEventKind.StdoutChunk)
            .Select(t => t.i))
        {
            Assert.True(chunkIndex > startedIndex, "chunk must arrive after Started");
            Assert.True(chunkIndex < exitedIndex, "chunk must arrive before Exited");
        }

        BatonEventArgs exited = events[exitedIndex];
        Assert.Equal(0, exited.ExitCode);
        Assert.Equal(BatonExitReason.Natural, exited.ExitReason);

        string output = DecodeChunks(events);
        Assert.Contains("hello", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_TimeoutElapses_ThrowsAerTimeoutException()
    {
        (string prog, string[] args) = LongRunning();
        using BatonTask task = new BatonTask(prog, args).WithTimeout(TimeSpan.FromMilliseconds(300));

        BatonTimeoutException ex = Assert.Throws<BatonTimeoutException>(task.Run);
        Assert.Equal(BatonErrorCode.TimedOut, ex.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_CancelledViaCancellationToken_ThrowsAerCancelException()
    {
        (string prog, string[] args) = LongRunning();
        using BatonTask task = new(prog, args);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        BatonCancelException ex = await Assert.ThrowsAsync<BatonCancelException>(() => task.RunAsync(cts.Token));
        Assert.Equal(BatonErrorCode.Cancelled, ex.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_NotCancelled_CompletesNormally()
    {
        (string prog, string[] args) = EchoHello();
        List<BatonEventArgs> events = [];
        using BatonTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        await task.RunAsync();

        Assert.Contains(events, e => e.Kind == BatonTaskEventKind.Exited && e.ExitCode == 0);
    }

    [Fact]
    public void WithEnv_MakesVariableVisibleToChild()
    {
        (string prog, string[] args) = EchoEnvVar("BATON_DOTNET_MANAGED_TEST_VAR");
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args)
            .WithCaptureOutput()
            .WithEnv("BATON_DOTNET_MANAGED_TEST_VAR", "hello_from_managed_wrapper");
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        string output = DecodeChunks(events);
        Assert.Contains("hello_from_managed_wrapper", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithCwd_ChangesChildWorkingDirectory()
    {
        string targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        (string prog, string[] args) = PrintCwd();
        List<BatonEventArgs> events = [];

        using BatonTask task = new BatonTask(prog, args).WithCaptureOutput().WithCwd(targetDir);
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        string output = DecodeChunks(events);
        string? printedLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        Assert.NotNull(printedLine);

        string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(printedLine.Trim()));
        Assert.Equal(targetDir, actual, ignoreCase: OperatingSystem.IsWindows());
    }
}
