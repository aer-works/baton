using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Tests.Shared;
using Baton.Workspaces;

namespace Baton.Tests.Outcomes;

/// <summary>
/// #1622 (b) / #1390: work-product evidence on a Succeeded verdict for a tree-changing role. A
/// permission-blocked or otherwise no-op worker can exit 0 with a satisfied (often zero-output)
/// contract and settle Succeeded with nothing durable in the tree — #1390's own measured defect.
/// <see cref="OutcomeClassifier.Classify"/>'s <c>changesTree</c> parameter is what turns this
/// evidence on; these tests exercise it against a real git worktree, the same discipline
/// <see cref="Workspaces.WorktreeProvisionerTests"/> uses, since a fake would not discriminate a git
/// status/rev-list probe.
/// </summary>
public sealed class OutcomeClassifierWorkProductTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "baton-outcome-workproduct-" + Guid.NewGuid().ToString("N"));

    public OutcomeClassifierWorkProductTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_tree_changing_role_with_no_diff_and_no_declared_outputs_settles_hollow_true()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        var contract = new WorkerContract("implement", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, "asked for approval; nothing answered"),
            contract, outputDirectory, worktreePath: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.False(classification.WorkspaceChanged);
        Assert.True(classification.Hollow);
        Assert.NotNull(classification.HollowReason);
    }

    [Fact]
    public void A_tree_changing_role_with_a_diff_settles_workspaceChanged_true_and_hollow_false()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(worktree, "new-file.txt"), "real work");
        var contract = new WorkerContract("implement", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, worktreePath: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.True(classification.WorkspaceChanged);
        Assert.False(classification.Hollow);
        Assert.Null(classification.HollowReason);
    }

    /// <summary>
    /// The polarity control for the hollow test above: an untouched worktree alone is not enough for
    /// <c>hollow: true</c> when the contract DOES declare an output (every shipped catalog role but a
    /// bespoke zero-output one) — see <c>OutcomeClassifier.BuildSucceededClassification</c>'s own
    /// remarks for why. <c>workspaceChanged</c> still reads false; only <c>hollow</c> differs.
    /// </summary>
    [Fact]
    public void A_tree_changing_role_with_no_diff_but_a_declared_output_settles_workspaceChanged_false_and_hollow_false()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        File.WriteAllText(Path.Combine(outputDirectory, "changes.md"), "nothing changed");
        var contract = new WorkerContract("implement", [], [new ProducedOutput("changes.md")], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, worktreePath: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.False(classification.WorkspaceChanged);
        Assert.False(classification.Hollow);
    }

    /// <summary>
    /// #1622 (b)'s "a review role → field absent" arm: the identical untouched-worktree, no-outputs
    /// shape as the hollow test above, but with <c>changesTree: false</c> (a review/patch/fact-check/
    /// advise/orchestrate role never sets it — <c>RoleDispatch.ToBinding</c>'s own grant-derived
    /// predicate). <c>WorkspaceChanged</c>/<c>Hollow</c> must stay null, not merely false, so
    /// <c>WorkflowStatusStepView</c>'s <c>JsonIgnoreCondition.WhenWritingNull</c> omits the fields
    /// entirely rather than asserting "unchanged" about a role whose contract was never "change the
    /// tree" in the first place.
    /// </summary>
    [Fact]
    public void A_non_tree_changing_role_never_computes_workspaceChanged_or_hollow()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        var contract = new WorkerContract("review", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, worktreePath: worktree, changesTree: false);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.Null(classification.WorkspaceChanged);
        Assert.Null(classification.Hollow);
        Assert.Null(classification.HollowReason);
    }

    /// <summary>
    /// A hollow success never reclassifies the room word: spec/baton.md §3 and #1622's own instruction
    /// leave "should a hollow success be Failed instead" to the operator. This pins that the verdict
    /// stays Succeeded even in the strongest hollow case.
    /// </summary>
    [Fact]
    public void Hollow_success_does_not_reclassify_the_verdict()
    {
        var (worktree, outputDirectory) = ProvisionUntouchedWorktree();
        var contract = new WorkerContract("implement", [], [], []);

        var classification = OutcomeClassifier.Classify(
            new CoreDispatchResult(0, CoreExitReason.Natural, null),
            contract, outputDirectory, worktreePath: worktree, changesTree: true);

        Assert.Equal(OutcomeVerdict.Succeeded, classification.Verdict);
        Assert.True(classification.Hollow);
    }

    /// <summary>
    /// A real git repository with one commit. Returns the repo path and the branch to provision a
    /// worktree of, mirroring <c>Workspaces.WorktreeProvisionerTests.CreateRepoWithBranch</c>.
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
        RunGit(repo, "branch", "worker-target");
        return (repo, "worker-target");
    }

    /// <summary>
    /// Provisions a real, freshly-checked-out worktree (no diff, no commits over base) plus a fresh
    /// output directory — the shared setup every test in this class starts from, mutating the worktree
    /// or the output directory afterward as its own fixture shape needs.
    /// </summary>
    private (string Worktree, string OutputDirectory) ProvisionUntouchedWorktree()
    {
        var (repo, reference) = CreateRepoWithBranch("committed.txt");
        var worktree = Path.Combine(NewDir("task"), "workspace");
        WorktreeProvisioner.Provision(worktree, repo, reference);
        var outputDirectory = NewDir("output");
        return (worktree, outputDirectory);
    }

    private string NewDir(string name)
    {
        var path = Path.Combine(_root, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        DirectoryCleanup.DeleteRecursively(_root);
    }
}
