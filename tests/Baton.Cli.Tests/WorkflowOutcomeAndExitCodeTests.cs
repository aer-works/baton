using System.Reflection;
using Baton.Domain;
using Baton.Status;

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

    [Fact]
    public void A_wait_timeout_expiry_on_a_still_paused_workflow_resolves_to_exit_3_not_the_generic_Failed_bucket()
    {
        // #1378: WaitTimedOut is set by RunCommand's --wait poll loop, never by anything the ledger
        // itself records -- the room is genuinely still Paused, distinct from the dispatch-timeout
        // arm above (which IS a Terminal, Failed room). Checked ahead of WorkflowOutcome entirely.
        var state = new FlowState(SnapshotId, [Step("a", StepStatus.Paused)], WorkflowStatus.Paused);

        Assert.Equal(WorkflowOutcome.Paused, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Timeout, RunExitCodeResolver.Resolve(Result(state, waitTimedOut: true)));
    }

    [Fact]
    public void A_wait_timeout_flag_on_a_room_that_actually_reached_Terminal_defers_to_the_real_outcome()
    {
        // #1478 review, F1 (the race itself is explained at RunCommand.WaitForTerminalAsync's
        // timedOut computation): RunCommand refuses to pair WaitTimedOut with a Terminal state;
        // this arm pins the resolver's own guard so that even a future producer of the pairing
        // cannot make exit 3 contradict a written terminal sentinel.
        var state = TerminalState([Step("a", StepStatus.Succeeded)]);

        Assert.Equal(RunExitCode.Succeeded, RunExitCodeResolver.Resolve(Result(state, waitTimedOut: true)));
    }

    // #1608 review: was "S1 did NOT wire this swap" -- now inverted, since this PR IS that swap. What
    // still matters about this exact fixture: a journal line written before #1608 shipped recorded
    // FlowEvent.ExecutionFailed (Permanent) with the capture fields attached, never
    // FlowEvent.ExecutionIndeterminate, so replaying it never sets IndeterminateAwaitingResolution.
    // That backward-compat reading is what this pins now -- the capture *fields* being present is not
    // by itself what makes a room read Indeterminate; the flag is.
    [Fact]
    public void A_pre_1608_captured_response_Failed_step_without_the_new_flag_still_describes_as_Failed()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.Permanent,
            LatestFailureReason: "Contract not satisfied: 'advice.md' is missing. Response captured to '.captured-response.md'; awaiting conductor resolution.",
            LatestCapturedResponseFile: ".captured-response.md",
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: false);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
        Assert.NotEqual(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
    }

    // #1608: the actual producer this issue adds -- an unresolved ExecutionIndeterminate projects
    // IndeterminateAwaitingResolution true, and DescribeTerminal must read the room Indeterminate for
    // it even though the step's own Status stays Failed (the "single added enum value" ruling).
    [Fact]
    public void An_unresolved_indeterminate_capture_describes_the_room_as_Indeterminate_not_Failed()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: null,
            LatestFailureReason: "Contract not satisfied: 'advice.md' is missing. Response captured to '.captured-response.md'; awaiting conductor resolution.",
            LatestCapturedResponseFile: ".captured-response.md",
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: true);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(state));
        Assert.Equal(RunExitCode.Failed, RunExitCodeResolver.Resolve(Result(state)));
    }

    // Polarity partner: a resolved (rejected) capture clears the flag but leaves the step Failed --
    // this is the shape 'baton resolve --reject' produces, and it must read as an ordinary Failed room
    // again, not stay stuck reading Indeterminate forever.
    [Fact]
    public void A_resolved_rejected_capture_describes_the_room_as_Failed_again()
    {
        var step = new StepState(
            new StepId("a"), StepStatus.Failed, new ExecutionId("exec-1"), new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: null,
            LatestCapturedResponseFile: ".captured-response.md",
            LatestUnsatisfiedOutputNames: ["advice.md"],
            IndeterminateAwaitingResolution: false);
        var state = TerminalState([step]);

        Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(state));
    }

    // #1586 S1 review F1: the operator's amendment 1 called this a "tripwire pattern" that sweeps
    // every predicate that must learn a new WorkflowOutcome member -- a mechanism the repo did not
    // actually have (no reflection over the constant set anywhere, no vocabulary checker under
    // tools/). This test IS that mechanism: the failure message doubles as the sweep list, so
    // whoever adds a seventh member reads it here rather than discovering the gap via
    // RunExitCodeResolver's silent wildcard (the concrete failure this closes).
    [Fact]
    public void The_WorkflowOutcome_vocabulary_is_pinned_so_a_new_member_forces_the_consumer_sweep()
    {
        var members = typeof(WorkflowOutcome)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Cancelled", "Failed", "Indeterminate", "Paused", "Running", "Succeeded",
            ],
            members);
        // Adding a member? Sweep: RunExitCodeResolver.Resolve, RedispatchCommand's parent gate,
        // StatusCommand, FleetStatusTool, glass.html chipsHtml + render buckets, spec/baton.md §3's
        // table.
    }

    private static FlowState TerminalState(IReadOnlyList<StepState> steps) =>
        new(SnapshotId, steps, WorkflowStatus.Terminal);

    private static StepState Step(string stepId, StepStatus status, string? reason = null) =>
        new(new StepId(stepId), status, new ExecutionId(Guid.NewGuid().ToString("N")),
            new Dictionary<StepId, ExecutionId>(), LatestFailureReason: reason);

    private static CommandResult Result(FlowState state, bool waitTimedOut = false) => new(
        state,
        new WorkflowDefinitionSnapshot(SnapshotId, new WorkflowTemplateId("t"), 1, []),
        WaitTimedOut: waitTimedOut);
}
