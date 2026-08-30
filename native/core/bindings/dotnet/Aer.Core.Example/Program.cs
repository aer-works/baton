// Tutorial-style walkthrough of the Aer.Core managed API.
//
// Run with: pixi run dotnet-example
//
// 1. Capture output live from EventRaised while a timeout is armed (but not hit).
// 2. RunAsync + CancellationTokenSource: cancel a long-running process and catch
//    AerCancelException.
// 3. WithEnv / WithCwd: the child echoes a variable we set and its working directory.

using System.Text;
using Aer.Core;

Console.WriteLine("=== Section 1: capture output live, with a timeout armed ===\n");
await RunCaptureSectionAsync();

Console.WriteLine("\n=== Section 2: RunAsync + CancellationTokenSource ===\n");
await RunCancellationSectionAsync();

Console.WriteLine("\n=== Section 3: WithEnv + WithCwd ===\n");
RunEnvAndCwdSection();

Console.WriteLine("\nDone.");

static async Task RunCaptureSectionAsync()
{
    (string prog, string[] args) = SlowButFiniteCommand();

    using AerTask task = new AerTask(prog, args)
        .WithCaptureOutput()
        .WithTimeout(TimeSpan.FromSeconds(10));

    task.EventRaised += (_, e) =>
    {
        switch (e.Kind)
        {
            case AerTaskEventKind.Started:
                Console.WriteLine($"  → Started    (pid {e.Pid})");
                break;
            case AerTaskEventKind.StdoutChunk:
                Console.WriteLine($"  [stdout] {Encoding.UTF8.GetString(e.Data!).TrimEnd('\r', '\n')}");
                break;
            case AerTaskEventKind.StderrChunk:
                Console.WriteLine($"  [stderr] {Encoding.UTF8.GetString(e.Data!).TrimEnd('\r', '\n')}");
                break;
            case AerTaskEventKind.Exited:
                Console.WriteLine($"  → Exited     (code {e.ExitCode}, reason {e.ExitReason})");
                break;
            default:
                break;
        }
    };

    Console.WriteLine("Spawning task, streaming output as it arrives...\n");
    await task.RunAsync();
}

static async Task RunCancellationSectionAsync()
{
    (string prog, string[] args) = LongRunningCommand();

    using AerTask task = new(prog, args);
    task.EventRaised += (_, e) =>
    {
        if (e.Kind == AerTaskEventKind.Started)
        {
            Console.WriteLine($"  → Started    (pid {e.Pid})");
        }
    };

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(1));

    Console.WriteLine("Spawning long-running process (will be cancelled in ~1 s)...\n");

    try
    {
        await task.RunAsync(cts.Token);
        Console.WriteLine("  unexpected: completed normally");
    }
    catch (AerCancelException ex)
    {
        Console.WriteLine($"  → Cancelled as expected: {ex.Message}");
    }
}

static void RunEnvAndCwdSection()
{
    string cwd = Path.GetTempPath();
    (string prog, string[] args) = EchoVarAndCwd("AER_EXAMPLE_VAR");

    using AerTask task = new AerTask(prog, args)
        .WithEnv("AER_EXAMPLE_VAR", "hello_from_dotnet")
        .WithCwd(cwd)
        .WithCaptureOutput();

    StringBuilder output = new();
    task.EventRaised += (_, e) =>
    {
        if (e.Kind == AerTaskEventKind.StdoutChunk)
        {
            _ = output.Append(Encoding.UTF8.GetString(e.Data!));
        }
    };

    Console.WriteLine($"Spawning with AER_EXAMPLE_VAR=hello_from_dotnet, cwd={cwd}\n");
    task.Run();

    Console.WriteLine("--- what the child saw ---");
    Console.Write(output.ToString());
}

static (string Program, string[] Args) SlowButFiniteCommand()
{
    return OperatingSystem.IsWindows()
        ? ("ping", ["-n", "4", "127.0.0.1"])
        : ("sh", ["-c", "for i in 1 2 3 4; do echo \"ping $i\"; sleep 1; done"]);
}

static (string Program, string[] Args) LongRunningCommand()
{
    return OperatingSystem.IsWindows()
        ? ("ping", ["-n", "60", "127.0.0.1"])
        : ("sh", ["-c", "sleep 60"]);
}

static (string Program, string[] Args) EchoVarAndCwd(string var)
{
    return OperatingSystem.IsWindows()
        ? ("cmd", ["/c", $"echo %{var}% & cd"])
        : ("sh", ["-c", $"echo ${var}; pwd"]);
}
