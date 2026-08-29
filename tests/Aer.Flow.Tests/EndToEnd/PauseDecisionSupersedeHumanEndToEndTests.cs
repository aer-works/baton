using Aer.Flow.Tests.TestSupport;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using static Aer.Flow.Tests.TestSupport.ShellWorkerCommands;

namespace Aer.Flow.Tests.EndToEnd;

/// <summary>
/// M9's completion gate (issue #61), playing #14's and #48's role for the pause/decision/supersede/
/// human machinery: every such behavior proved against real processes on a real filesystem, the
/// test acting as the human throughout — dropping files into pre-allocated output directories
/// across separate mutation-interface calls is exactly the model of a non-process party. No
/// mocking of Aer.Core itself, same discipline <see cref="WorkflowEndToEndTests"/> follows.
/// </summary>
public class PauseDecisionSupersedeHumanEndToEndTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId H = new("h");
    private static readonly StepId Flaky = new("flaky");
    private static readonly StepId Downstream = new("downstream");
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");

    [Fact]
    public async Task An_approval_gate_pauses_A_then_Resume_runs_B_to_the_fixed_point()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = await LoadSnapshotAsync("approval-gate-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a"] = new WorkerBinding.Process(
                    new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                    WriteFile("out_a", "a-out"),
                    TimeSpan.FromSeconds(30)),
                ["b"] = new WorkerBinding.Process(
                    new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                    CopyFirstInputTo("out_b"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-approval-gate");

            var pausedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            // First pump ends WorkflowPaused with B never dispatched, Flow idle, no process alive.
            Assert.Equal(WorkflowStatus.Paused, pausedState.Status);
            Assert.Equal(StepStatus.Paused, pausedState.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, pausedState.Steps.Single(s => s.StepId == B).Status);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.StepId == B);

            var pausedExecutionId = pausedState.Steps.Single(s => s.StepId == A).LatestExecutionId!.Value;

            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                pausedExecutionId, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == B).Status);
            await AssertOutputExistsAsync(artifactsRoot, finalState.Steps.Single(s => s.StepId == B), "out_b", "a-out");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Reject_on_a_successful_outcome_projects_A_terminally_failed_and_B_never_dispatches()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = await LoadSnapshotAsync("approval-gate-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a"] = new WorkerBinding.Process(
                    new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                    WriteFile("out_a", "a-out"),
                    TimeSpan.FromSeconds(30)),
                ["b"] = new WorkerBinding.Process(
                    new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                    CopyFirstInputTo("out_b"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-reject");

            var pausedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            var pausedExecutionId = pausedState.Steps.Single(s => s.StepId == A).LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Succeeded, pausedState.Steps.Single(s => s.StepId == A).PausedOutcome);

            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                pausedExecutionId, DecisionType.Reject, cancellationToken: TestContext.Current.CancellationToken);

            // A's ExecutionSucceeded stands in the log, yet it projects terminally failed — the
            // approval-gate "no".
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Rejected, finalState.Steps.Single(s => s.StepId == A).Status);
            Assert.Equal(StepStatus.Pending, finalState.Steps.Single(s => s.StepId == B).Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && succeeded.ExecutionId == pausedExecutionId);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionRequestAccepted accepted && accepted.Request.StepId == B);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Exhaustion_then_a_supplementary_human_revision_then_RetryWithRevision_succeeds_and_downstream_runs()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var snapshot = await LoadSnapshotAsync("retry-with-revision-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["flaky"] = new WorkerBinding.Process(
                    new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                    ConsumeSupplementaryInputElseFail(scriptDirectory, "result", "revision.md"),
                    TimeSpan.FromSeconds(30)),
                ["downstream"] = new WorkerBinding.Process(
                    new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                    CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
                ["human"] = new WorkerBinding.NonProcess(
                    new WorkerContract("human", [], [new ProducedOutput("revision.md")], [])),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-retry-with-revision");

            var pausedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            // Exactly two real attempts (RetryPolicy.MaxAttempts: 2), both failing with no
            // supplement, then pause — the M9 Phase 1 settled-round rule against real processes.
            var flakyExecutionIds = GetAcceptedExecutionIds(await reader.ReadAllAsync(TestContext.Current.CancellationToken), Flaky);
            Assert.Equal(2, flakyExecutionIds.Count);
            var pausedExecutionId = pausedState.Steps.Single(s => s.StepId == Flaky).LatestExecutionId!.Value;
            Assert.Equal(StepStatus.Failed, pausedState.Steps.Single(s => s.StepId == Flaky).PausedOutcome);
            Assert.Equal(StepStatus.Pending, pausedState.Steps.Single(s => s.StepId == Downstream).Status);

            // A supplementary human execution supplies a revision file — the test is the human.
            var (mintedState, revisionExecutionId) = await MutationInterface.RecordSupplementaryExecutionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, "human", inputs: [], reader, writer, cancellationToken: TestContext.Current.CancellationToken);
            var revisionOutputDirectory = Path.Combine(artifactsRoot, $"execution_{revisionExecutionId}");
            await File.WriteAllTextAsync(Path.Combine(revisionOutputDirectory, "revision.md"), "revised-result", TestContext.Current.CancellationToken);
            Assert.Single(mintedState.StepLessExecutions);

            // A settling pump finalizes the supplementary execution (NonProcessCompletionDetector);
            // nothing else is ready, so this call is otherwise a no-op for the paused DAG.
            var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(settledState.StepLessExecutions);
            var succeededExecutionIds = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionSucceeded>()
                .Select(e => e.ExecutionId)
                .ToHashSet();
            Assert.Contains(revisionExecutionId, succeededExecutionIds);

            // RetryWithRevision names it; the worker reads the supplementary input path and succeeds.
            var retriedState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                pausedExecutionId, DecisionType.RetryWithRevision, supplementaryExecutionId: revisionExecutionId, cancellationToken: TestContext.Current.CancellationToken);

            var flakyAfterRetry = retriedState.Steps.Single(s => s.StepId == Flaky);
            Assert.Equal(StepStatus.Paused, flakyAfterRetry.Status);
            Assert.Equal(StepStatus.Succeeded, flakyAfterRetry.PausedOutcome);
            Assert.NotEqual(pausedExecutionId, flakyAfterRetry.LatestExecutionId);
            await AssertOutputExistsAsync(artifactsRoot, flakyAfterRetry, "result", "revised-result");

            // Flaky's PausePoint pauses again on this settled (successful) round too — a
            // second Resume is needed before downstream, which only cares about Succeeded, can run.
            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                flakyAfterRetry.LatestExecutionId!.Value, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Flaky).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Downstream).Status);
            await AssertOutputExistsAsync(artifactsRoot, finalState.Steps.Single(s => s.StepId == Downstream), "final", "revised-result");

            // Every attempt's artifact directory — both exhausted, the supplementary, and the
            // eventual success — persists untouched.
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{flakyExecutionIds[0]}")));
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{flakyExecutionIds[1]}")));
            Assert.True(Directory.Exists(revisionOutputDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task The_full_architect_critic_supersede_loop_reruns_both_steps_and_a_final_Resume_reaches_terminal()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var snapshot = await LoadSnapshotAsync("architect-critic-supersede-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ConsumeSupplementaryInputElseWrite(scriptDirectory, "plan", "feedback", "original-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("feedback")], []),
                    AppendSuffixToFirstInput(scriptDirectory, "feedback", "-feedback"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-architect-critic");

            var firstPauseState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, firstPauseState.Status);
            var architectExecutionId1 = firstPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId1 = firstPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;
            await AssertOutputExistsAsync(artifactsRoot, firstPauseState.Steps.Single(s => s.StepId == Architect), "plan", "original-plan");
            await AssertOutputExistsAsync(artifactsRoot, firstPauseState.Steps.Single(s => s.StepId == Critic), "feedback", "original-plan-feedback");
            var architectOutputDirectory1 = Path.Combine(artifactsRoot, $"execution_{architectExecutionId1}");

            // Critic's feedback artifact is its own successful execution,
            // naming Architect (its declared SupersedeTargets entry) as the target.
            var secondPauseState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId1, DecisionType.Supersede, targetStepId: Architect, supplementaryExecutionId: criticExecutionId1, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, secondPauseState.Status);
            var architectExecutionId2 = secondPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId2 = secondPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;
            Assert.NotEqual(architectExecutionId1, architectExecutionId2);
            Assert.NotEqual(criticExecutionId1, criticExecutionId2);

            // Architect's rerun consumed Critic's feedback artifact rather than writing its
            // default output again.
            await AssertOutputExistsAsync(artifactsRoot, secondPauseState.Steps.Single(s => s.StepId == Architect), "plan", "original-plan-feedback");

            // Critic reran automatically — nothing explicitly told Flow
            // to go back — against Architect's new plan, and paused again at the same PausePoint.
            await AssertOutputExistsAsync(
                artifactsRoot, secondPauseState.Steps.Single(s => s.StepId == Critic), "feedback", "original-plan-feedback-feedback");

            // Critic's second success records the second Architect execution in UpstreamExecutionIds.
            var criticAccepted2 = (await reader.ReadAllAsync(TestContext.Current.CancellationToken))
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Single(e => e.Request.ExecutionId == criticExecutionId2);
            Assert.Equal(architectExecutionId2, criticAccepted2.Request.UpstreamExecutionIds[Architect]);

            // A1's artifact directory is untouched on disk.
            Assert.True(Directory.Exists(architectOutputDirectory1));
            Assert.Equal("original-plan", (await File.ReadAllTextAsync(Path.Combine(architectOutputDirectory1, "plan"), TestContext.Current.CancellationToken)).Trim());

            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId2, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Architect).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Critic).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// M23 Phase 2's own acceptance bar (#271): a step already superseded once can legally be
    /// superseded a second time, once the first cycle's consequence has fully settled (Architect
    /// back to a fresh <see cref="StepStatus.Succeeded"/>, no longer <see cref="StepState.IsPendingSupersedeTarget"/>)
    /// — the exact chain M24's chat primitive depends on (repeatedly superseding one step, once per
    /// human turn). Extends <see cref="The_full_architect_critic_supersede_loop_reruns_both_steps_and_a_final_Resume_reaches_terminal"/>
    /// with a second Supersede cycle in place of that test's final Resume, through the real
    /// mutation interface end to end — not just <c>ExternalDecisionValidatorTests</c>' pure
    /// validation of the same rule.
    /// </summary>
    [Fact]
    public async Task A_second_Supersede_targeting_the_same_step_after_the_first_cycle_settles_reruns_it_again()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var snapshot = await LoadSnapshotAsync("architect-critic-supersede-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ConsumeSupplementaryInputElseWrite(scriptDirectory, "plan", "feedback", "original-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("feedback")], []),
                    AppendSuffixToFirstInput(scriptDirectory, "feedback", "-feedback"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-architect-critic-chained");

            var firstPauseState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            var architectExecutionId1 = firstPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId1 = firstPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;
            var architectOutputDirectory1 = Path.Combine(artifactsRoot, $"execution_{architectExecutionId1}");

            // Cycle 1, exactly as the sibling single-cycle test proves.
            var secondPauseState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId1, DecisionType.Supersede, targetStepId: Architect, supplementaryExecutionId: criticExecutionId1, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Paused, secondPauseState.Status);
            var architectExecutionId2 = secondPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId2 = secondPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;
            var architectOutputDirectory2 = Path.Combine(artifactsRoot, $"execution_{architectExecutionId2}");

            // Architect's first-cycle consequence has fully settled by now: back to a fresh
            // Succeeded, no longer a pending Supersede target — exactly what makes a second
            // Supersede against it legal (ExternalDecisionValidatorTests proves the pure rule; this
            // proves the real dispatch consequence).
            Assert.Equal(StepStatus.Succeeded, secondPauseState.Steps.Single(s => s.StepId == Architect).Status);
            Assert.False(secondPauseState.Steps.Single(s => s.StepId == Architect).IsPendingSupersedeTarget);

            // Cycle 2: the same target, a second time, naming Critic's *second* execution as the
            // new supplement — proving this isn't cycle 1 replayed, but a genuinely new decision.
            var thirdPauseState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId2, DecisionType.Supersede, targetStepId: Architect, supplementaryExecutionId: criticExecutionId2, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Paused, thirdPauseState.Status);
            var architectExecutionId3 = thirdPauseState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId3 = thirdPauseState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;
            Assert.NotEqual(architectExecutionId2, architectExecutionId3);
            Assert.NotEqual(criticExecutionId2, criticExecutionId3);

            // Architect's second rerun consumed Critic's *second* feedback artifact, not the first
            // cycle's stale one — the second decision's own supplement actually took effect.
            await AssertOutputExistsAsync(
                artifactsRoot, thirdPauseState.Steps.Single(s => s.StepId == Architect), "plan", "original-plan-feedback-feedback");
            await AssertOutputExistsAsync(
                artifactsRoot, thirdPauseState.Steps.Single(s => s.StepId == Critic), "feedback", "original-plan-feedback-feedback-feedback");

            // Every prior cycle's artifact directory persists untouched — cycle 2 didn't
            // erase or reuse cycle 1's.
            Assert.True(Directory.Exists(architectOutputDirectory1));
            Assert.Equal("original-plan", (await File.ReadAllTextAsync(Path.Combine(architectOutputDirectory1, "plan"), TestContext.Current.CancellationToken)).Trim());
            Assert.True(Directory.Exists(architectOutputDirectory2));
            Assert.Equal("original-plan-feedback", (await File.ReadAllTextAsync(Path.Combine(architectOutputDirectory2, "plan"), TestContext.Current.CancellationToken)).Trim());

            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId3, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Architect).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == Critic).Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_human_step_mid_DAG_pauses_the_pump_until_the_test_drops_its_output_then_downstream_runs()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = await LoadSnapshotAsync("human-mid-dag-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a"] = new WorkerBinding.Process(
                    new WorkerContract("a", [], [new ProducedOutput("draft")], []),
                    WriteFile("draft", "the-draft"),
                    TimeSpan.FromSeconds(30)),
                ["human"] = new WorkerBinding.NonProcess(
                    new WorkerContract("human", ["draft"], [new ProducedOutput("revision")], [])),
                ["c"] = new WorkerBinding.Process(
                    new WorkerContract("c", ["revision"], [new ProducedOutput("final")], []),
                    CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-human-mid-dag");

            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            // H was admitted without any Core process being requested — CoreDispatcher
            // would otherwise have spawned a real process for the "human" worker, which has no
            // Target to spawn at all; C never dispatches since H hasn't completed.
            Assert.Equal(StepStatus.Running, firstState.Steps.Single(s => s.StepId == H).Status);
            Assert.Equal(StepStatus.Pending, firstState.Steps.Single(s => s.StepId == C).Status);

            var hExecutionId = firstState.Steps.Single(s => s.StepId == H).LatestExecutionId!.Value;
            var hOutputDirectory = Path.Combine(artifactsRoot, $"execution_{hExecutionId}");
            Assert.True(Directory.Exists(hOutputDirectory));

            // The test is the human: it drops the contractually required output, across a separate
            // mutation-interface invocation.
            await File.WriteAllTextAsync(Path.Combine(hOutputDirectory, "revision"), "the-revision", TestContext.Current.CancellationToken);

            var finalState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == H).Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == C).Status);
            await AssertOutputExistsAsync(artifactsRoot, finalState.Steps.Single(s => s.StepId == C), "final", "the-revision");

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && succeeded.ExecutionId == hExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task The_invalid_decision_matrix_is_rejected_with_a_typed_error_and_appends_nothing()
    {
        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var snapshot = await LoadSnapshotAsync("architect-critic-supersede-workflow.json");
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("feedback")], []),
                    CopyFirstInputTo("feedback"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var workflowId = new WorkflowId("wf-invalid-decisions");

            var pausedState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);
            var architectExecutionId = pausedState.Steps.Single(s => s.StepId == Architect).LatestExecutionId!.Value;
            var criticExecutionId = pausedState.Steps.Single(s => s.StepId == Critic).LatestExecutionId!.Value;
            var eventCountBeforeInvalidDecisions = (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).Count;

            // Supersede naming a step outside its declared SupersedeTargets ([architect]).
            await Assert.ThrowsAsync<InvalidExternalDecisionException>(() => MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId, DecisionType.Supersede, targetStepId: Critic, supplementaryExecutionId: criticExecutionId, cancellationToken: TestContext.Current.CancellationToken));

            // Supersede without a SupplementaryExecutionId.
            await Assert.ThrowsAsync<InvalidExternalDecisionException>(() => MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId, DecisionType.Supersede, targetStepId: Architect, cancellationToken: TestContext.Current.CancellationToken));

            // A decision against a non-paused execution (Architect already succeeded and was
            // never paused itself).
            await Assert.ThrowsAsync<InvalidExternalDecisionException>(() => MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                architectExecutionId, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken));

            var eventCountAfterInvalidDecisions = (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).Count;
            Assert.Equal(eventCountBeforeInvalidDecisions, eventCountAfterInvalidDecisions);

            // The paused workflow is still perfectly resolvable by a valid decision afterward.
            var finalState = await MutationInterface.RecordDecisionAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
                criticExecutionId, DecisionType.Resume, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<WorkflowDefinitionSnapshot> LoadSnapshotAsync(string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);
        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath);
        return SnapshotBinder.Bind(definition);
    }

    private static async Task AssertOutputExistsAsync(
        string artifactsRoot, StepState stepState, string outputName, string? expectedContent = null)
    {
        var executionId = stepState.LatestExecutionId!.Value;
        var outputPath = Path.Combine(artifactsRoot, $"execution_{executionId}", outputName);

        Assert.True(File.Exists(outputPath));

        if (expectedContent is not null)
        {
            Assert.Equal(expectedContent, (await File.ReadAllTextAsync(outputPath)).Trim());
        }
    }

    private static IReadOnlyList<ExecutionId> GetAcceptedExecutionIds(IReadOnlyList<FlowEvent> events, StepId stepId) => events
        .OfType<FlowEvent.ExecutionRequestAccepted>()
        .Where(e => e.Request.StepId == stepId)
        .Select(e => e.Request.ExecutionId)
        .ToList();

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }
}
