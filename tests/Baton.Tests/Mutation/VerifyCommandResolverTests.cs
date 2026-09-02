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
        var resolved = VerifyCommandResolver.Resolve(workspaceDirectory: null, overrideCommand: null, roleVerifyPixiTask: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_falls_back_to_the_role_default_when_no_override_or_repo_declaration()
    {
        var resolved = VerifyCommandResolver.Resolve(workspaceDirectory: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

        Assert.NotNull(resolved);
        Assert.Equal(VerifyCommandSource.RoleDefault, resolved!.Source);
        Assert.Equal("pixi", resolved.Program);
        Assert.Equal(["run", "gates-quiet"], resolved.Args);
        Assert.Equal("gates-quiet", resolved.Label);
    }

    [Fact]
    public void Resolve_repo_declaration_wins_over_the_role_default()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(0)\"");

            var resolved = VerifyCommandResolver.Resolve(workspace, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

            Assert.NotNull(resolved);
            Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
            Assert.Equal("python -c \"import sys; sys.exit(0)\"", resolved.Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public void Resolve_override_wins_over_both_the_repo_declaration_and_the_role_default()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(0)\"");

            var resolved = VerifyCommandResolver.Resolve(
                workspace, overrideCommand: "python -c \"import sys; sys.exit(1)\"", roleVerifyPixiTask: "gates-quiet");

            Assert.NotNull(resolved);
            Assert.Equal(VerifyCommandSource.Override, resolved!.Source);
            Assert.Equal("python -c \"import sys; sys.exit(1)\"", resolved.Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public void Resolve_repo_declaration_still_applies_when_the_role_declares_no_default()
    {
        // Pins the rule spec/baton.md §3 states: a review/advise-shaped role (no VerifyPixiTask)
        // dispatched against a workspace that declares .baton/verify still gets a verify step.
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "python -c \"import sys; sys.exit(0)\"");

            var resolved = VerifyCommandResolver.Resolve(workspace, overrideCommand: null, roleVerifyPixiTask: null);

            Assert.NotNull(resolved);
            Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public void Resolve_repo_declaration_skips_blank_lines_and_comments()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            WriteRepoDeclaration(workspace, "\n  \n# a comment\n  python -c \"import sys; sys.exit(0)\"  \n");

            var resolved = VerifyCommandResolver.Resolve(workspace, overrideCommand: null, roleVerifyPixiTask: null);

            Assert.NotNull(resolved);
            Assert.Equal(VerifyCommandSource.RepoDeclaration, resolved!.Source);
            Assert.Equal("python -c \"import sys; sys.exit(0)\"", resolved.Label);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_not_runnable_when_the_pixi_task_is_absent()
    {
        // #1702's own measured shape: a role's baked-in task name that a foreign (or just
        // out-of-date) workspace's `pixi task list` does not contain.
        var resolved = VerifyCommandResolver.Resolve(
            RepoRoot(), overrideCommand: null, roleVerifyPixiTask: "this-task-definitely-does-not-exist");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, RepoRoot(), CancellationToken.None);

        Assert.False(runnable);
        Assert.Equal("task absent: this-task-definitely-does-not-exist", reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_the_pixi_task_is_present()
    {
        var resolved = VerifyCommandResolver.Resolve(RepoRoot(), overrideCommand: null, roleVerifyPixiTask: "build");

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, RepoRoot(), CancellationToken.None);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_override_reports_not_runnable_when_the_executable_does_not_resolve()
    {
        var resolved = VerifyCommandResolver.Resolve(
            workspaceDirectory: null, overrideCommand: "totally-not-a-real-binary-12345 --flag", roleVerifyPixiTask: null);

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, workingDirectory: null, CancellationToken.None);

        Assert.False(runnable);
        Assert.Equal("executable not found: totally-not-a-real-binary-12345", reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_override_reports_runnable_when_the_executable_resolves()
    {
        var resolved = VerifyCommandResolver.Resolve(
            workspaceDirectory: null, overrideCommand: "python -c \"import sys; sys.exit(0)\"", roleVerifyPixiTask: null);

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, workingDirectory: null, CancellationToken.None);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_override_reports_runnable_for_a_quoted_or_shell_shaped_line()
    {
        // A quoted path or a line built from cmd.exe intrinsics/operators isn't a bare executable name
        // the filesystem-only PATH lookup can resolve -- must not mislabel a genuinely runnable line
        // "not runnable" on the wrong reason (second-reader finding). Defers to the real cmd.exe /d /c
        // spawn, which handles quoting/operators correctly.
        var resolved = VerifyCommandResolver.Resolve(
            workspaceDirectory: null, overrideCommand: "echo ok && exit 0", roleVerifyPixiTask: null);

        var (runnable, reason) = await VerifyCommandResolver.CheckRunnableAsync(resolved!, workingDirectory: null, CancellationToken.None);

        Assert.True(runnable);
        Assert.Null(reason);
    }

    [Fact]
    public async Task CheckRunnableAsync_role_default_reports_runnable_when_pixi_itself_cannot_spawn()
    {
        // Pins the CheckPixiTaskAsync BatonException arm's own contract -- see its comment for why.
        var resolved = VerifyCommandResolver.Resolve(
            workspaceDirectory: null, overrideCommand: null, roleVerifyPixiTask: "gates-quiet");

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
