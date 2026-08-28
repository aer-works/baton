using Aer.Ui.Tests.TestSupport;
using Aer.Adapters;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests;

/// <summary>
/// M19 Phase 4 (issue #189): the guided New Workflow flow — form-first authoring whose Save
/// writes the same durable files (workflow definition, bindings) every
/// existing loader consumes, verified by loading them back through those exact loaders. Plain
/// ViewModel tests, no window: the flow's state and file I/O live entirely in
/// <see cref="NewWorkflowViewModel"/> (Aer.Ui.Core), which is the point of the seam.
/// </summary>
public class GuidedAuthoringTests
{
    private static string NewWorkspacePath() =>
        Path.Combine(Path.GetTempPath(), $"ui-guided-{Guid.NewGuid():N}");

    private static NewWorkflowViewModel DraftAndReviewFlow(string workspacePath)
    {
        var flow = new NewWorkflowViewModel
        {
            WorkflowName = "draft-and-review",
            WorkspaceOverridePath = workspacePath,
        };

        flow.AddStepCommand.Execute(null);
        var draft = flow.Steps[0];
        draft.Name = "draft";
        draft.Prompt = "Write the draft.";
        draft.ProducesFileName = "draft.md";

        flow.AddStepCommand.Execute(null);
        var review = flow.Steps[1];
        review.Name = "review";
        review.Kind = GuidedStepKind.Claude;
        review.Prompt = "Critique the draft.";
        review.ProducesFileName = "review.md";
        review.HasReviewGate = true;
        review.DependsOnOptions.Single(option => option.StepName == "draft").IsSelected = true;

        return flow;
    }

    [Fact]
    public async Task Save_writes_a_workflow_and_bindings_the_existing_loaders_load_back()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = DraftAndReviewFlow(workspacePath);
            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(paths);
            var definition = await TemplateProjectionLoader.LoadAsync(
                paths.Value.WorkflowFilePath, TestContext.Current.CancellationToken);
            Assert.Equal(new WorkflowTemplateId("draft-and-review"), definition.WorkflowTemplateId);
            Assert.Equal(2, definition.Steps.Count);

            var review = definition.Steps.Single(step => step.StepId.Value == "review");
            Assert.Equal(["draft.md"], review.Inputs);
            Assert.Equal(["review.md"], review.Outputs);
            Assert.Equal([new StepId("draft")], review.DependsOn);
            Assert.NotNull(review.PausePoint);
            Assert.Equal([new StepId("draft")], review.PausePoint!.SupersedeTargets);

            var bindings = await BindingsProjectionLoader.LoadAsync(
                paths.Value.BindingsFilePath, TestContext.Current.CancellationToken);
            var reviewBinding = bindings["review"];
            Assert.Equal("claude", reviewBinding.Adapter);
            Assert.Equal("Critique the draft.", reviewBinding.PromptTemplate);
            Assert.Equal(GuidedStepViewModel.DefaultTimeout, reviewBinding.Timeout);
            Assert.Null(reviewBinding.Model);
            Assert.Null(reviewBinding.PermissionScope);
            Assert.Null(reviewBinding.PermissionGrant);
            Assert.Equal(["draft.md"], reviewBinding.Contract.RequiredInputs);
            Assert.Equal("review.md", Assert.Single(reviewBinding.Contract.ProducedOutputs).Name);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public async Task Guidance_blocks_save_in_plain_words_until_the_flow_is_complete()
    {
        var flow = new NewWorkflowViewModel { WorkspaceOverridePath = NewWorkspacePath() };
        flow.RefreshStructure();

        Assert.Contains("Give the workflow a name — it names the plan and its folder.", flow.GuidanceMessages);
        Assert.Contains("Add at least one step.", flow.GuidanceMessages);
        Assert.False(flow.CanSave);
        Assert.Null(await flow.SaveAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Not saved — finish the guidance items above.", flow.StatusText);

        flow.WorkflowName = "one-step";
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";
        flow.RefreshStructure();

        Assert.Empty(flow.GuidanceMessages);
        Assert.True(flow.CanSave);
    }

    [Fact]
    public async Task Save_and_run_raises_RunRequested_with_the_saved_paths()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = DraftAndReviewFlow(workspacePath);
            string? requestedWorkflowPath = null;
            string? requestedBindingsPath = null;
            flow.RunRequested += (workflowFilePath, bindingsFilePath) =>
            {
                requestedWorkflowPath = workflowFilePath;
                requestedBindingsPath = bindingsFilePath;
                return Task.CompletedTask;
            };

            await flow.SaveAndRunCommand.ExecuteAsync(null);

            Assert.Equal(Path.Combine(workspacePath, "workflow.json"), requestedWorkflowPath);
            Assert.Equal(Path.Combine(workspacePath, "bindings.json"), requestedBindingsPath);
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public void Vendor_readiness_reports_presence_in_plain_words_and_never_gates()
    {
        var flow = new NewWorkflowViewModel();
        flow.RefreshVendorReadiness(isOnPath: binary => binary == "claude");

        Assert.Equal(
            [
                "Claude: available",
                "Agy: not found — install and sign in to the agy CLI to run steps with it",
            ],
            flow.VendorReadinessLines);

        // Readiness is informational only: nothing in the guidance path reads it, so an
        // unavailable vendor never blocks authoring or saving.
        Assert.DoesNotContain(flow.GuidanceMessages, message => message.Contains("Agy"));
    }

    // M21 Phase 1 follow-up (owner feedback on the initial per-entry-only builder): permissions are
    // set once per workflow and applied to every step at Save, not configured per step.

    [Fact]
    public async Task A_shared_permission_grant_applies_to_every_step()
    {
        var workspacePath = NewWorkspacePath();
        try
        {
            var flow = DraftAndReviewFlow(workspacePath);
            flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter>
            {
                ["claude"] = new ClaudeWorkerAdapter(),
                ["agy"] = new AgyWorkerAdapter(),
            });
            flow.GrantReadFiles = true;
            flow.GrantWriteFiles = true;

            var paths = await flow.SaveAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(paths);
            var bindings = await BindingsProjectionLoader.LoadAsync(
                paths.Value.BindingsFilePath, TestContext.Current.CancellationToken);
            foreach (var stepName in new[] { "draft", "review" })
            {
                var grant = bindings[stepName].PermissionGrant;
                Assert.NotNull(grant);
                Assert.True(grant!.ReadFiles);
                Assert.True(grant.WriteFiles);
                Assert.Null(bindings[stepName].PermissionScope);
            }
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                DirectoryCleanup.DeleteRecursively(workspacePath);
            }
        }
    }

    [Fact]
    public async Task A_permission_grant_an_in_use_adapter_cant_honor_blocks_save_with_a_plain_language_message()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["agy"] = new AgyWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Agy;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;

        Assert.Contains(
            flow.GuidanceMessages,
            message => message.Contains("agy", StringComparison.Ordinal) && message.Contains("shell", StringComparison.OrdinalIgnoreCase));
        Assert.False(flow.CanSave);
        Assert.Null(await flow.SaveAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// #657's wording fix reaches this surface too. The old phrasing blamed the builder; what the
    /// operator needs to know is that the values would be ignored at dispatch — recorded once, beside
    /// the string in <c>WorkerBindingEntryViewModel</c>.
    /// </summary>
    [Fact]
    public void An_adapter_that_ignores_grants_says_so_in_guidance_rather_than_blaming_the_builder()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["claude"] = new NoTranslatorWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantReadFiles = true;

        Assert.Contains(
            flow.GuidanceMessages,
            message => message.Contains("does not enforce permission grants", StringComparison.Ordinal)
                       && message.Contains("ignored at dispatch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            flow.GuidanceMessages,
            message => message.Contains("no structured permission builder support", StringComparison.Ordinal));
        Assert.False(flow.CanSave);
    }

    /// <summary>
    /// #645 on the guided wizard, which is a fourth authoring surface and was missed when the rule
    /// was given one home: it built a grant, validated registry membership and vendor translation,
    /// and never asked whether the grant was coherent at all.
    /// <para>
    /// Claude is the adapter here deliberately: <c>ClaudeWorkerAdapter</c> never refuses a
    /// translation, so nothing else on this path would have caught it. On gemini a shell-only grant
    /// happens to be stopped by the vendor gap instead, which is coincidence rather than coverage.
    /// What this surface costs when the check is missing is recorded at the check itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_grant_the_engine_refuses_at_bind_time_blocks_save_in_the_wizard()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        // The shell reaches reads, writes and the network, and all three are unticked.
        flow.GrantRunShellCommands = true;

        Assert.Contains(
            flow.GuidanceMessages,
            message => message.Contains("shell is granted while", StringComparison.Ordinal)
                       && message.Contains("bind time", StringComparison.Ordinal));
        Assert.False(flow.CanSave);
        Assert.Null(await flow.SaveAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The control for the test above: a coherent grant on the same adapter and the same step still
    /// saves. Without this, a validator that refused everything would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_coherent_grant_still_saves_in_the_wizard()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.SetAdapterRegistry(new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() });
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;

        Assert.DoesNotContain(
            flow.GuidanceMessages,
            message => message.Contains("shell is granted while", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
        Assert.NotNull(await flow.SaveAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Leaving_permissions_unset_never_blocks_save_even_with_no_adapter_registry()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";
        flow.RefreshStructure();

        Assert.Empty(flow.GuidanceMessages);
        Assert.True(flow.CanSave);
    }

    [Fact]
    public void Depends_on_options_follow_the_other_steps_names()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf" };
        flow.AddStepCommand.Execute(null);
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[1].Name = "review";

        Assert.Equal("draft", Assert.Single(flow.Steps[1].DependsOnOptions).StepName);
        Assert.Equal("review", Assert.Single(flow.Steps[0].DependsOnOptions).StepName);

        // A selection survives an unrelated structural refresh (options are rebuilt, state kept).
        flow.Steps[1].DependsOnOptions[0].IsSelected = true;
        flow.RefreshStructure();
        Assert.True(flow.Steps[1].DependsOnOptions.Single(option => option.StepName == "draft").IsSelected);
    }

    [Fact]
    public void Validate_refuses_grant_shapes_that_agy_adapter_refuses_at_bind()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Agy;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        // Network without shell is coherent (no shell to defeat a withheld category) but refused by
        // AgyWorkerAdapter: agy's only auto-approve-network flag (--dangerously-skip-permissions)
        // also grants shell, so the narrower shape cannot be expressed. Shell + patterns used to reach
        // here too (#624) but #659 now honours it via the hook matcher -- see the acceptance test below.
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;

        var message = Assert.Single(flow.GuidanceMessages);
        Assert.Contains("'draft'", message, StringComparison.Ordinal);
        Assert.Contains("agy only supports auto-approving network access", message, StringComparison.Ordinal);
        Assert.False(flow.CanSave);
    }

    [Fact]
    public void Validate_accepts_a_pattern_scoped_shell_grant_now_that_the_hook_enforces_it()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Agy;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        // #659: a shell + network grant scoped by ShellCommandPatterns is honoured -- the
        // AgyHookCheckCommand strict matcher enforces the patterns -- so the adapter no longer refuses
        // it at bind. This is the UI-side proof that the #624 refusal is gone.
        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;
        flow.ShellCommandPatternsText = "git:*";

        Assert.DoesNotContain(flow.GuidanceMessages, m => m.Contains("scope an agy shell grant", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
    }

    [Fact]
    public void Steps_sharing_one_grant_problem_get_one_message_naming_them_all()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[1].Name = "revise";
        foreach (var step in flow.Steps)
        {
            step.Kind = GuidedStepKind.Agy;
            step.Prompt = "Write it.";
        }
        flow.Steps[0].ProducesFileName = "draft.md";
        flow.Steps[1].ProducesFileName = "revise.md";

        // Both steps share one agy-refused shape (network without shell -- see the refusal test above),
        // so the two are coalesced into a single message naming them all.
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;

        var message = Assert.Single(flow.GuidanceMessages);
        Assert.Contains("'draft', 'revise'", message, StringComparison.Ordinal);
        Assert.False(flow.CanSave);
    }

    [Fact]
    public void Agy_step_with_shell_and_network_grant_validates()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Agy;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;

        Assert.DoesNotContain(flow.GuidanceMessages, m => m.Contains("can't be granted to", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
    }

    [Fact]
    public void Claude_step_with_shell_and_patterns_grant_validates()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "wf", WorkspaceOverridePath = NewWorkspacePath() };
        flow.AddStepCommand.Execute(null);
        flow.Steps[0].Name = "draft";
        flow.Steps[0].Kind = GuidedStepKind.Claude;
        flow.Steps[0].Prompt = "Write it.";
        flow.Steps[0].ProducesFileName = "draft.md";

        flow.GrantRunShellCommands = true;
        flow.GrantReadFiles = true;
        flow.GrantWriteFiles = true;
        flow.GrantNetworkAccess = true;
        flow.ShellCommandPatternsText = "git:*";

        Assert.DoesNotContain(flow.GuidanceMessages, m => m.Contains("can't be granted to", StringComparison.Ordinal));
        Assert.True(flow.CanSave);
    }

}

/// <summary>An adapter that never implements <see cref="IPermissionGrantTranslator"/> — the "no structured permission builder support" guidance path.</summary>
internal sealed class NoTranslatorWorkerAdapter : IWorkerAdapter
{
    public Aer.Flow.Dispatch.CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) =>
        throw new NotSupportedException("This test adapter never dispatches a real invocation.");
}
