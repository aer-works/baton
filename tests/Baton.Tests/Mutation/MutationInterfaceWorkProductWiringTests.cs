using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.Shared;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1622 (b)/#1390, second-reader finding 1: <see cref="OutcomeClassifierWorkProductTests"/> calls
/// <c>OutcomeClassifier.Classify</c> directly and so never exercised the actual wiring
/// <see cref="MutationInterface"/> feeds it — every real <c>implement</c>/<c>janitor</c> dispatch has
/// <c>WorkerBinding.Process.IsWorktree == false</c> (a tree-changing role's <c>WriteFiles</c> grant
/// means <c>Baton.Vendors.RoleDispatch.ToBinding</c> never auto-provisions a worktree for it), and the
/// live-dispatch call site used to gate the path handed to the workspace probe behind that same
/// <c>IsWorktree</c> flag — so <c>workspaceChanged</c> read <c>true</c> unconditionally for every real
/// dispatch, regardless of what the worker actually did. These tests dispatch through the real,
/// managed engine (same discipline as <see cref="MutationInterfaceTests"/>) with the binding's
/// <c>IsWorktree</c> flag left <c>false</c> throughout — the shape <c>RoleDispatch.ToBinding</c>
/// actually produces — over the two workspace shapes a real dispatch lands in:
/// <list type="bullet">
/// <item>an operator-created <c>git worktree add</c> tree (this very checkout's own shape), where the
/// probe measures and <c>workspaceChanged</c>/<c>hollow</c> read the real state; and</item>
/// <item>a plain checkout with no <c>@{upstream}</c> — the ordinary state after <c>git switch -c</c> —
/// where the probe cannot measure at all and both fields must be ABSENT rather than fabricated
/// (F2/F7, #1720 review: this class's summary previously claimed the plain-checkout shape while every
/// arm used <c>git worktree add</c>, and that unclaimed branch was the one the defect lived on).</item>
/// </list>
/// </summary>
public sealed class MutationInterfaceWorkProductWiringTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "baton-workproduct-wiring-" + Guid.NewGuid().ToString("N"));

    public MutationInterfaceWorkProductWiringTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task A_tree_changing_dispatch_with_no_auto_provisioned_worktree_still_reads_the_real_workspace_state()
    {
        // A git worktree the OPERATOR set up by hand (`git worktree add`), never
        // `WorktreeProvisioner.Provision` -- the exact shape #1622's own worktree (this very
        // checkout) is, and the shape every real `implement`/`janitor` dispatch runs a worker in,
        // since RoleDispatch.ToBinding never auto-provisions one for a WriteFiles:true role (see this
        // class's own remarks). `IsWorktree(path)`'s static filesystem probe (WorktreeProvisioner.cs)
        // reads this as a real worktree regardless of who created it -- unlike WorkerBinding.Process's
        // own `IsWorktree` flag, which only `WorktreeWorkspaces.Provision` ever sets.
        var repo = NewDir("repo");
        RunGit(repo, "init", "-b", "main");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, "committed.txt"), "committed content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");
        var worker = Path.Combine(NewDir("worker-parent"), "worker");
        RunGit(repo, "worktree", "add", worker, "-b", "worker-branch", "main");

        var roomDirectory = Path.Combine(_root, $"room-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var stepId = new StepId("implement-step");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-workproduct-wiring"),
            new WorkflowTemplateId("implement"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(stepId, "implement", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["implement"] = new WorkerBinding.Process(
                new WorkerContract("implement", [], [], []),
                ExitCleanlyWithoutWriting() with { WorkingDirectory = worker },
                TimeSpan.FromSeconds(30),
                // IsWorktree defaults false -- the exact shape RoleDispatch.ToBinding produces for
                // every real implement/janitor dispatch, which never gets an auto-provisioned worktree.
                ChangesTree: true),
        };

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var finalState = await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-workproduct-wiring"), roomDirectory, snapshot, bindings, artifactsRoot,
            reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

        var stepState = Assert.Single(finalState.Steps);
        Assert.Equal(StepStatus.Succeeded, stepState.Status);
        Assert.False(stepState.WorkspaceChanged);
        Assert.True(stepState.Hollow);
    }

    /// <summary>
    /// F2 (#1720 review): the arm the class summary always claimed and never had. A plain checkout on
    /// a locally-created branch has no <c>@{upstream}</c> to count commits against, so the probe
    /// cannot answer — and the fix's whole point is that "cannot measure" renders as ABSENCE. Before
    /// it, this same run reported <c>workspaceChanged: true</c> (the negation of a fail-closed
    /// <c>false</c>) for a worker that did nothing at all, and <c>hollow</c> could never fire here.
    /// </summary>
    [Fact]
    public async Task A_tree_changing_dispatch_on_a_plain_checkout_with_no_upstream_reports_no_work_product_evidence()
    {
        var repo = NewDir("plain-repo");
        RunGit(repo, "init", "-b", "main");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repo, "committed.txt"), "committed content");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");

        var roomDirectory = Path.Combine(_root, $"room-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var stepId = new StepId("implement-step");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-workproduct-wiring-plain"),
            new WorkflowTemplateId("implement"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(stepId, "implement", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var bindings = new Dictionary<string, WorkerBinding>
        {
            ["implement"] = new WorkerBinding.Process(
                new WorkerContract("implement", [], [], []),
                ExitCleanlyWithoutWriting() with { WorkingDirectory = repo },
                TimeSpan.FromSeconds(30),
                ChangesTree: true),
        };

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var finalState = await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-workproduct-wiring-plain"), roomDirectory, snapshot, bindings, artifactsRoot,
            reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

        var stepState = Assert.Single(finalState.Steps);
        Assert.Equal(StepStatus.Succeeded, stepState.Status);
        Assert.Null(stepState.WorkspaceChanged);
        Assert.Null(stepState.Hollow);
        Assert.Null(stepState.HollowReason);
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
