using System.Text.Json;
using Baton.Flow.Domain;

namespace Baton.Flow.Tests.Domain;

public class WorkflowDefinitionTests
{
    [Fact]
    public void A_three_step_linear_workflow_definition_round_trips()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("architect-critic-synth"),
            WorkflowTemplateVersion: 3,
            Steps:
            [
                new WorkflowStepDefinition(
                    new StepId("architect"),
                    "architect",
                    Inputs: ["goal"],
                    Outputs: ["plan"],
                    DependsOn: [],
                    RetryPolicy: new RetryPolicy(MaxAttempts: 3)),
                new WorkflowStepDefinition(
                    new StepId("critic"),
                    "critic",
                    Inputs: ["plan"],
                    Outputs: ["review"],
                    DependsOn: [new StepId("architect")],
                    RetryPolicy: new RetryPolicy(MaxAttempts: 1),
                    PausePoint: new PausePoint(SupersedeTargets: [new StepId("architect")])),
            ]);

        var json = JsonSerializer.Serialize(definition);
        var deserialized = JsonSerializer.Deserialize<WorkflowDefinition>(json);
        Assert.NotNull(deserialized);

        Assert.Equal(json, JsonSerializer.Serialize(deserialized));
    }

    [Fact]
    public void Snapshot_freezes_the_template_id_and_version_alongside_its_own_id()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-1"),
            new WorkflowTemplateId("architect-critic-synth"),
            WorkflowTemplateVersion: 3,
            Steps: []);

        Assert.Equal("snap-1", snapshot.WorkflowDefinitionSnapshotId.Value);
        Assert.Equal(3, snapshot.WorkflowTemplateVersion);
    }

    [Fact]
    public void WorkerContract_with_a_conditional_output_round_trips()
    {
        var contract = new WorkerContract(
            "critic",
            RequiredInputs: ["plan"],
            ProducedOutputs:
            [
                new ProducedOutput("verdict", new OutputCondition("/status", new JsonScalar.String("approved"))),
            ],
            OptionalMetadata: ["summary"]);

        var json = JsonSerializer.Serialize(contract);
        var deserialized = JsonSerializer.Deserialize<WorkerContract>(json);
        Assert.NotNull(deserialized);

        Assert.Equal(json, JsonSerializer.Serialize(deserialized));
    }

    [Fact]
    public void FlowState_projects_a_skeleton_per_step_status()
    {
        var state = new FlowState(
            new WorkflowDefinitionSnapshotId("snap-1"),
            Steps:
            [
                new StepState(
                    new StepId("architect"),
                    StepStatus.Succeeded,
                    new ExecutionId("exec-1"),
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()),
                new StepState(
                    new StepId("critic"),
                    StepStatus.Pending,
                    LatestExecutionId: null,
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()),
            ]);

        var json = JsonSerializer.Serialize(state);
        var deserialized = JsonSerializer.Deserialize<FlowState>(json);
        Assert.NotNull(deserialized);

        Assert.Equal(json, JsonSerializer.Serialize(deserialized));
    }
}
