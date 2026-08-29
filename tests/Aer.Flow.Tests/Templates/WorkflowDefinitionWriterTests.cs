using Aer.Flow.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Flow.Tests.Templates;

/// <summary>
/// The template write seam's round-trip bar (M16 Phase 1, issue #150): a saved file must
/// round-trip through the exact <see cref="WorkflowDefinitionParser"/>/<see cref="WorkflowDefinitionValidator"/>
/// every other consumer uses — provable at the domain layer precisely because the writer lives
/// beside its parser (the phase's placement decision of record).
/// </summary>
public class WorkflowDefinitionWriterTests
{
    private static WorkflowDefinition ThreeStepLinearDefinition() => new(
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
            new WorkflowStepDefinition(
                new StepId("synth"),
                "synth",
                Inputs: ["review"],
                Outputs: ["result"],
                DependsOn: [new StepId("critic")],
                RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
        ]);

    [Fact]
    public async Task A_saved_template_round_trips_through_the_engines_own_parser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"writer-{Guid.NewGuid():N}.json");
        try
        {
            var definition = ThreeStepLinearDefinition();

            await WorkflowDefinitionWriter.SaveToFileAsync(definition, path, TestContext.Current.CancellationToken);
            var parsed = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(definition.WorkflowTemplateId, parsed.WorkflowTemplateId);
            Assert.Equal(definition.WorkflowTemplateVersion, parsed.WorkflowTemplateVersion);
            Assert.Equal(definition.Steps.Count, parsed.Steps.Count);
            for (var i = 0; i < definition.Steps.Count; i++)
            {
                Assert.Equal(definition.Steps[i].StepId, parsed.Steps[i].StepId);
                Assert.Equal(definition.Steps[i].Worker, parsed.Steps[i].Worker);
                Assert.Equal(definition.Steps[i].Inputs, parsed.Steps[i].Inputs);
                Assert.Equal(definition.Steps[i].Outputs, parsed.Steps[i].Outputs);
                Assert.Equal(definition.Steps[i].DependsOn, parsed.Steps[i].DependsOn);
                Assert.Equal(definition.Steps[i].RetryPolicy, parsed.Steps[i].RetryPolicy);
                Assert.Equal(definition.Steps[i].PausePoint?.SupersedeTargets, parsed.Steps[i].PausePoint?.SupersedeTargets);
            }
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_blank_template_with_no_steps_is_engine_valid_and_round_trips()
    {
        // The editor's New action mints exactly this shape (M16 Phase 1) — an empty Steps list
        // passes structural validation, so a just-created template is already a parseable file.
        var path = Path.Combine(Path.GetTempPath(), $"writer-blank-{Guid.NewGuid():N}.json");
        try
        {
            var definition = new WorkflowDefinition(new WorkflowTemplateId("brand-new"), WorkflowTemplateVersion: 1, Steps: []);

            await WorkflowDefinitionWriter.SaveToFileAsync(definition, path, TestContext.Current.CancellationToken);
            var parsed = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("brand-new", parsed.WorkflowTemplateId.Value);
            Assert.Equal(1, parsed.WorkflowTemplateVersion);
            Assert.Empty(parsed.Steps);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task An_invalid_definition_is_rejected_at_write_time_and_nothing_is_written()
    {
        var path = Path.Combine(Path.GetTempPath(), $"writer-invalid-{Guid.NewGuid():N}.json");
        var invalid = new WorkflowDefinition(
            new WorkflowTemplateId("dup-steps"),
            WorkflowTemplateVersion: 1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "w", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("a"), "w", [], [], [], new RetryPolicy(1)),
            ]);

        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionWriter.SaveToFileAsync(invalid, path, TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, error => error.Contains("Duplicate StepId"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SaveToFileAsync_creates_missing_parent_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"writer-dirs-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "template.json");
        try
        {
            await WorkflowDefinitionWriter.SaveToFileAsync(
                ThreeStepLinearDefinition(), path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(Path.GetDirectoryName(directory)!);
        }
    }

    [Fact]
    public void Serialize_emits_indented_human_editable_json()
    {
        // A template is a hand-editable file — indented like the repo's
        // hand-authored fixtures, not SnapshotBinder's compact machine-only form.
        var json = WorkflowDefinitionWriter.Serialize(ThreeStepLinearDefinition());

        Assert.Contains("\n", json);
        Assert.Equal("architect-critic-synth", WorkflowDefinitionParser.Parse(json).WorkflowTemplateId.Value);
    }
}
