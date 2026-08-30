using System.Text;

namespace Aer.Core.Tests;

public class EnvironmentAndWorkingDirectoryTests
{
    private static (string Program, string[] Args) ExitZero() =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "exit 0"])
            : ("sh", ["-c", "exit 0"]);

    private static (string Program, string[] Args) EchoEnvVar(string var) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"echo %{var}%"])
            : ("sh", ["-c", $"echo ${var}"]);

    private static (string Program, string[] Args) EchoTwoEnvVars(string var1, string var2) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"echo %{var1}% & echo %{var2}%"])
            : ("sh", ["-c", $"echo ${var1} ; echo ${var2}"]);

    private static (string Program, string[] Args) PrintCwd() =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "cd"])
            : ("sh", ["-c", "pwd"]);

    /// <summary>
    /// The shell's absolute path. Used specifically for clear-env tests: after clearing the child's
    /// environment, relying on PATH-based resolution of "cmd"/"sh" would be testing an unrelated
    /// resolution mechanism rather than clear-env behavior itself.
    /// </summary>
    private static string ShellAbsolutePath() =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe"
            : "/bin/sh";

    private static List<byte[]> RunAndCaptureStdout(AerTaskHandle task)
    {
        AerErrorCode captureCode = NativeMethods.aer_task_with_capture_output(task, true);
        Assert.Equal(AerErrorCode.Ok, captureCode);

        List<byte[]> chunks = [];
        using CallbackBridge bridge = new((evt, data) =>
        {
            if (evt.Kind == AerEventKind.StdoutChunk && data != null)
            {
                chunks.Add(data);
            }
        });
        AerErrorCode runCode = NativeMethods.aer_task_run(task, bridge.NativeCallback, nint.Zero);
        Assert.Equal(AerErrorCode.Ok, runCode);

        return chunks;
    }

    [Fact]
    public void SetEnv_MakesVariableVisibleToChild()
    {
        (string prog, string[] args) = EchoEnvVar("AER_DOTNET_TEST_VAR");

        using AerTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        AerErrorCode envCode = NativeMethods.aer_task_set_env(task, "AER_DOTNET_TEST_VAR", "hello_dotnet");
        Assert.Equal(AerErrorCode.Ok, envCode);

        List<byte[]> chunks = RunAndCaptureStdout(task);

        string output = Encoding.UTF8.GetString([.. chunks.SelectMany(b => b)]);
        Assert.Contains("hello_dotnet", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SetEnv_RepeatedCallSameKey_OverridesEarlierValue()
    {
        (string prog, string[] args) = EchoEnvVar("AER_DOTNET_TEST_VAR");

        using AerTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        Assert.Equal(AerErrorCode.Ok, NativeMethods.aer_task_set_env(task, "AER_DOTNET_TEST_VAR", "first_value"));
        Assert.Equal(AerErrorCode.Ok, NativeMethods.aer_task_set_env(task, "AER_DOTNET_TEST_VAR", "second_value"));

        List<byte[]> chunks = RunAndCaptureStdout(task);

        string output = Encoding.UTF8.GetString([.. chunks.SelectMany(b => b)]);
        Assert.Contains("second_value", output, StringComparison.Ordinal);
        Assert.DoesNotContain("first_value", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SetClearEnv_RemovesInheritedVariable_KeepsExplicitOne()
    {
        Environment.SetEnvironmentVariable("AER_DOTNET_INHERITED_VAR", "should_not_be_inherited");
        try
        {
            (_, string[] args) = EchoTwoEnvVars("AER_DOTNET_INHERITED_VAR", "AER_DOTNET_EXPLICIT_VAR");

            using AerTaskHandle task = NativeMethods.CreateTask(ShellAbsolutePath(), args);
            Assert.False(task.IsInvalid);

            Assert.Equal(AerErrorCode.Ok, NativeMethods.aer_task_set_clear_env(task, true));
            Assert.Equal(
                AerErrorCode.Ok,
                NativeMethods.aer_task_set_env(task, "AER_DOTNET_EXPLICIT_VAR", "should_be_present"));

            List<byte[]> chunks = RunAndCaptureStdout(task);

            string output = Encoding.UTF8.GetString([.. chunks.SelectMany(b => b)]);
            Assert.DoesNotContain("should_not_be_inherited", output, StringComparison.Ordinal);
            Assert.Contains("should_be_present", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AER_DOTNET_INHERITED_VAR", null);
        }
    }

    [Fact]
    public void SetCwd_ChangesChildWorkingDirectory()
    {
        string targetDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        (string prog, string[] args) = PrintCwd();

        using AerTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        Assert.Equal(AerErrorCode.Ok, NativeMethods.aer_task_set_cwd(task, targetDir));

        List<byte[]> chunks = RunAndCaptureStdout(task);

        string output = Encoding.UTF8.GetString([.. chunks.SelectMany(b => b)]);
        string? printedLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        Assert.NotNull(printedLine);

        string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(printedLine.Trim()));
        Assert.Equal(targetDir, actual, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void SetCwd_InvalidPath_CausesSpawnFailedOnRun()
    {
        (string prog, string[] args) = ExitZero();
        List<AerEvent> events = [];

        using AerTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        AerErrorCode cwdCode = NativeMethods.aer_task_set_cwd(task, "definitely_not_a_real_directory_xyzzy_aer");
        Assert.Equal(AerErrorCode.Ok, cwdCode);

        using CallbackBridge bridge = new((evt, _) => events.Add(evt));
        AerErrorCode runCode = NativeMethods.aer_task_run(task, bridge.NativeCallback, nint.Zero);

        Assert.Equal(AerErrorCode.SpawnFailed, runCode);
        Assert.Empty(events);
    }

    [Fact]
    public void SetEnv_EmptyKey_ReturnsInvalidArgument()
    {
        using AerTaskHandle task = NativeMethods.CreateTask(OperatingSystem.IsWindows() ? "cmd" : "echo");
        Assert.False(task.IsInvalid);

        AerErrorCode code = NativeMethods.aer_task_set_env(task, string.Empty, "value");
        Assert.Equal(AerErrorCode.InvalidArgument, code);
    }

    [Fact]
    public void SetEnv_KeyContainingEquals_ReturnsInvalidArgument()
    {
        using AerTaskHandle task = NativeMethods.CreateTask(OperatingSystem.IsWindows() ? "cmd" : "echo");
        Assert.False(task.IsInvalid);

        AerErrorCode code = NativeMethods.aer_task_set_env(task, "BAD=KEY", "value");
        Assert.Equal(AerErrorCode.InvalidArgument, code);
    }

    [Fact]
    public void SetCwd_EmptyPath_ReturnsInvalidArgument()
    {
        using AerTaskHandle task = NativeMethods.CreateTask(OperatingSystem.IsWindows() ? "cmd" : "echo");
        Assert.False(task.IsInvalid);

        AerErrorCode code = NativeMethods.aer_task_set_cwd(task, string.Empty);
        Assert.Equal(AerErrorCode.InvalidArgument, code);
    }
}
