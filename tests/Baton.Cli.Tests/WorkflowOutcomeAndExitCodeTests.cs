using Baton.Flow.Domain;
using Baton.Flow.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// Pure unit coverage for <see cref="WorkflowOutcome"/> and <see cref="RunExitCodeResolver"/> (#1356)
/// against hand-built <see cref="FlowState"/>s — every terminal class the exit-code table promises,
/// without spinning up a real pump for each one. The wiring itself (that <c>Program</c> actually
/// returns these codes) is covered separately by the real-process tests in
/// <see cref="TerminalSentinelEndToEndTests"/>.
/// </summary>
public class WorkflowOutcomeAndExitCodeTests
{
    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new(Guid.NewGuid().ToString("N"));

    [Fact]
    public void All_steps_succeeded_resolves_to_Succeeded_and_exit_0()
    {
        var state = TerminalState([Step("a", StepStatus.Succeeded), Step("b", StepStatus.Succeeded)]);

        Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_zero_step_terminal_workflow_resolves_to_Succeeded_vacuously_matching_pre_1356_behaviour()
    {
        var state = TerminalState([]);

        Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_step_that_ran_and_failed_for_an_ordinary_reason_resolves_to_Failed_and_exit_1()
    {
        var state = TerminalState([
            Step("a", StepStatus.Succeeded),
            Step("b", StepStatus.Failed, reason: "Worker exited with non-zero code 1."),
        ]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_rejected_step_resolves_to_Failed_and_exit_1()
    {
        var state = TerminalState([Step("a", StepStatus.Rejected)]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_step_whose_only_failure_was_a_dispatch_timeout_resolves_to_exit_3_not_the_generic_Failed_bucket()
    {
        // The exact sentence OutcomeClassifier.Classify writes for CoreExitReason.TimedOut -- the
        // only signal this distinction has (there is no structural Timeout classification).
        var state = TerminalState([Step("a", StepStatus.Failed, reason: "Execution timed out. stderr: …")]);

        // The JSON/human-facing outcome word stays the coarse "Failed" -- #1356 point 1's shape
        // doesn't ask for a sixth top-level state, only the exit-code table (point 2) asks for a
        // distinct class.
        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Timeout, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_timeout_alongside_a_genuine_hard_failure_stays_in_the_Failed_bucket_not_Timeout()
    {
        // Mixed outcome: one step timed out, another failed outright. The hard failure is the more
        // actionable signal, so it wins rather than the two averaging out to a misleadingly narrow
        // "just a timeout" code.
        var state = TerminalState([
            Step("a", StepStatus.Failed, reason: "Execution timed out."),
            Step("b", StepStatus.Failed, reason: "Worker exited with non-zero code 1."),
        ]);

        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_cancelled_step_with_nothing_else_failed_resolves_to_Cancelled_and_exit_4()
    {
        var state = TerminalState([Step("a", StepStatus.Succeeded), Step("b", StepStatus.Cancelled)]);

        Assert.Equal(WorkflowOutcome.Cancelled, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Cancelled, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_paused_workflow_without_wait_is_not_terminal_and_resolves_to_the_general_Failed_bucket()
    {
        var state = new FlowState(SnapshotId, [Step("a", StepStatus.Paused)], WorkflowStatus.Paused);

        Assert.Equal(WorkflowOutcome.Paused, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    [Fact]
    public void A_still_running_workflow_resolves_to_the_general_Failed_bucket()
    {
        var state = new FlowState(SnapshotId, [Step("a", StepStatus.Running)], WorkflowStatus.Running);

        Assert.Equal(WorkflowOutcome.Running, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    private static FlowState TerminalState(IReadOnlyList<StepState> steps) =>
        new(SnapshotId, steps, WorkflowStatus.Terminal);

    private static StepState Step(string stepId, StepStatus status, string? reason = null) =>
        new(new StepId(stepId), status, new ExecutionId(Guid.NewGuid().ToString("N")),
            new Dictionary<StepId, ExecutionId>(), LatestFailureReason: reason);

    private static CommandResult Result(FlowState state) => new(
        state,
        new WorkflowDefinitionSnapshot(SnapshotId, new WorkflowTemplateId("t"), 1, []));
}
