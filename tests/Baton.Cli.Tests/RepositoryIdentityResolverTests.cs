using System.Diagnostics;
using Baton.Accounting;

namespace Baton.Cli.Tests;

/// <summary>
/// #1849: <see cref="RepositoryIdentity"/>'s pure derivation is covered by <c>RepositoryIdentityTests</c>
/// against strings. This file covers the seam that decides WHICH strings — and it does so against real
/// git, because the worktree-convergence claim rests entirely on this type probing
/// <c>--git-common-dir</c> rather than <c>--git-dir</c>, and no string-level test can tell those apart.
/// A real <c>git worktree add</c> is the only instrument that can.
/// </summary>
public sealed class RepositoryIdentityResolverTests
{
    [Fact]
    public async Task A_linked_worktree_and_its_main_checkout_resolve_to_one_identity_with_no_remote()
    {
        // No remote on purpose: with one, both would converge through the origin URL and the
        // common-dir half -- the half a remote-less repository depends on entirely -- would go
        // unexercised. `--git-dir` would answer `<main>/.git/worktrees/<name>` from inside the linked
        // worktree and split the ledger one file per checkout, which is exactly the bug this excludes.
        var root = NewTempDirectory();
        try
        {
            var main = Path.Combine(root, "main");
            var linked = Path.Combine(root, "linked");
            await InitGitRepoAsync(main);
            await RunGitAsync(main, "worktree", "add", "-q", "-b", "side", linked);

            var fromMain = await RepositoryIdentityResolver.TryResolveAsync(main, TestContext.Current.CancellationToken);
            var fromLinked = await RepositoryIdentityResolver.TryResolveAsync(linked, TestContext.Current.CancellationToken);

            Assert.NotNull(fromMain);
            Assert.NotNull(fromLinked);
            Assert.Equal(fromMain.Value, fromLinked.Value);
            Assert.Equal(fromMain.FileSlug, fromLinked.FileSlug);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task Two_unrelated_repositories_resolve_to_two_identities()
    {
        // The control for the arm above: without it, a resolver that returned one constant for every
        // directory would pass convergence and pool the whole fleet into a single ledger.
        var root = NewTempDirectory();
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            await InitGitRepoAsync(first);
            await InitGitRepoAsync(second);

            var a = await RepositoryIdentityResolver.TryResolveAsync(first, TestContext.Current.CancellationToken);
            var b = await RepositoryIdentityResolver.TryResolveAsync(second, TestContext.Current.CancellationToken);

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.NotEqual(a.Value, b.Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task An_origin_remote_decides_the_identity_even_across_two_separate_clones()
    {
        // Two independent checkouts of one repository share no `.git` at all, so only the remote can
        // converge them -- and it must, or a second clone starts a second ledger for the same project.
        var root = NewTempDirectory();
        try
        {
            var first = Path.Combine(root, "clone-a");
            var second = Path.Combine(root, "clone-b");
            await InitGitRepoAsync(first);
            await InitGitRepoAsync(second);
            await RunGitAsync(first, "remote", "add", "origin", "https://github.com/aer-works/baton.git");
            await RunGitAsync(second, "remote", "add", "origin", "git@github.com:AER-Works/Baton.git");

            var a = await RepositoryIdentityResolver.TryResolveAsync(first, TestContext.Current.CancellationToken);
            var b = await RepositoryIdentityResolver.TryResolveAsync(second, TestContext.Current.CancellationToken);

            Assert.Equal("github.com/aer-works/baton", a!.Value);
            Assert.Equal(a.Value, b!.Value);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_resolves_to_nothing_rather_than_throwing()
    {
        // What the settle site reads as "no row for this room". It must be an answer, not an exception
        // -- an accounting write never gates a run that already reached Terminal.
        var root = NewTempDirectory();
        try
        {
            var identity = await RepositoryIdentityResolver.TryResolveAsync(root, TestContext.Current.CancellationToken);
            Assert.Null(identity);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public async Task A_missing_directory_resolves_to_nothing()
    {
        Assert.Null(await RepositoryIdentityResolver.TryResolveAsync(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken));
        Assert.Null(await RepositoryIdentityResolver.TryResolveAsync("   ", TestContext.Current.CancellationToken));
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repo-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task InitGitRepoAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        // -c identity keeps the commit independent of any (absent) global git config on the runner.
        await RunGitAsync(
            directory, "-c", "user.email=test@example.invalid", "-c", "user.name=Test",
            "commit", "--allow-empty", "-q", "-m", "base");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git — is it on PATH? These tests need git.");
        var (stdout, stderr) = await BoundedProcessWait.RunToExitAsync(
            process, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr.Trim()}");
        }
    }
}
