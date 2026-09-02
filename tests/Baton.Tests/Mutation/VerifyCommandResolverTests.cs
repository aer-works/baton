using Baton.Mutation;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="VerifyCommandResolver"/> (#1702) — see its own class doc for the resolution
/// order and spec/baton.md §3 for the contract. Pure/unit — no pump, no real dispatch.
/// </summary>
public sealed class VerifyCommandResolverTests
{
    [Fact]
    public void Resolve_returns_null_when_nothing_resolves()
    {
        var resolved = VerifyCommandResolver.Resolve(committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_falls_back_to_the_role_default_when_no_override_or_repo_declaration()
    {
        var resolved = VerifyCommandResolver.Resolve(committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RoleDefault, resolved!.Source);
        Assert.Equal("pixi", resolved.Program);
        Assert.Equal(["run", "gates-quiet"], resolved.Args);
        Assert.Equal("gates-quiet", resolved.Label);
    }

    [Fact]
    public void Resolve_repo_declaration_wins_over_the_role_default()
    {
        var resolved = VerifyCommandResolver.Resolve(
            "python -c \"import sys; sys.exit(0)\"", overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
        Assert.Equal("python -c \"import sys; sys.exit(0)\"", resolved.Label);
    }

    [Fact]
    public void Resolve_override_wins_over_both_the_repo_declaration_and_the_role_default()
    {
        var resolved = VerifyCommandResolver.Resolve(
            "python -c \"import sys; sys.exit(0)\"",
            overrideCommand: "python -c \"import sys; sys.exit(1)\"",
            roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.Override, resolved!.Source);
        Assert.Equal("python -c \"import sys; sys.exit(1)\"", resolved.Label);
    }

    [Fact]
    public void Resolve_repo_declaration_still_applies_when_the_role_declares_no_default()
    {
        // Pins the rule spec/baton.md §3 states: a review/advise-shaped role (no VerifyPixiTask)
        // dispatched against a workspace that declares .baton/verify still gets a verify step.
        var resolved = VerifyCommandResolver.Resolve(
            "python -c \"import sys; sys.exit(0)\"", overrideCommand: null, roleVerifyPixiTask: null);

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
    }

    // ---- #1708 H1: the declaration comes from the COMMITTED tree, read BEFORE the worker runs ----

    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_reads_the_committed_declaration_skipping_blanks_and_comments()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "\n  \n# a comment\n  python -c \"import sys; sys.exit(0)\"  \n");
            TempGitRepository.CommitAll(workspace, "declare verify");

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(0)\"", committed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The self-verification hole (#1708 H1), red-first: the "worker" writes its own <c>.baton/verify</c>
    /// into the live workspace, and the engine must not read it. Discriminates the committed-tree read
    /// from a merely-earlier working-tree read — the file here is written into a workspace that ALREADY
    /// held a different committed declaration, so a working-tree read at any time returns the worker's
    /// line and only a committed read returns the repo's.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_ignores_a_working_tree_file_the_worker_wrote()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.CommitAll(workspace, "declare verify");

            // The worker, mid-execution, replaces it with a verifier that always passes.
            WriteRepoDeclaration(workspace, "cmd /c exit 0");

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Equal("python -c \"import sys; sys.exit(1)\"", committed);
            Assert.Equal("cmd /c exit 0", VerifyCommandResolver.ReadWorkingTreeRepoDeclaration(workspace));

            // ...and what actually runs is the committed line, never the worker's.
            var resolved = VerifyCommandResolver.Resolve(committed, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");
            Assert.Equal("python -c \"import sys; sys.exit(1)\"", resolved!.Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The same hole with nothing committed at all — a worker inventing the file from scratch. Fails
    /// CLOSED: no committed declaration means the role default runs, not the worker's line.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_returns_null_for_an_uncommitted_declaration()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            TempGitRepository.InitWithEverythingCommitted(workspace);
            WriteRepoDeclaration(workspace, "cmd /c exit 0");

            var committed = await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken);

            Assert.Null(committed);

            var resolved = VerifyCommandResolver.Resolve(committed, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");
            Assert.Equal(VerifyCommandSource.RoleDefault, resolved!.Source);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    /// <summary>
    /// The declaration is the DISPATCHED workspace's, not its repository root's. `git show HEAD:&lt;path&gt;`
    /// resolves against the repo root, so a monorepo package dispatched with <c>--workspace</c> would
    /// otherwise be graded by a root-level file it does not own — and the working-tree half of the
    /// drift comparison, which is workspace-relative, would be reading a different file entirely.
    /// </summary>
    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_reads_the_workspace_subdirectory_not_the_repository_root()
    {
        var repoRoot = CreateTempWorkspace();
        try
        {
            var package = Path.Combine(repoRoot, "packages", "thing");
            Directory.CreateDirectory(package);
            WriteRepoDeclaration(repoRoot, "cmd /c exit 0");
            TempGitRepository.InitWithEverythingCommitted(repoRoot);

            // The dispatched workspace declares nothing of its own, so it declares nothing at all --
            // never the root's line.
            Assert.Null(await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                package, TestContext.Current.CancellationToken));

            // ...and when it does declare its own, that is the one read.
            WriteRepoDeclaration(package, "python -c \"import sys; sys.exit(1)\"");
            TempGitRepository.CommitAll(repoRoot, "declare package verify");

            Assert.Equal(
                "python -c \"import sys; sys.exit(1)\"",
                await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                    package, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(repoRoot);
        }
    }

    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_returns_null_outside_a_git_repository()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "cmd /c exit 0");

            Assert.Null(await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public async Task ReadCommittedRepoDeclarationAsync_returns_null_when_git_itself_cannot_spawn()
    {
        // Fails closed rather than throwing: this runs on the dispatch path, before the worker is
        // spawned, so an exception here would abort a dispatch over an absent optional file.
        var workspace = CreateTempWorkspace();
        try
        {
            Assert.Null(await VerifyCommandResolver.ReadCommittedRepoDeclarationAsync(
                workspace, TestContext.Current.CancellationToken, gitProgram: "this-is-not-a-real-git-binary-12345"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public void DeclarationDigest_compares_the_command_line_not_the_file()
    {
        // What FlowEvent.VerifyDeclarationIgnored's two digests mean: a comment or whitespace edit is
        // not drift, a changed command always is, and "no declaration" is distinguishable from both.
        Assert.Equal(
            VerifyCommandResolver.DeclarationDigest("pixi run gates-quiet"),
            VerifyCommandResolver.DeclarationDigest("pixi run gates-quiet"));
        Assert.NotEqual(
            VerifyCommandResolver.DeclarationDigest("pixi run gates-quiet"),
            VerifyCommandResolver.DeclarationDigest("cmd /c exit 0"));
        Assert.Null(VerifyCommandResolver.DeclarationDigest(null));
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_not_runnable_when_the_pixi_task_is_absent()
    {
        // #1702's own measured shape: a role's baked-in task name that a foreign (or just
        // out-of-date) workspace's `pixi task list` does not contain.
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "this-task-definitely-does-not-exist");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, RepoRoot(), CancellationToken.None);

        Assert.False(runnable);
        Assert.Equal("task absent: this-task-definitely-does-not-exist", reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_task_is_present()
    {
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "build");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, RepoRoot(), CancellationToken.None);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    /// <summary>
    /// #1708 H2, red-first: a probe that SPAWNS and exits non-zero is an engine-environment failure,
    /// not evidence the task is absent (spec/baton.md §3). Before this fix it returned
    /// <c>"task absent: ... (pixi task list failed)"</c> and the gate was skipped with the step reading
    /// Succeeded. <c>git task list</c> is the stand-in: a real binary that runs and exits 1.
    /// </summary>
    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_probe_itself_exits_non_zero()
    {
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
            resolved!, RepoRoot(), TestContext.Current.CancellationToken, pixiProgram: "git");

        Assert.True(runnable);
        Assert.Null(reason);
    }

    /// <summary>
    /// #1708 H3, red-first (control arm): an unresolvable first token is NOT a not-run verdict any more.
    /// It runs through <c>cmd.exe /d /c</c> and its exit code becomes a real <c>VerifyFailed</c> — the
    /// polarity here is inverted from what this same input asserted before the fix.
    /// </summary>
    [Theory]
    [InlineData("totally-not-a-real-binary-12345 --flag")]
    [InlineData("python -c \"import sys; sys.exit(0)\"")]
    [InlineData("exit 3")]
    [InlineData("echo ok")]
    [InlineData("call gates.bat")]
    public async Task CheckRunnableAsync_never_pre_probes_a_non_pixi_command_line(string overrideCommand)
    {
        // `exit`, `echo` and `call` are cmd.exe intrinsics: runnable through the shell, resolvable to no
        // file on PATH. The old filesystem lookup called all three "executable not found" and skipped
        // the gate. Nothing but a role-default pixi task is probed at all now.
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: overrideCommand, roleVerifyPixiTask: null);

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
            resolved!, workingDirectory: null, TestContext.Current.CancellationToken);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    /// <summary>
    /// The other half of H3's polarity: those intrinsic lines really do run, and their exit code really
    /// does decide. <c>exit 3</c> goes red, <c>echo ok</c> goes green — through the same
    /// <see cref="VerifyRunner"/> spawn the engine uses.
    /// </summary>
    [Theory]
    [InlineData("exit 3", false)]
    [InlineData("echo ok", true)]
    public async Task A_cmd_intrinsic_verify_line_actually_runs_and_its_exit_code_decides(string commandLine, bool expectedPassed)
    {
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: commandLine, roleVerifyPixiTask: null);

        var outcome = await VerifyRunner.RunProcessAsync(
            resolved!.Program, resolved.Args, workingDirectory: null, TestContext.Current.CancellationToken);

        Assert.Equal(expectedPassed, outcome.Passed);
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_pixi_itself_cannot_spawn()
    {
        // Pins the CheckPixiTaskAsync BatonException arm's own contract -- see its comment for why.
        var resolved = VerifyCommandResolver.Resolve(
            committedRepoDeclaration: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(
            resolved!, workingDirectory: null, CancellationToken.None, pixiProgram: "this-is-not-a-real-pixi-binary-12345");

        Assert.True(runnable);
        Assert.Null(reason);
    }

    private static string CreateTempWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"verify-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteRepoDeclaration(string workspace, string content)
    {
        var batonDir = Path.Combine(workspace, ".baton");
        Directory.CreateDirectory(batonDir);
        File.WriteAllText(Path.Combine(batonDir, "verify"), content);
    }


    /// <summary>The real baton repo checkout — its own <c>pixi task list</c> is what CheckRunnableAsync's role-default arms probe.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (pixi.toml) from test base directory.");
    }
}
