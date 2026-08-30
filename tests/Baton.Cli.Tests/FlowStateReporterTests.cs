using Baton.Flow.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// M12 Phase 3's pause-aware reporting requirement (issue #97): without a paused step's
/// <see cref="ExecutionId"/> and declared <c>SupersedeTargets</c> printed somewhere, a terminal user
/// has no way to know what to pass to <c>baton decide --execution</c>/<c>--target-step</c>.
/// </summary>
public class FlowStateReporterTests
{
    /// <summary>
    /// #597: `baton run`'s own terminal output is named in the issue's acceptance criteria, not just
    /// `flow.jsonl`. Before this, a worker that exited 0 having written none of its declared outputs
    /// printed `worker: Failed` beside `ExitCode: 0` — which reads as an AER bug rather than a
    /// worker one and sent diagnosis in the wrong direction three times.
    /// </summary>
    /// <remarks>
    /// The succeeded step is the polarity control on the same output: a reporter that appended the
    /// suffix unconditionally, or dropped it entirely, fails one line or the other.
    /// </remarks>
    [Fact]
    public void A_failed_step_reports_the_reason_Flow_derived_and_a_succeeded_step_reports_none()
    {
        const string reason = "Contract not satisfied: 'plan' is missing";
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [
                new WorkflowStepDefinition(new StepId("source"), "source", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("later"), "later", [], ["out"], [], new RetryPolicy(1)),
            ]);

        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(
                    new StepId("source"), StepStatus.Failed, new ExecutionId("exec-source"),
                    new Dictionary<StepId, ExecutionId>(),
                    LatestFailureClassification: FailureClassification.Retryable,
                    LatestFailureReason: reason),
                new StepState(
                    new StepId("later"), StepStatus.Succeeded, new ExecutionId("exec-later"),
                    new Dictionary<StepId, ExecutionId>()),
            ],
            WorkflowStatus.Terminal);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot));

        var lines = stringWriter.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        Assert.Contains($"  source: {StepStatus.Failed} — {reason}", lines);
        Assert.Contains($"  later: {StepStatus.Succeeded}", lines);
    }

    /// <summary>
    /// #628: a resumed run reports the prior run's status and writes no new events, so without this
    /// line an already-terminal room directory is indistinguishable from a fresh failure. Naming the
    /// template is the point — under <c>--room-dir</c> it need not be the file on the command line.
    /// </summary>
    /// <remarks>
    /// A fresh run is the polarity control on the same output: a reporter that printed the line
    /// unconditionally would claim every first run resumed something.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_resumed_run_says_which_template_it_resumed_and_a_fresh_one_says_nothing(bool resumed)
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("the-bound-template"),
            1,
            [new WorkflowStepDefinition(new StepId("only"), "only", [], ["out"], [], new RetryPolicy(1))]);
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [new StepState(new StepId("only"), StepStatus.Succeeded, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>())],
            WorkflowStatus.Terminal);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot, ResumedFromSnapshot: resumed));

        var output = stringWriter.ToString();

        Assert.Equal(resumed, output.Contains("Resumed the snapshot", StringComparison.Ordinal));
        Assert.Equal(resumed, output.Contains("the-bound-template", StringComparison.Ordinal));
    }

    [Fact]
    public void A_paused_step_reports_its_execution_id_paused_outcome_and_supersede_targets()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [
                new WorkflowStepDefinition(new StepId("source"), "source", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("reviewer"), "reviewer", ["plan"], ["verdict"], [new StepId("source")],
                    new RetryPolicy(1), new PausePoint([new StepId("source")])),
            ]);

        var reviewerExecutionId = new ExecutionId("exec-reviewer");
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(new StepId("source"), StepStatus.Succeeded, new ExecutionId("exec-source"), new Dictionary<StepId, ExecutionId>()),
                new StepState(
                    new StepId("reviewer"), StepStatus.Paused, reviewerExecutionId, new Dictionary<StepId, ExecutionId>(),
                    PausedOutcome: StepStatus.Succeeded),
            ],
            WorkflowStatus.Paused);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot));

        var output = stringWriter.ToString();
        Assert.Contains("Workflow status: Paused", output);
        Assert.Contains($"execution={reviewerExecutionId}", output);
        Assert.Contains("outcome=Succeeded", output);
        Assert.Contains("supersede-targets: source", output);
    }

    [Fact]
    public void A_paused_step_with_no_declared_supersede_targets_reports_none()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1), new PausePoint([]))]);

        var executionId = new ExecutionId("exec-a");
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(
                    new StepId("a"), StepStatus.Paused, executionId, new Dictionary<StepId, ExecutionId>(),
                    PausedOutcome: StepStatus.Succeeded),
            ],
            WorkflowStatus.Paused);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot));

        Assert.Contains("supersede-targets: none", stringWriter.ToString());
    }

    [Fact]
    public void Report_names_the_pause_kind_so_a_needs_input_turn_is_not_reported_as_a_review()
    {
        // #334: a NeedsInput pause reads "awaiting input"; a ReadyForReview pause (the default) reads
        // "awaiting review" — a terminal user triaging pauses needs the same distinction the clients show.
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [
                new WorkflowStepDefinition(new StepId("chat"), "chat", [], ["out"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("anchor"), "anchor", ["out"], ["marker"], [new StepId("chat")],
                    new RetryPolicy(1), new PausePoint([new StepId("chat")], PausePointKind.NeedsInput)),
            ]);

        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(new StepId("chat"), StepStatus.Succeeded, new ExecutionId("exec-chat"), new Dictionary<StepId, ExecutionId>()),
                new StepState(
                    new StepId("anchor"), StepStatus.Paused, new ExecutionId("exec-anchor"), new Dictionary<StepId, ExecutionId>(),
                    PausedOutcome: StepStatus.Succeeded),
            ],
            WorkflowStatus.Paused);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot));

        var output = stringWriter.ToString();
        Assert.Contains("Paused — awaiting input", output);
        Assert.DoesNotContain("Paused — awaiting review", output);
    }

    [Fact]
    public void Report_prints_produced_output_paths_for_succeeded_steps_and_none_for_failed_steps()
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"flow-reporter-test-{Guid.NewGuid():N}");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [
                new WorkflowStepDefinition(new StepId("succeeded_step"), "worker", [], ["plan", "spec"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("failed_step"), "worker", [], ["unused_out"], [], new RetryPolicy(1)),
            ]);

        var execSucceeded = new ExecutionId("exec-succ-123");
        var execFailed = new ExecutionId("exec-fail-456");

        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(new StepId("succeeded_step"), StepStatus.Succeeded, execSucceeded, new Dictionary<StepId, ExecutionId>()),
                new StepState(
                    new StepId("failed_step"), StepStatus.Failed, execFailed, new Dictionary<StepId, ExecutionId>(),
                    LatestFailureReason: "Contract not satisfied"),
            ],
            WorkflowStatus.Terminal);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot, RoomDirectoryPath: roomDir));

        var output = stringWriter.ToString();

        var expectedPlanPath = Path.GetFullPath(Path.Combine(roomDir, "artifacts", $"execution_{execSucceeded}", "plan"));
        var expectedSpecPath = Path.GetFullPath(Path.Combine(roomDir, "artifacts", $"execution_{execSucceeded}", "spec"));
        var unexpectedFailPath = Path.GetFullPath(Path.Combine(roomDir, "artifacts", $"execution_{execFailed}", "unused_out"));

        Assert.Contains($"plan -> {expectedPlanPath}", output);
        Assert.Contains($"spec -> {expectedSpecPath}", output);
        Assert.DoesNotContain($"unused_out -> {unexpectedFailPath}", output);
        Assert.DoesNotContain("unused_out ->", output);
    }

    /// <summary>
    /// The second reader's finding on #740: the first cut keyed the path-printing gate on
    /// <see cref="StepStatus.Succeeded"/> alone, and a pause masks the status to Paused — so the
    /// approval-gate state got no paths despite holding a finished artifact (the why lives on the
    /// gate in <see cref="FlowStateReporter"/> itself).
    /// </summary>
    /// <remarks>
    /// The Failed-outcome pause is the polarity control one condition apart: a reporter keying on
    /// <see cref="StepStatus.Paused"/> alone, rather than on <c>PausedOutcome</c>, fails it.
    /// </remarks>
    [Theory]
    [InlineData(StepStatus.Succeeded, true)]
    [InlineData(StepStatus.Failed, false)]
    public void A_paused_step_prints_output_paths_exactly_when_its_masked_outcome_succeeded(
        StepStatus pausedOutcome, bool expectPaths)
    {
        var roomDir = Path.Combine(Path.GetTempPath(), $"flow-reporter-test-{Guid.NewGuid():N}");
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("wf"),
            1,
            [
                new WorkflowStepDefinition(
                    new StepId("gated"), "worker", [], ["verdict"], [],
                    new RetryPolicy(1), new PausePoint([])),
            ]);

        var executionId = new ExecutionId("exec-gated");
        var state = new FlowState(
            snapshot.WorkflowDefinitionSnapshotId,
            [
                new StepState(
                    new StepId("gated"), StepStatus.Paused, executionId, new Dictionary<StepId, ExecutionId>(),
                    PausedOutcome: pausedOutcome),
            ],
            WorkflowStatus.Paused);

        using var stringWriter = new StringWriter();
        FlowStateReporter.Report(stringWriter, new CommandResult(state, snapshot, RoomDirectoryPath: roomDir));

        var output = stringWriter.ToString();
        var expectedPath = Path.Combine(roomDir, "artifacts", $"execution_{executionId}", "verdict");

        Assert.Contains("Paused — awaiting review", output);
        Assert.Equal(expectPaths, output.Contains($"verdict -> {expectedPath}", StringComparison.Ordinal));
    }
}

