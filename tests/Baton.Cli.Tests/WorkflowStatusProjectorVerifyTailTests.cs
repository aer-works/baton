using Baton.Cli.Tests.TestSupport;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// #1701: <c>baton status --json</c> must surface a verify failure's own per-member output
/// (<see cref="Baton.Domain.StepState.IndeterminateVerifyTail"/>), not only the one-line
/// member-name summary -- this is the last hop of that fix (<c>StepState</c> to
/// <see cref="WorkflowStatusStepView.VerifyTail"/>'s JSON shape), which the projection-level tests in
/// <c>StateProjectorTests</c> do not reach.
/// </summary>
public sealed class WorkflowStatusProjectorVerifyTailTests
{
    private static readonly StepId StepId = new("implement");
    private static readonly WorkflowId WorkflowId = new("wf-1701");

    private static WorkflowDefinitionSnapshot OneStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1701"),
        new WorkflowTemplateId("verify-tail"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(StepId, "implement", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId) => new(
        executionId, WorkflowId, StepId, "implement",
        Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(10), Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(), Adapter: "claude");

    [Fact]
    public void A_VerifyFailed_steps_own_output_reaches_the_verifyTail_JSON_field()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"verify-tail-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1701");
            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId)),
                new FlowEvent.VerifyFailed(
                    executionId, ["tool-refresh-selftest"],
                    "FAILED: could not write current pointer ... [WinError 5] Access is denied"),
            };

            var state = StateProjector.Project(events, OneStepSnapshot());
            var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), roomDirectory);

            var step = Assert.Single(view.Steps);
            Assert.Equal("VerifyFailed", step.IndeterminateProducerKind);
            Assert.Equal(
                "FAILED: could not write current pointer ... [WinError 5] Access is denied",
                step.VerifyTail);

            // F2 (#1711 review): the claim is about the `verifyTail` JSON key, not just the CLR
            // property -- a rename of [JsonPropertyName("verifyTail")] must fail this test.
            var json = System.Text.Json.JsonSerializer.Serialize(step);
            Assert.Contains(
                "\"verifyTail\":\"FAILED: could not write current pointer ... [WinError 5] Access is denied\"",
                json);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void An_Arrested_step_carries_no_verifyTail_nothing_truncated_to_recover()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"verify-tail-{Guid.NewGuid():N}");
        try
        {
            var executionId = new ExecutionId("exec-1701-arrest");
            var events = new FlowEvent[]
            {
                new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId)),
                new FlowEvent.ExecutionArrested(executionId, new WorkerUsage(TokensIn: 500_000, TokensOut: 120_000), ["manage_task"]),
            };

            var state = StateProjector.Project(events, OneStepSnapshot());
            var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), roomDirectory);

            var step = Assert.Single(view.Steps);
            Assert.Equal("Arrested", step.IndeterminateProducerKind);
            Assert.Null(step.VerifyTail);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
