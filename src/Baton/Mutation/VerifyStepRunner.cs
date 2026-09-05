using System.Diagnostics;
using Baton.Core;

namespace Baton.Mutation;

/// <summary>
/// The seam <see cref="VerifyStepRunner"/> spawns through (#1882). Exists so a test can pin WHAT the
/// runner launches — the program, the argv (buildlock wrapping included), the cwd and the timeout —
/// without spawning anything, which is the only way those four facts are checkable at all: they are
/// arguments to a process launch, not observable in its output.
/// </summary>
/// <returns>
/// The child's exit code (null when it was killed at the wall-clock bound), its combined
/// stdout+stderr, and whether that kill happened.
/// </returns>
public delegate Task<(int? ExitCode, string Output, bool TimedOut)> VerifyProcessLauncher(
    string program,
    IReadOnlyList<string> args,
    string workingDirectory,
    TimeSpan timeout,
    CancellationToken cancellationToken);

/// <summary>
/// The zero-token verify step (#1882, spec/baton.md §3): before a review lane's worker takes its
/// first turn, the ENGINE runs the operator's allowlisted <c>--verify-cmd</c> commands and captures
/// what they did. No model is in the loop; spec/baton.md §9 states why that, rather than a cheaper
/// model or a wider grant, is the answer to a reviewer's unverified runtime claims.
/// <para>
/// Every command runs sequentially (they contend for the same build lock, so parallelism would only
/// convert wall clock into lock waits), in <paramref name="workingDirectory"/>, wrapped in
/// <c>python tools/buildlock.py</c>, under an individual wall-clock bound. A non-zero exit is
/// recorded and the step carries on: the results are evidence for the reviewer, not a gate on it.
/// </para>
/// </summary>
/// <remarks>
/// Registered in <c>tests/Baton.Architecture.Tests/VendorSpawnGateTests.cs</c>'s approved-spawn-sites
/// list, per that test's requirement that every <see cref="BatonTask"/> construction site in
/// <c>src/</c> be named there.
/// </remarks>
public static class VerifyStepRunner
{
    /// <summary>The interpreter the wrapper itself runs under — the same spelling <c>pixi.toml</c>'s own tasks use.</summary>
    public const string LauncherProgram = "python";

    /// <summary>
    /// <c>tools/buildlock.py</c>, repo-relative: this step launches beside lanes that may already be
    /// building, which is exactly the mutual-kill case that script's own docstring exists for (#1402).
    /// Relative rather than absolute because it resolves against
    /// <c>workingDirectory</c> — the review worktree, which is a checkout of this repo.
    /// </summary>
    public const string BuildLockScriptPath = "tools/buildlock.py";

    /// <summary>
    /// The default per-command wall clock (<c>--verify-timeout</c>'s default). Ten minutes is longer
    /// than a cold <c>dotnet build</c> here and shorter than <c>buildlock.py</c>'s own 1800s
    /// contention timeout — which is why <see cref="VerifyStepReport.Render"/> tells the reader a
    /// timeout may be lock contention rather than a slow command.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs <paramref name="commands"/> in order and returns one <see cref="VerifyCommandResult"/>
    /// per command, always in the same order — a failing command never truncates the list, because
    /// "command 2 exited 1" and "command 3 was never run" are different facts and the reviewer needs
    /// both.
    /// </summary>
    /// <param name="launcher">Null uses the real <see cref="BatonTask"/> spawn; a test injects its own.</param>
    public static async Task<IReadOnlyList<VerifyCommandResult>> RunAsync(
        IReadOnlyList<VerifyStepCommand> commands,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        VerifyProcessLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        var spawn = launcher ?? SpawnAsync;
        var results = new List<VerifyCommandResult>(commands.Count);

        foreach (var command in commands)
        {
            var args = BuildLauncherArgs(command);
            var stopwatch = Stopwatch.StartNew();
            int? exitCode;
            string output;
            bool timedOut;
            try
            {
                (exitCode, output, timedOut) = await spawn(
                    LauncherProgram, args, workingDirectory, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (BatonException ex)
            {
                // The OS refusing to spawn `python` at all. Recorded as a result rather than thrown:
                // the step is evidence-gathering ahead of a review, and a review that still runs with
                // "this instrument did not start" written down beats a dispatch that dies before the
                // worker ever begins. exitCode stays null (nothing exited), TimedOut stays false.
                stopwatch.Stop();
                results.Add(new VerifyCommandResult(
                    command.CommandLine, ExitCode: null, stopwatch.ElapsedMilliseconds, TimedOut: false,
                    Tail: $"The verify command could not be launched: {ex.Message}"));
                continue;
            }

            stopwatch.Stop();
            results.Add(new VerifyCommandResult(
                command.CommandLine, exitCode, stopwatch.ElapsedMilliseconds, timedOut,
                VerifyStepReport.BoundTail(output)));
        }

        return results;
    }

    /// <summary>
    /// The argv actually launched: <c>python tools/buildlock.py &lt;command argv...&gt;</c>. No
    /// <c>--</c> separator — <c>buildlock.py</c> takes the wrapped command as its bare positional
    /// tail, and a literal <c>--</c> would be passed through to the wrapped program.
    /// </summary>
    public static IReadOnlyList<string> BuildLauncherArgs(VerifyStepCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return [BuildLockScriptPath, .. command.Argv];
    }

    /// <summary>
    /// The real spawn. <see cref="BatonTask.WithTimeout"/> kills the whole process tree through the
    /// Windows Job Object the task holds (<c>SafeJobObjectHandle</c>), which is what makes a timeout
    /// safe here: <c>buildlock.py</c>'s lock is an OS region lock the kernel releases the instant its
    /// holder dies, so killing a waiting or building child cannot strand the lock.
    /// </summary>
    private static async Task<(int? ExitCode, string Output, bool TimedOut)> SpawnAsync(
        string program,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Declared outside the task so a timeout still yields whatever the child had already printed:
        // the tail of a build that ran for ten minutes is the most useful thing a timed-out command
        // can leave behind, and it is lost if the buffer lives inside the throwing call.
        var output = new System.Text.StringBuilder();
        var exitCode = -1;

        using var task = new BatonTask(program, [.. args])
            .WithCaptureOutput(true)
            .WithTimeout(timeout)
            .WithCwd(workingDirectory);

        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case BatonTaskEventKind.StdoutChunk or BatonTaskEventKind.StderrChunk when e.Data is { } data:
                    lock (output)
                    {
                        output.Append(System.Text.Encoding.UTF8.GetString(data));
                    }

                    break;
                case BatonTaskEventKind.Exited:
                    exitCode = e.ExitCode;
                    break;
            }
        };

        try
        {
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BatonTimeoutException)
        {
            lock (output)
            {
                return (null, output.ToString(), true);
            }
        }
        catch (BatonException ex) when (ex.ErrorCode == BatonErrorCode.TimedOut)
        {
            lock (output)
            {
                return (null, output.ToString(), true);
            }
        }

        lock (output)
        {
            return (exitCode, output.ToString(), false);
        }
    }
}
