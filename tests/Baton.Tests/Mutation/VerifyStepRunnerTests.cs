using Baton.Mutation;

namespace Baton.Tests.Mutation;

/// <summary>
/// What the pre-turn verify step actually LAUNCHES (#1882), pinned through the injected
/// <see cref="VerifyProcessLauncher"/> seam. These four facts — the program, the buildlock wrapping,
/// the working directory, and the per-command timeout — are arguments to a process spawn, so they are
/// invisible in the spawn's own output and cannot be checked any other way short of really running a
/// build.
/// </summary>
public class VerifyStepRunnerTests
{
    private sealed record Launch(string Program, IReadOnlyList<string> Args, string Cwd, TimeSpan Timeout);

    private static VerifyStepCommand Command(string commandLine)
    {
        Assert.True(VerifyStepCommandParser.TryParse(commandLine, out var command, out var error), error);
        return command!;
    }

    [Fact]
    public async Task Each_command_is_launched_through_python_buildlock_in_the_review_worktree()
    {
        var launches = new List<Launch>();
        VerifyProcessLauncher launcher = (program, args, cwd, timeout, _) =>
        {
            launches.Add(new Launch(program, args, cwd, timeout));
            return Task.FromResult<(int?, string, bool)>((0, "ok", false));
        };

        await VerifyStepRunner.RunAsync(
            [Command("dotnet build -warnaserror"), Command("python tools/gates/gates.py --selftest")],
            workingDirectory: @"C:\worktrees\w1882",
            timeout: TimeSpan.FromMinutes(7),
            CancellationToken.None,
            launcher);

        Assert.Equal(2, launches.Count);

        // The launcher is `python`, never a shell: no cmd.exe, no /c, no shell string.
        Assert.All(launches, launch => Assert.Equal("python", launch.Program));
        Assert.All(launches, launch => Assert.Equal(@"C:\worktrees\w1882", launch.Cwd));
        Assert.All(launches, launch => Assert.Equal(TimeSpan.FromMinutes(7), launch.Timeout));

        // The wrapping is buildlock first, then the command's own argv verbatim -- and NO `--`
        // separator, which buildlock.py would pass through to the wrapped program.
        Assert.Equal(["tools/buildlock.py", "dotnet", "build", "-warnaserror"], launches[0].Args);
        Assert.Equal(["tools/buildlock.py", "python", "tools/gates/gates.py", "--selftest"], launches[1].Args);
        Assert.DoesNotContain("--", launches[0].Args);
    }

    [Fact]
    public async Task A_non_zero_exit_does_not_abort_the_step_and_later_commands_still_run()
    {
        var launched = new List<string>();
        VerifyProcessLauncher launcher = (_, args, _, _, _) =>
        {
            launched.Add(string.Join(" ", args));
            // The first command fails hard; the second is the discriminating arm -- if a non-zero exit
            // aborted the step, it would never be launched and would be missing from the results.
            return Task.FromResult<(int?, string, bool)>((launched.Count == 1 ? 1 : 0, "output", false));
        };

        var results = await VerifyStepRunner.RunAsync(
            [Command("dotnet build"), Command("dotnet test")],
            @"C:\worktrees\w1882", TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

        Assert.Equal(2, launched.Count);
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].ExitCode);
        Assert.Equal(0, results[1].ExitCode);
        Assert.All(results, r => Assert.False(r.TimedOut));
    }

    [Fact]
    public async Task A_timed_out_command_records_a_null_exit_code_and_the_timed_out_flag()
    {
        VerifyProcessLauncher launcher = (_, _, _, _, _) =>
            Task.FromResult<(int?, string, bool)>((null, "partial build output", true));

        var results = await VerifyStepRunner.RunAsync(
            [Command("dotnet build")], @"C:\worktrees\w1882", TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

        var result = Assert.Single(results);
        Assert.True(result.TimedOut);
        // Never a fabricated -1: an exit code that does not exist is reported as absent, because -1 is
        // also a real exit code a real command can produce.
        Assert.Null(result.ExitCode);
        // Whatever the child had already printed survives the kill (VerifyStepRunner.SpawnAsync's own
        // buffer placement is what buys this).
        Assert.Equal("partial build output", result.Tail);
    }

    [Fact]
    public async Task The_tail_is_bounded_to_the_last_two_hundred_lines()
    {
        var output = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));
        VerifyProcessLauncher launcher = (_, _, _, _, _) => Task.FromResult<(int?, string, bool)>((0, output, false));

        var results = await VerifyStepRunner.RunAsync(
            [Command("dotnet build")], @"C:\worktrees\w1882", TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

        var lines = Assert.Single(results).Tail.Split('\n');
        Assert.Equal(VerifyStepReport.MaxTailLines, lines.Length);
        Assert.Equal("line 301", lines[0]);
        Assert.Equal("line 500", lines[^1]);
    }
}
