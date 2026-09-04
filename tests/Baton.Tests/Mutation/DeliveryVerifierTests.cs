using Baton.Mutation;
using Baton.Tests.TestSupport;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="DeliveryVerifier"/> (#1788) — see its own class doc for the contract and
/// spec/baton.md §3 for the "Post-exit delivery check" register entry. Real git against a local bare
/// "origin" (a push/fetch round-trip needs a real remote, not <c>TempGitRepository</c>'s ref-only
/// baseline shortcut) plus a fake <c>gh</c> stand-in script, mirroring
/// <c>VerifyCommandResolverTests</c>' own "real git, fake pixi/gh binary name" pattern.
/// </summary>
public sealed class DeliveryVerifierTests
{
    [Fact]
    public async Task Pushed_branch_with_an_open_PR_passes()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-a");
        try
        {
            var gh = WriteFakeGh(workspace, """[{"number":42}]""");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.Equal(DeliveryCheckStatus.Passed, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task ExpectPr_false_skips_the_PR_check_even_with_no_gh_available()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-b");
        try
        {
            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: false, TestContext.Current.CancellationToken,
                ghProgram: "this-is-not-a-real-gh-binary-12345");

            Assert.Equal(DeliveryCheckStatus.Passed, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task Unpushed_local_commits_on_top_of_a_pushed_branch_fail_branch_not_pushed()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-c");
        try
        {
            // A local commit made AFTER the push above -- origin has the branch, but HEAD has moved
            // past it, exactly #1788's own measured defect (2/3 commits ahead of origin, no PR).
            TempGitRepository.CommitAll(workspace, "one more change, never pushed");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
            Assert.Contains("branch-not-pushed", outcome.Tail, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_branch_that_was_never_pushed_at_all_fails_branch_not_pushed()
    {
        // The `git ls-remote --exit-code` arm (exit 2, ref absent) rather than the `merge-base` arm
        // above -- the loudest version of #1788's defect: nothing was ever pushed, not merely a few
        // trailing commits.
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            TempGitRepository.CreateAndCheckoutBranch(workspace, "never-pushed");
            TempGitRepository.CommitAll(workspace, "local only");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// The discriminating control for the explicit-refspec fetch. Real git DOES opportunistically update
    /// <c>refs/remotes/origin/&lt;branch&gt;</c> on a plain <c>git fetch origin &lt;branch&gt;</c> when the
    /// remote's own <c>remote.origin.fetch</c> setting already covers that ref (measured; an earlier
    /// draft of this test/its docs claimed otherwise) — so this test removes that configuration entirely
    /// (<c>git config --unset-all remote.origin.fetch</c>) before also deleting the locally cached ref,
    /// reproducing a workspace with NO way to recover it except via an explicit refspec. Under the bare
    /// fetch form this would leave the ref absent and <c>merge-base --is-ancestor HEAD
    /// origin/&lt;branch&gt;</c> unable to resolve it (a non-0/1 exit this class reads as
    /// <see cref="DeliveryCheckStatus.NotRun"/>, never a fabricated pass or failure). Only the
    /// <c>+refs/heads/&lt;branch&gt;:refs/remotes/origin/&lt;branch&gt;</c> form recreates the ref
    /// regardless, and lets the check resolve to <see cref="DeliveryCheckStatus.Passed"/>.
    /// </summary>
    [Fact]
    public async Task A_workspace_with_no_locally_cached_tracking_ref_still_resolves_via_the_refspec_fetch()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-g");
        try
        {
            RunGit(workspace, "config", "--unset-all", "remote.origin.fetch");
            RunGit(workspace, "update-ref", "-d", "refs/remotes/origin/feature-g");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Passed, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// The <c>--heads</c> scoping's own control (#1788 review): a TAG on origin sharing the branch's
    /// name must not make <c>ls-remote --exit-code</c> read as "the branch exists" — measured directly
    /// against real git, an unscoped query matches tags too, which would defer to a fetch that then
    /// fails to resolve <c>refs/heads/&lt;branch&gt;</c> and downgrade this real "never pushed" into a
    /// misleading <see cref="DeliveryCheckStatus.NotRun"/> instead.
    /// </summary>
    [Fact]
    public async Task A_same_named_tag_on_origin_does_not_mask_a_branch_that_was_never_pushed()
    {
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            RunGit(workspace, "tag", "ghost");
            RunGit(workspace, "push", "origin", "ghost");
            TempGitRepository.CreateAndCheckoutBranch(workspace, "ghost");
            TempGitRepository.CommitAll(workspace, "local only, never pushed as a branch");

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task Pushed_branch_with_no_open_PR_fails_pr_not_open()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-d");
        try
        {
            var gh = WriteFakeGh(workspace, "[]");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: gh);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["pr-not-open"], outcome.FailingMembers);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    /// <summary>
    /// #1788 review: an exit-0 <c>gh pr list</c> whose stdout does not parse as the JSON array it always
    /// emits on success must never fabricate "a PR exists" (nor "no PR" — the class doc's own refused
    /// fabrication in the other direction). A wrapper script or a truncated pipe is the realistic cause;
    /// this fixture just returns plain unparseable text with exit 0.
    /// </summary>
    [Fact]
    public async Task Unparseable_gh_output_on_a_successful_exit_reports_NotRun_rather_than_a_pass()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-h");
        try
        {
            var path = Path.Combine(workspace, $"fake-gh-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(path, "@echo off\necho not-json-at-all\nexit /b 0\n");

            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken, ghProgram: path);

            Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
            Assert.NotNull(outcome.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_cancellation_requested_before_the_check_starts_reports_Cancelled()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-i");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, cts.Token);

            Assert.Equal(DeliveryCheckStatus.Cancelled, outcome.Status);
            Assert.Null(outcome.FailingMembers);
            Assert.Null(outcome.NotRunReason);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_missing_gh_binary_reports_NotRun_rather_than_a_pass_or_a_failure()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-e");
        try
        {
            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: true, TestContext.Current.CancellationToken,
                ghProgram: "this-is-not-a-real-gh-binary-12345");

            Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
            Assert.NotNull(outcome.NotRunReason);
            Assert.Contains("gh", outcome.NotRunReason, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_missing_git_binary_reports_NotRun_rather_than_a_pass_or_a_failure()
    {
        var (workspace, origin) = CreatePushedWorkspace("feature-f");
        try
        {
            var outcome = await DeliveryVerifier.CheckAsync(
                workspace, expectPr: false, TestContext.Current.CancellationToken,
                gitProgram: "this-is-not-a-real-git-binary-12345");

            Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task A_detached_HEAD_fails_branch_not_pushed_rather_than_NotRun()
    {
        // #1788: checking out a raw commit (`git checkout <sha>`) leaves git DETACHED, whatever
        // provisioned the workspace this way -- confirmed directly against real git for this issue. A
        // worker that exits 0 without ever checking out a named branch has delivered nothing pushable,
        // which is a real failure, not merely unmeasurable.
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            TempGitRepository.InitWithEverythingCommitted(workspace);
            TempGitRepository.AddRemote(workspace, "origin", origin);
            var sha = GitRevParseHead(workspace);
            GitCheckout(workspace, sha);

            var outcome = await DeliveryVerifier.CheckAsync(workspace, expectPr: false, TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryCheckStatus.Failed, outcome.Status);
            Assert.Equal(["branch-not-pushed"], outcome.FailingMembers);
            Assert.Contains("detached", outcome.Tail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(workspace, origin);
        }
    }

    [Fact]
    public async Task No_working_directory_reports_NotRun()
    {
        var outcome = await DeliveryVerifier.CheckAsync(null, expectPr: true, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryCheckStatus.NotRun, outcome.Status);
    }

    // ---- fixtures ----

    private static (string Workspace, string Origin) CreatePushedWorkspace(string branch)
    {
        var origin = TempGitRepository.InitBareRepository(TempPath("origin"));
        var workspace = TempPath("workspace");
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.AddRemote(workspace, "origin", origin);
        TempGitRepository.CreateAndCheckoutBranch(workspace, branch);
        TempGitRepository.CommitAll(workspace, "lane work");
        TempGitRepository.Push(workspace, "origin", branch);
        return (workspace, origin);
    }

    private static string TempPath(string label) => Path.Combine(Path.GetTempPath(), $"dv-{label}-{Guid.NewGuid():N}");

    private static void Cleanup(string workspace, string origin)
    {
        DirectoryCleanup.DeleteRecursively(workspace);
        DirectoryCleanup.DeleteRecursively(origin);
    }

    private static string WriteFakeGh(string directory, string jsonOutput)
    {
        var path = Path.Combine(directory, $"fake-gh-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, $"@echo off\necho {jsonOutput}\nexit /b 0\n");
        return path;
    }

    private static string GitRevParseHead(string workspace) =>
        RunGit(workspace, "rev-parse", "HEAD").Trim();

    private static void GitCheckout(string workspace, string commitish) => RunGit(workspace, "checkout", commitish);

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not spawn git {string.Join(' ', args)}.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }

        return stdout;
    }
}
