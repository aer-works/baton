using Aer.Adapters.Tests.TestSupport;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Adapters.Tests;

public class BuiltInWorkflowTemplatesTests
{
    [Fact]
    public void Catalog_ContainsSoloAndReviewRunTemplates()
    {
        var catalog = BuiltInWorkflowTemplates.Catalog;
        Assert.Equal(4, catalog.Count);
        Assert.Contains(catalog, t => t.Id == "chat-session");
        Assert.Contains(catalog, t => t.Id == "codebase-session");
        Assert.Contains(catalog, t => t.Id == "solo-run");
        Assert.Contains(catalog, t => t.Id == "review-run");
        // The dispatch roles deliberately stay OUT of Catalog (they'd land in the start
        // pickers) — GetRoleTemplates() is their export surface, asserted below.
        Assert.DoesNotContain(catalog, t => t.Id == "implement");
    }

    [Fact]
    public void GetRoleTemplates_ContainsEveryCatalogRole_WithValidFields()
    {
        var roles = BuiltInWorkflowTemplates.GetRoleTemplates();
        Assert.Equal(7, roles.Count);
        Assert.True(roles.ContainsKey("advise"));
        Assert.True(roles.ContainsKey("implement"));
        Assert.True(roles.ContainsKey("review"));
        Assert.True(roles.ContainsKey("fact-check"));
        Assert.True(roles.ContainsKey("janitor"));
        Assert.True(roles.ContainsKey("orchestrate"));
        Assert.True(roles.ContainsKey("patch"));

        foreach (var (id, role) in roles)
        {
            Assert.False(string.IsNullOrWhiteSpace(role.Adapter));
            Assert.False(string.IsNullOrWhiteSpace(role.Use));
            Assert.InRange(role.TimeoutMinutes, 1, 120);
            Assert.NotEmpty(role.Outputs);
        }
    }

    [Fact]
    public void Materialize_SoloRun_ProducesValidDefinitionAndBindings()
    {
        var (definition, bindings) = BuiltInWorkflowTemplates.Materialize("solo-run", "claude", null, "Custom solo prompt");

        Assert.Equal("solo-run-template", definition.WorkflowTemplateId.Value);
        Assert.Single(definition.Steps);
        Assert.Equal("solo-step", definition.Steps[0].StepId.Value);

        Assert.Single(bindings);
        var entry = bindings["solo-worker"];
        Assert.Equal("claude", entry.Adapter);
        Assert.Equal("Custom solo prompt", entry.PromptTemplate);
    }

    [Fact]
    public void Materialize_ReviewRun_ProducesValidTwoStepDefinitionAndBindings()
    {
        var (definition, bindings) = BuiltInWorkflowTemplates.Materialize("review-run", "claude", "agy");

        Assert.Equal("review-run-template", definition.WorkflowTemplateId.Value);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal("draft", definition.Steps[0].StepId.Value);
        Assert.Equal("review", definition.Steps[1].StepId.Value);
        Assert.NotNull(definition.Steps[1].PausePoint);
        Assert.Contains(definition.Steps[1].PausePoint!.SupersedeTargets, target => target.Value == "draft");

        Assert.Equal(2, bindings.Count);
        Assert.Equal("claude", bindings["draft-worker"].Adapter);
        Assert.Equal("agy", bindings["review-worker"].Adapter);
    }

    [Fact]
    public void Materialize_ReviewRun_DefaultsReviewerPromptWhenNoSecondaryCustomPromptGiven()
    {
        var role = WorkerRoleCatalog.For("review");
        var (_, bindings) = BuiltInWorkflowTemplates.Materialize("review-run", "claude", "agy", "Write a roast");

        Assert.StartsWith(
            "Review draft.md carefully, provide feedback and recommendations.",
            bindings["review-worker"].PromptTemplate);
        foreach (var output in role.Outputs)
        {
            Assert.Contains(output.Instruction, bindings["review-worker"].PromptTemplate);
        }
    }

    [Fact]
    public void Materialize_ReviewRun_UsesSecondaryCustomPromptForReviewerWhenGiven()
    {
        // Review follow-up (issue #255): the reviewer's prompt used to be hardcoded no matter what
        // the drafter was asked to do -- e.g. asking the drafter for a roast still got the reviewer
        // told to "review draft.md carefully" as a document, not respond to it.
        var role = WorkerRoleCatalog.For("review");
        var (_, bindings) = BuiltInWorkflowTemplates.Materialize(
            "review-run", "claude", "agy", "Write a roast", "Write your own roast back");

        Assert.Equal("Write a roast", bindings["draft-worker"].PromptTemplate);
        Assert.StartsWith("Write your own roast back", bindings["review-worker"].PromptTemplate);
        foreach (var output in role.Outputs)
        {
            Assert.Contains(output.Instruction, bindings["review-worker"].PromptTemplate);
        }
    }

    [Fact]
    public void Materialize_ReviewRun_AdoptsCatalogReviewRole_ClaudeSecondary()
    {
        var role = WorkerRoleCatalog.For("review");
        var (definition, bindings) = BuiltInWorkflowTemplates.Materialize("review-run", "claude", "claude");

        var reviewBinding = bindings["review-worker"]!;
        Assert.NotNull(reviewBinding.PermissionGrant);
        Assert.False(reviewBinding.PermissionGrant!.WriteFiles);
        Assert.Equal(GrantAuditMode.Enforced, reviewBinding.GrantAuditMode);

        var expectedProducedOutputs = role.Outputs
            .Select(o => new ProducedOutput(o.Name, Schema: o.Schema))
            .ToList();
        Assert.Equal(expectedProducedOutputs, reviewBinding.Contract.ProducedOutputs);

        foreach (var output in role.Outputs)
        {
            Assert.Contains(output.Instruction, reviewBinding.PromptTemplate);
        }

        Assert.Equal(reviewBinding.Contract.ProducedOutputs[0].Name, definition.Steps[1].Outputs.Single());

        // #1147: the contract's RequiredInputs must mirror the step's Inputs — it is what the
        // adapters' prompt builders disclose the AER_INPUT_<n> path from, and the adoption
        // originally dropped it, leaving the reviewer undisclosed where draft.md landed.
        Assert.Equal(definition.Steps[1].Inputs, reviewBinding.Contract.RequiredInputs);
        Assert.Equal(new[] { "draft.md" }, reviewBinding.Contract.RequiredInputs);

        // #1146 review: timeout/model/effort now come from the role's tier — pinned from the
        // catalog itself, so a tier edit reddens this instead of silently retuning review-run.
        // Same-vendor secondary keeps the tier's model and effort (#1082's rule, non-swap arm).
        Assert.Equal(role.Timeout, reviewBinding.Timeout);
        Assert.Equal(role.Model, reviewBinding.Model);
        Assert.Equal(role.Effort, reviewBinding.Effort);
    }

    [Fact]
    public void Materialize_ReviewRun_AdoptsCatalogReviewRole_AgySecondary()
    {
        var role = WorkerRoleCatalog.For("review");
        var (definition, bindings) = BuiltInWorkflowTemplates.Materialize("review-run", "claude", "agy");

        var reviewBinding = bindings["review-worker"]!;
        Assert.NotNull(reviewBinding.PermissionGrant);
        // WriteFiles: true alone holds under the old hand-rolled binding too (its shared
        // defaultGrant granted writes on every adapter) — the audit mode is the discriminator.
        Assert.True(reviewBinding.PermissionGrant!.WriteFiles);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, reviewBinding.GrantAuditMode);

        Assert.Equal(reviewBinding.Contract.ProducedOutputs[0].Name, definition.Steps[1].Outputs.Single());

        // Vendor swap (frontier tier is claude): the tier's model/effort drop to the new vendor's
        // own defaults (#1082); the timeout is the role's regardless of vendor.
        Assert.Equal(role.Timeout, reviewBinding.Timeout);
        Assert.Null(reviewBinding.Model);
        Assert.Null(reviewBinding.Effort);
    }

    [Fact]
    public async Task MaterializeToDirectoryAsync_PersistsValidFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_template_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync("review-run", "claude", "agy", tempDir, cancellationToken: TestContext.Current.CancellationToken);

            var workflowPath = Path.Combine(tempDir, "workflow.json");
            var bindingsPath = Path.Combine(tempDir, "bindings.json");
            var metaWorkflow = Path.Combine(tempDir, ".aer", "workflow-path");
            var metaBindings = Path.Combine(tempDir, ".aer", "bindings-path");

            Assert.True(File.Exists(workflowPath));
            Assert.True(File.Exists(bindingsPath));
            Assert.True(File.Exists(metaWorkflow));
            Assert.True(File.Exists(metaBindings));

            var loadedDef = await WorkflowDefinitionParser.LoadFromFileAsync(workflowPath, TestContext.Current.CancellationToken);
            var loadedBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken);

            Assert.Equal("review-run-template", loadedDef.WorkflowTemplateId.Value);
            Assert.Equal(2, loadedBindings.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                DirectoryCleanup.DeleteRecursively(tempDir);
            }
        }
    }

    [Fact]
    public async Task MaterializeToDirectoryAsync_RejectsASecondTaskAtTheSameDirectoryInsteadOfOverwriting()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aer_template_collision_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync("solo-run", "claude", null, tempDir, "First prompt", cancellationToken: TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<RoomDirectoryAlreadyExistsException>(() =>
                BuiltInWorkflowTemplates.MaterializeToDirectoryAsync("review-run", "claude", "agy", tempDir, "Second prompt", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(tempDir, ex.Message);

            // The rejected second attempt must not have clobbered the first task's definition.
            var loadedDef = await WorkflowDefinitionParser.LoadFromFileAsync(Path.Combine(tempDir, "workflow.json"), TestContext.Current.CancellationToken);
            Assert.Equal("solo-run-template", loadedDef.WorkflowTemplateId.Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                DirectoryCleanup.DeleteRecursively(tempDir);
            }
        }
    }
}
