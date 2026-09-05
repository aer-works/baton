using Baton.Mutation;
using Baton.Tests.Shared;

namespace Baton.Tests.Mutation;

/// <summary>
/// What the pre-turn verify step actually LAUNCHES (#1882), pinned through the injected
/// <see cref="VerifyProcessLauncher"/> seam. These four facts — the program, the buildlock wrapping,
/// the working directory, and the per-command timeout — are arguments to a process spawn, so they are
/// invisible in the spawn's own output and cannot be checked any other way short of really running a
/// build.
/// </summary>
public class VerifyStepRunnerTests : IDisposable
{
    private sealed record Launch(string Program, IReadOnlyList<string> Args, string Cwd, TimeSpan Timeout);

    /// <summary>
    /// A directory that looks enough like a Baton checkout for the step to run in at all — which,
    /// since the wrapper-existence check landed, means it has to really contain
    /// <c>tools/buildlock.py</c>. A hardcoded fake path would now short-circuit every launch assertion
    /// below into the refusal arm, so the cwd these tests pin is a real one they created.
    /// </summary>
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"verify-runner-{Guid.NewGuid():N}");

    public VerifyStepRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "tools"));
        File.WriteAllText(Path.Combine(_workspace, VerifyStepRunner.BuildLockScriptPath), "# stand-in");
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_workspace);
        GC.SuppressFinalize(this);
    }

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
            workingDirectory: _workspace,
            timeout: TimeSpan.FromMinutes(7),
            CancellationToken.None,
            launcher);

        Assert.Equal(2, launches.Count);

        // The launcher is `python`, never a shell: no cmd.exe, no /c, no shell string.
        Assert.All(launches, launch => Assert.Equal("python", launch.Program));
        Assert.All(launches, launch => Assert.Equal(_workspace, launch.Cwd));
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
            _workspace, TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

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
            [Command("dotnet build")], _workspace, TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

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
            [Command("dotnet build")], _workspace, TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

        var lines = Assert.Single(results).Tail.Split('\n');
        Assert.Equal(VerifyStepReport.MaxTailLines, lines.Length);
        Assert.Equal("line 301", lines[0]);
        Assert.Equal("line 500", lines[^1]);
    }

    [Fact]
    public async Task A_workspace_with_no_buildlock_refuses_the_step_without_spawning_anything()
    {
        // The discriminating half of the wrapper check: a --workspace that is not a Baton checkout
        // (docs/agents/invoking-baton.md's whole population) must not reach the launcher at all --
        // python's own "can't open file" exit would otherwise be recorded under a heading naming the
        // operator's command. Every test above is the positive control: same launcher, same commands,
        // a workspace that DOES carry the wrapper, and each of them spawns.
        var launched = 0;
        VerifyProcessLauncher launcher = (_, _, _, _, _) =>
        {
            launched++;
            return Task.FromResult<(int?, string, bool)>((0, "ok", false));
        };

        var elsewhere = Path.Combine(Path.GetTempPath(), $"verify-nolock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        try
        {
            var results = await VerifyStepRunner.RunAsync(
                [Command("dotnet build"), Command("dotnet test")],
                elsewhere, TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

            Assert.Equal(0, launched);
            // Both commands are still listed -- "two were asked for and neither could run" is the fact
            // the reviewer needs, and an empty results file does not carry it.
            Assert.Equal(2, results.Count);
            Assert.Equal(["dotnet build", "dotnet test"], results.Select(r => r.CommandLine));
            Assert.All(results, r => Assert.Null(r.ExitCode));
            Assert.All(results, r => Assert.False(r.TimedOut));
            // The reason names both the missing file and the directory it was looked for under.
            Assert.All(results, r => Assert.Contains("tools/buildlock.py", r.Tail, StringComparison.Ordinal));
            Assert.All(results, r => Assert.Contains(elsewhere, r.Tail, StringComparison.Ordinal));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(elsewhere);
        }
    }
}
