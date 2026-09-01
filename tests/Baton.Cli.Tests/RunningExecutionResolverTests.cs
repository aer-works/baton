using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495's room-level targeting rule: exactly one <see cref="StepStatus.Running"/> step resolves;
/// zero or more than one refuses (naming every candidate) rather than guessing. Shared by
/// <see cref="CancelCommand"/>'s <c>--execution</c>-omitted path and <see cref="CancelRequestPoller"/>'s
/// <c>latest</c> resolution.
/// </summary>
public class RunningExecutionResolverTests
{
    private static readonly WorkflowDefinitionSnapshotId SnapshotId = new("snapshot-1");

    [Fact]
    public void Exactly_one_Running_step_resolves_to_its_execution_id()
    {
        var runningExecutionId = new ExecutionId("exec-running");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Running, runningExecutionId),
            Step("b", StepStatus.Succeeded, new ExecutionId("exec-b")),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Equal(runningExecutionId, result.Single);
        Assert.Equal([runningExecutionId], result.RunningExecutionIds);
    }

    [Fact]
    public void Zero_Running_steps_resolve_to_null_with_an_empty_candidate_list()
    {
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Succeeded, new ExecutionId("exec-a")),
            Step("b", StepStatus.Pending, null),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Empty(result.RunningExecutionIds);
    }

    [Fact]
    public void More_than_one_Running_step_resolves_to_null_naming_every_candidate()
    {
        var first = new ExecutionId("exec-a");
        var second = new ExecutionId("exec-b");
        var state = new FlowState(SnapshotId, [
            Step("a", StepStatus.Running, first),
            Step("b", StepStatus.Running, second),
        ]);

        var result = RunningExecutionResolver.Resolve(state);

        Assert.Null(result.Single);
        Assert.Equal([first, second], result.RunningExecutionIds);
    }

    private static StepState Step(string stepId, StepStatus status, ExecutionId? latestExecutionId) =>
        new(new StepId(stepId), status, latestExecutionId, new Dictionary<StepId, ExecutionId>());
}
