using System.Diagnostics;
using Aer.Flow.Workspaces;
using Aer.Tests.Shared;

namespace Aer.Flow.Tests.Workspaces;

/// <summary>
/// Covers the engine half of #669: standing a worker up in an isolated worktree, and the three honest
/// teardown outcomes. Uses a real on-disk git repository per test — the provisioner shells out to git,
/// so a fake would not discriminate.
/// </summary>
public sealed class WorktreeProvisionerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "aer-worktree-" + Guid.NewGuid().ToString("N"));

    public WorktreeProvisionerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ValidateSpec_refuses_a_relative_repository_path()
    {
        var ex = Assert.Throws<InvalidWorkspaceSpecException>(
            () => WorktreeProvisioner.ValidateSpec("some/relative/repo", "main"));
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSpec_refuses_an_empty_ref()
    {
        var absolute = Path.Combine(_root, "repo");
        Assert.Throws<InvalidWorkspaceSpecException>(
            () => WorktreeProvisioner.ValidateSpec(absolute, "  "));
    }

    [Fact]
    public void ValidateSpec_accepts_an_absolute_repository_and_a_ref()
    {
        // No throw: the happy shape a real dispatch passes.
        WorktreeProvisioner.ValidateSpec(Path.Combine(_root, "repo"), "review-target");
    }

    [Fact]
    public void Provision_checks_out_the_requested_ref_into_a_new_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        WorktreeProvisioner.Provision(worktree, repo, reference);

        Assert.True(Directory.Exists(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, "committed.txt")),
            "the ref's committed file should be checked out into the worktree");
    }

    [Fact]
    public void Provision_throws_a_typed_error_when_the_ref_does_not_exist()
    {
        var (repo, _) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        Assert.Throws<WorktreeProvisioningException>(
            () => WorktreeProvisioner.Provision(worktree, repo, "no-such-ref"));
    }

    [Fact]
    public void Provision_is_idempotent_when_called_again_for_the_same_worktree_repo_and_ref()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");

        // First call provisions the worktree
        WorktreeProvisioner.Provision(worktree, repo, reference);
        Assert.True(Directory.Exists(worktree));

        // Second call for the exact same worktree/repo/ref simulates winning the race: must not throw
        WorktreeProvisioner.Provision(worktree, repo, reference);
        Assert.True(Directory.Exists(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, "committed.txt")));
    }

    [Fact]
    public void Provision_throws_when_worktree_path_is_occupied_by_a_different_ref()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        // Create another ref pointing to a different commit
        File.WriteAllText(Path.Combine(repo, "second.txt"), "second content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "second commit");
        RunGit(repo, "branch", "other-ref");

        var worktree = Path.Combine(NewDir("task"), "workspace");

        // Provision for reference ("review-target") first
        WorktreeProvisioner.Provision(worktree, repo, reference);

        // Attempting to provision the same worktree path for a DIFFERENT ref ("other-ref") must throw
        Assert.Throws<WorktreeProvisioningException>(
            () => WorktreeProvisioner.Provision(worktree, repo, "other-ref"));
    }

    [Fact]
    public void Provision_throws_when_path_is_occupied_by_unrelated_directory()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "unrelated.txt"), "data");

        Assert.Throws<WorktreeProvisioningException>(
            () => WorktreeProvisioner.Provision(worktree, repo, reference));
    }

    [Fact]
    public void Teardown_removes_a_clean_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        var result = WorktreeProvisioner.Teardown(repo, worktree);

        Assert.Equal(WorktreeTeardownOutcome.Removed, result.Outcome);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public void Teardown_keeps_a_worktree_that_carries_uncommitted_changes()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        // A worker's not-yet-committed output. Discarding it is worse than leaving a directory behind.
        File.WriteAllText(Path.Combine(worktree, "worker-output.md"), "half-written result");

        var result = WorktreeProvisioner.Teardown(repo, worktree);

        Assert.Equal(WorktreeTeardownOutcome.KeptUncommitted, result.Outcome);
        Assert.True(Directory.Exists(worktree));
        Assert.True(File.Exists(Path.Combine(worktree, "worker-output.md")));
    }

    [Fact]
    public void Teardown_reports_rather_than_throwing_when_removal_is_blocked()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        // A locked worktree stands in for the real blocker (a live build process holding an output):
        // it makes `git worktree remove` fail deterministically on every platform, where a held file
        // handle only blocks removal on Windows. What is under test is the handling — report, don't
        // throw, so the completed task still terminates cleanly — not the specific cause.
        RunGit(repo, "worktree", "lock", worktree);

        var result = WorktreeProvisioner.Teardown(repo, worktree);

        Assert.Equal(WorktreeTeardownOutcome.RemovalBlocked, result.Outcome);
        Assert.True(Directory.Exists(worktree));
        Assert.NotNull(result.Detail);

        RunGit(repo, "worktree", "unlock", worktree); // so cleanup can delete the tree
    }

    [Fact]
    public void IsWorktree_returns_true_for_a_provisioned_worktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);

        Assert.True(WorktreeProvisioner.IsWorktree(worktree));
    }

    [Fact]
    public void IsWorktree_returns_false_for_a_main_repo()
    {
        var (repo, _) = CreateRepoWithBranch("committed.txt");

        Assert.False(WorktreeProvisioner.IsWorktree(repo));
    }

    [Fact]
    public void IsWorktree_returns_false_for_non_existent_or_non_git_path()
    {
        Assert.False(WorktreeProvisioner.IsWorktree(_root));
        Assert.False(WorktreeProvisioner.IsWorktree(Path.Combine(_root, "nonexistent")));
    }

    // --- fixture ---

    private string NewDir(string name)
    {
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A real git repository with one commit and a branch <c>review-target</c> that is not checked out
    /// in the main tree (so a worktree can take it). Returns the repo path and that ref name.
    /// </summary>
    private (string Repository, string Reference) CreateRepoWithBranch(string committedFileName)
    {
        var repo = NewDir("repo");
        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, committedFileName), "committed content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");
        RunGit(repo, "branch", "review-target");
        return (repo, "review-target");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr.Result}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // git's committed object files are read-only by design, which Windows' Directory.Delete refuses
        // to remove; clear the attribute first so cleanup succeeds on every OS.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(_root);
    }

    /// <summary>
    /// #1103: git prints macOS temp paths in their resolved <c>/private/...</c> spelling while
    /// callers hold the <c>/var/...</c> symlink spelling; equality must see through that or the
    /// idempotence check above can never match on macOS. Pure string logic, so this discriminates
    /// on every platform — it is the unit-level pin for the macos-only CI failure.
    /// </summary>
    [Theory]
    [InlineData("/private/var/folders/x/task/workspace", "/var/folders/x/task/workspace", true)]
    [InlineData("/private/tmp/task", "/tmp/task", true)]
    [InlineData("/var/folders/x/a", "/var/folders/x/b", false)]
    [InlineData("/private/var/folders/x/a", "/var/folders/y/a", false)]
    public void Normalization_sees_through_the_macos_private_symlink_spelling(string a, string b, bool expected)
    {
        // Targets the normalization itself rather than PathsEqual: PathsEqual runs GetFullPath
        // first, which on Windows would re-root these POSIX literals under the current drive and
        // turn this into a test of GetFullPath instead of the /private equivalence.
        var equal = string.Equals(
            WorktreeProvisioner.NormalizeForComparison(a),
            WorktreeProvisioner.NormalizeForComparison(b),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, equal);
    }
}
