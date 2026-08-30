using System.Text;

namespace Aer.Core.Tests;

/// <summary>
/// Exercises the managed <see cref="AerTask"/> surface exclusively — no direct <c>NativeMethods</c>
/// P/Invoke calls. See <see cref="NativeAbiTests"/>/<see cref="CallbackMarshallingTests"/>/
/// <see cref="EnvironmentAndWorkingDirectoryTests"/>/<see cref="SafeHandleTests"/> for coverage of the
/// raw P/Invoke layer itself.
/// </summary>
public class AerTaskTests
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

    private static string DecodeChunks(IEnumerable<AerEventArgs> events) =>
        Encoding.UTF8.GetString(
            [.. events.Where(e => e.Kind == AerTaskEventKind.StdoutChunk).SelectMany(e => e.Data ?? [])]);

    [Fact]
    public void Constructor_NullProgram_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new AerTask(null!));
    }

    [Fact]
    public void Constructor_NullArgs_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new AerTask("cmd", null!));
    }

    [Fact]
    public void Constructor_TooManyArgs_ThrowsAerException()
    {
        string[] tooManyArgs = [.. Enumerable.Repeat("x", 65_537)];

        AerException ex = Assert.Throws<AerException>(() => new AerTask("cmd", tooManyArgs));
        Assert.Equal(AerErrorCode.NullPointer, ex.ErrorCode);
    }

    [Fact]
    public void WithEnv_EmptyKey_ThrowsAerExceptionWithInvalidArgument()
    {
        using AerTask task = new(OperatingSystem.IsWindows() ? "cmd" : "echo");

        AerException ex = Assert.Throws<AerException>(() => task.WithEnv(string.Empty, "value"));
        Assert.Equal(AerErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithEnv_KeyContainingEquals_ThrowsAerExceptionWithInvalidArgument()
    {
        using AerTask task = new(OperatingSystem.IsWindows() ? "cmd" : "echo");

        AerException ex = Assert.Throws<AerException>(() => task.WithEnv("BAD=KEY", "value"));
        Assert.Equal(AerErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithCwd_EmptyPath_ThrowsAerExceptionWithInvalidArgument()
    {
        using AerTask task = new(OperatingSystem.IsWindows() ? "cmd" : "echo");

        AerException ex = Assert.Throws<AerException>(() => task.WithCwd(string.Empty));
        Assert.Equal(AerErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public void WithCwd_InvalidDirectory_RunThrowsAerExceptionWithSpawnFailed()
    {
        (string prog, string[] args) = ExitZero();
        using AerTask task = new AerTask(prog, args).WithCwd("definitely_not_a_real_directory_xyzzy_aer");

        AerException ex = Assert.Throws<AerException>(task.Run);
        Assert.Equal(AerErrorCode.SpawnFailed, ex.ErrorCode);
    }

    [Fact]
    public void Run_CalledTwice_ThrowsInvalidOperationException()
    {
        (string prog, string[] args) = EchoHello();
        using AerTask task = new(prog, args);

        task.Run();

        _ = Assert.Throws<InvalidOperationException>(task.Run);
    }

    [Fact]
    public void Run_HappyPath_RaisesStartedThenChunksThenExited()
    {
        (string prog, string[] args) = EchoHello();
        List<AerEventArgs> events = [];

        using AerTask task = new AerTask(prog, args).WithCaptureOutput();
        task.EventRaised += (_, e) => events.Add(e);

        task.Run();

        Assert.NotEmpty(events);
        Assert.Equal(AerTaskEventKind.Started, events[0].Kind);
        Assert.Equal(AerTaskEventKind.Exited, events[^1].Kind);

        int startedIndex = events.FindIndex(e => e.Kind == AerTaskEventKind.Started);
        int exitedIndex = events.FindIndex(e => e.Kind == AerTaskEventKind.Exited);
        foreach (int chunkIndex in events
            .Select((e, i) => (e, i))
            .Where(t => t.e.Kind == AerTaskEventKind.StdoutChunk)
            .Select(t => t.i))
        {
            Assert.True(chunkIndex > startedIndex, "chunk must arrive after Started");
            Assert.True(chunkIndex < exitedIndex, "chunk must arrive before Exited");
        }

        AerEventArgs exited = events[exitedIndex];
        Assert.Equal(0, exited.ExitCode);
        Assert.Equal(AerExitReason.Natural, exited.ExitReason);

        string output = DecodeChunks(events);
        Assert.Contains("hello", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_TimeoutElapses_ThrowsAerTimeoutException()
    {
        (string prog, string[] args) = LongRunning();
        using AerTask task = new AerTask(prog, args).WithTimeout(TimeSpan.FromMilliseconds(300));

        AerTimeoutException ex = Assert.Throws<AerTimeoutException>(task.Run);
        Assert.Equal(AerErrorCode.TimedOut, ex.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_CancelledViaCancellationToken_ThrowsAerCancelException()
    {
        (string prog, string[] args) = LongRunning();
        using AerTask task = new(prog, args);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        AerCancelException ex = await Assert.ThrowsAsync<AerCancelException>(() => task.RunAsync(cts.Token));
        Assert.Equal(AerErrorCode.Cancelled, ex.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_NotCancelled_CompletesNormally()
    {
        (string prog, string[] args) = EchoHello();
        List<AerEventArgs> events = [];
        using AerTask task = new(prog, args);
        task.EventRaised += (_, e) => events.Add(e);

        await task.RunAsync();

        Assert.Contains(events, e => e.Kind == AerTaskEventKind.Exited && e.ExitCode == 0);
    }

    [Fact]
    public void WithEnv_MakesVariableVisibleToChild()
    {
        (string prog, string[] args) = EchoEnvVar("AER_DOTNET_MANAGED_TEST_VAR");
        List<AerEventArgs> events = [];

        using AerTask task = new AerTask(prog, args)
            .WithCaptureOutput()
            .WithEnv("AER_DOTNET_MANAGED_TEST_VAR", "hello_from_managed_wrapper");
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
        List<AerEventArgs> events = [];

        using AerTask task = new AerTask(prog, args).WithCaptureOutput().WithCwd(targetDir);
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
