using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// Integration coverage for #1788's own wiring into <see cref="MutationInterface"/>: the delivery
/// check runs through the REAL pump against a real git workspace (no mocking of
/// <see cref="Baton.Core.BatonTask"/>, the same M7 Phase 7 acceptance criteria
/// <c>MutationInterfaceTests</c> holds itself to), gated on <see cref="WorkerBinding.Process.DeliversBranch"/>.
/// <see cref="DeliveryVerifierTests"/> owns <see cref="DeliveryVerifier"/>'s own unit coverage; this
/// file only proves the gate and the event wiring.
/// </summary>
public sealed class MutationInterfaceDeliveryVerificationTests
{
    private static readonly StepId Implementer = new("implementer");

    [Fact]
    public async Task A_delivers_branch_role_that_pushed_cleanly_settles_Succeeded()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-pass");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget("cmd", ["/c", "echo done>%BATON_OUTPUT_DIR%\\changes.md"], WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Implementer).Status);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_delivers_branch_role_that_commits_without_pushing_settles_Indeterminate_with_branch_not_pushed()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-fail");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget(
                        "cmd",
                        ["/c", "git commit --allow-empty -m more -q && echo done>%BATON_OUTPUT_DIR%\\changes.md"],
                        WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    DeliversBranch: true,
                    ExpectPr: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);
            var stepState = finalState.Steps.Single(s => s.StepId == Implementer);

            // Indeterminate projects StepStatus.Failed with the awaiting-resolution flag set -- the
            // room-level word, not StepStatus itself, is what changes (spec/baton.md's Indeterminate
            // register entry).
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.True(stepState.IndeterminateAwaitingResolution);
            Assert.Equal(IndeterminateProducer.VerifyFailed, stepState.IndeterminateProducer);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            var verifyFailed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.DeliveryFailed, verifyFailed.Kind);
            Assert.Equal(["branch-not-pushed"], verifyFailed.FailingMembers);
            Assert.Contains("branch-not-pushed", stepState.IndeterminateReason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    [Fact]
    public async Task A_role_that_does_not_deliver_a_branch_never_runs_the_check_even_when_unpushed()
    {
        var (workspace, origin) = CreatePushedWorkspace("lane-readonly");
        TempGitRepository.CommitAll(workspace, "unpushed, but this role never gets checked for it");
        var (roomDirectory, artifactsRoot, logPath) = CreateRoomPaths();
        try
        {
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["implementer"] = new WorkerBinding.Process(
                    new WorkerContract("implementer", [], [new ProducedOutput("changes.md")], []),
                    new CoreDispatchTarget("cmd", ["/c", "echo done>%BATON_OUTPUT_DIR%\\changes.md"], WorkingDirectory: workspace),
                    TimeSpan.FromSeconds(30),
                    DeliversBranch: false),
            };

            var finalState = await RunSingleStepPumpAsync(roomDirectory, artifactsRoot, logPath, bindings);

            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Implementer).Status);
            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyNotRun>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
            DirectoryCleanup.DeleteRecursively(workspace);
            DirectoryCleanup.DeleteRecursively(origin);
        }
    }

    private static async Task<FlowState> RunSingleStepPumpAsync(
        string roomDirectory, string artifactsRoot, string logPath, IReadOnlyDictionary<string, WorkerBinding> bindings)
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snapshot-delivery"),
            new WorkflowTemplateId("implementer-only"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(Implementer, "implementer", [], ["changes.md"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer, writer);

        return await MutationInterface.StartWorkflowAsync(
            new WorkflowId("wf-delivery"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) CreateRoomPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-delivery-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }

    private static (string Workspace, string Origin) CreatePushedWorkspace(string branch)
    {
        var origin = TempGitRepository.InitBareRepository(Path.Combine(Path.GetTempPath(), $"miv-origin-{Guid.NewGuid():N}"));
        var workspace = Path.Combine(Path.GetTempPath(), $"miv-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        TempGitRepository.InitWithEverythingCommitted(workspace);
        TempGitRepository.AddRemote(workspace, "origin", origin);
        TempGitRepository.CreateAndCheckoutBranch(workspace, branch);
        TempGitRepository.CommitAll(workspace, "lane work");
        TempGitRepository.Push(workspace, "origin", branch);
        return (workspace, origin);
    }
}
