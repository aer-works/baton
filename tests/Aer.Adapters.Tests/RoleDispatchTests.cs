using System.Linq;
using Aer.Adapters;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// The role -> binding mapping behind <c>aer dispatch</c> (#900): a role's declared outputs become the
/// contract the engine enforces, its grant/timeout/model/effort ride along, and its output
/// instructions are appended to the spec so the worker is told to produce exactly what the contract
/// asserts. Exercised against the shipped catalog, since that is what the command actually dispatches.
/// </summary>
[Collection(WorkerRoleCatalogCollection.Name)]
public class RoleDispatchTests
{
    private static WorkerRole Review => WorkerRoleCatalog.For("review");

    [Fact]
    public void A_roles_declared_outputs_become_the_contracts_produced_outputs_with_their_schema()
    {
        var binding = RoleDispatch.ToBinding(Review, "Review the change.");

        var outputs = binding.Contract.ProducedOutputs;
        Assert.Contains(outputs, o => o.Name == "report.md" && o.Schema == OutputSchema.None);
        // The schema is carried through, not dropped to None — a verdict.json that is not a
        // ReviewVerdict must fail the contract, and that only happens if the schema survives the map.
        Assert.Contains(outputs, o => o.Name == "verdict.json" && o.Schema == OutputSchema.ReviewVerdict);
        Assert.Equal(Review.Outputs.Count, outputs.Count);
    }

    /// <summary>
    /// #1089: dispatch turns on stream-json for agy (so its terminal `result` event reaches the timeout
    /// guard) and leaves claude in text mode (no teardown-hang, no detector — streaming it would change
    /// its stdout for nothing).
    /// </summary>
    [Fact]
    public void StreamJson_is_enabled_for_agy_and_left_off_for_claude()
    {
        Assert.True(RoleDispatch.ToBinding(Review, "Review the change.", adapterOverride: "agy").StreamJson);
        Assert.False(RoleDispatch.ToBinding(Review, "Review the change.", adapterOverride: "claude").StreamJson);
    }

    [Fact]
    public void The_prompt_is_the_spec_followed_by_every_output_instruction()
    {
        var binding = RoleDispatch.ToBinding(Review, "Review the change.");

        Assert.StartsWith("Review the change.", binding.PromptTemplate);
        foreach (var output in Review.Outputs)
        {
            Assert.Contains(output.Instruction, binding.PromptTemplate);
        }
    }

    /// <summary>
    /// #1095: the dispatch prompt carries the one-shot execution contract (its rationale lives on
    /// <see cref="RoleDispatch"/>'s <c>OneShotContract</c>) — a dispatched worker's turn is never
    /// resumed, unlike a chat turn.
    /// </summary>
    [Fact]
    public void The_dispatch_prompt_states_the_one_shot_contract()
    {
        var dispatch = RoleDispatch.ToBinding(Review, "Review the change.").PromptTemplate;
        Assert.Contains("non-interactive turn", dispatch);
    }

    [Fact]
    public void The_binding_carries_the_roles_grant_timeout_model_and_effort()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec");

        Assert.Equal(Review.Grant, binding.PermissionGrant);
        Assert.Equal(Review.Timeout, binding.Timeout);
        Assert.Equal(Review.Model, binding.Model);
        Assert.Equal(Review.Effort, binding.Effort);
    }

    [Fact]
    public void The_adapter_defaults_to_the_roles_tier_but_an_override_wins()
    {
        // review is a claude-tier role; overriding to agy must change it (and normalize case),
        // so the two arms differ regardless of which tier review sits on later.
        Assert.Equal(Review.Adapter, RoleDispatch.ToBinding(Review, "spec").Adapter);
        var overridden = RoleDispatch.ToBinding(Review, "spec", "agy").Adapter;
        Assert.Equal("agy", overridden);
        Assert.NotEqual(Review.Adapter, overridden);
    }

    [Fact]
    public void Materialize_produces_one_step_keyed_by_the_role_id_whose_outputs_mirror_the_contract()
    {
        var (definition, bindings) = RoleDispatch.Materialize(Review, "spec");

        var step = Assert.Single(definition.Steps);
        Assert.Equal("review", step.StepId.Value);
        Assert.Equal("review", step.Worker);
        Assert.Empty(step.DependsOn);
        // Step output names mirror the contract's; this pins that alignment (its rationale lives on RoleDispatch).
        Assert.Equal(
            Review.Outputs.Select(o => o.Name).OrderBy(n => n),
            step.Outputs.OrderBy(n => n));

        var binding = Assert.Contains("review", bindings);
        Assert.Equal(step.Outputs.OrderBy(n => n), binding.Contract.ProducedOutputs.Select(o => o.Name).OrderBy(n => n));
    }

    [Fact]
    public void ToBinding_on_agy_adapter_for_write_files_false_role_with_outputs_materializes_audited_grant()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", "agy");

        Assert.True(binding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);
    }

    [Fact]
    public void ToBinding_on_claude_adapter_for_write_files_false_role_with_outputs_keeps_enforced_grant()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", "claude");

        Assert.False(binding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.Enforced, binding.GrantAuditMode);
    }

    private static WorkerRole Advise => WorkerRoleCatalog.For("advise");

    [Fact]
    public void An_adapter_override_to_a_different_vendor_drops_the_tiers_vendor_specific_model()
    {
        // advise is an agy-tier role whose tier pins a (gemini) model; running it on claude must NOT
        // carry that vendor-specific string to claude's CLI — the measured #1082 failure. With no
        // explicit --model, the swapped vendor falls back to its own default (null model).
        Assert.False(string.IsNullOrEmpty(Advise.Model)); // the tier really does pin a model to drop

        var onClaude = RoleDispatch.ToBinding(Advise, "spec", "claude");
        Assert.Equal("claude", onClaude.Adapter);
        Assert.Null(onClaude.Model);

        // Control — same vendor keeps the tier's own model, so this is about the swap, not a blanket null.
        Assert.Equal(Advise.Model, RoleDispatch.ToBinding(Advise, "spec").Model);
    }

    [Fact]
    public void An_explicit_model_override_wins_over_both_the_tier_and_the_vendor_swap()
    {
        // The model is its own axis (0017/0033): an explicit --model is used verbatim, whether or not
        // the vendor is also swapped.
        Assert.Equal("opus", RoleDispatch.ToBinding(Advise, "spec", "claude", modelOverride: "opus").Model);
        Assert.Equal("gemini-x", RoleDispatch.ToBinding(Advise, "spec", modelOverride: "gemini-x").Model);
    }

    [Fact]
    public void Effort_is_its_own_axis_dropped_on_a_vendor_swap_but_kept_on_the_same_vendor_and_overridable()
    {
        // The catalog pins raw vendor flag values as effort ("high"/"low"), not the canonical 0023
        // vocabulary an adapter would map — so effort is vendor-specific in practice and, like the model,
        // must not ride a vendor swap (an "xhigh"/"max" tier would leak onto agy, which rejects those).
        Assert.False(string.IsNullOrEmpty(Review.Effort)); // the tier really does pin an effort to drop

        Assert.Null(RoleDispatch.ToBinding(Review, "spec", "agy").Effort);          // swapped: dropped
        Assert.Equal(Review.Effort, RoleDispatch.ToBinding(Review, "spec").Effort); // same vendor: kept
        Assert.Equal("quick", RoleDispatch.ToBinding(Review, "spec", effortOverride: "quick").Effort); // override wins
    }

    [Fact]
    public void ToBinding_pins_the_working_directory_when_given_so_the_worker_can_read_the_project()
    {
        // #1083 polarity: a null binding pins no directory, a given one pins it. The rationale — why an
        // unpinned binding stranded repo reads — lives on RoleDispatch.workingDirectory.
        Assert.Null(RoleDispatch.ToBinding(Review, "spec").WorkingDirectory);
        Assert.Equal("/repo/root", RoleDispatch.ToBinding(Review, "spec", workingDirectory: "/repo/root").WorkingDirectory);
    }

    [Fact]
    public void Materialize_threads_the_working_directory_and_axis_overrides_onto_the_binding()
    {
        var (_, bindings) = RoleDispatch.Materialize(
            Advise, "spec", "claude", workingDirectory: "/w", effortOverride: "careful");
        var binding = Assert.Contains("advise", bindings);

        Assert.Equal("claude", binding.Adapter);
        Assert.Null(binding.Model);              // vendor swapped, no explicit --model
        Assert.Equal("careful", binding.Effort);
        Assert.Equal("/w", binding.WorkingDirectory);
    }

    [Fact]
    public void Patch_role_resolves_with_expected_contract_and_grant_polarity_per_adapter()
    {
        var patchRole = WorkerRoleCatalog.For("patch");
        Assert.Equal("patch", patchRole.Id);

        var claudeBinding = RoleDispatch.ToBinding(patchRole, "Propose a patch.", "claude");
        Assert.Single(claudeBinding.Contract.ProducedOutputs);
        Assert.Equal("patch.diff", claudeBinding.Contract.ProducedOutputs[0].Name);
        Assert.Equal(OutputSchema.Diff, claudeBinding.Contract.ProducedOutputs[0].Schema);
        Assert.False(claudeBinding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.Enforced, claudeBinding.GrantAuditMode);

        var agyBinding = RoleDispatch.ToBinding(patchRole, "Propose a patch.", "agy");
        Assert.True(agyBinding.PermissionGrant?.WriteFiles);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, agyBinding.GrantAuditMode);
    }

    [Fact]
    public void OutputOverride_replaces_primary_output_name_and_updates_prompt_instructions()
    {
        var binding = RoleDispatch.ToBinding(Advise, "spec", outputOverride: "custom-advice.md");
        Assert.Equal("custom-advice.md", binding.Contract.ProducedOutputs[0].Name);
        Assert.Contains("custom-advice.md", binding.PromptTemplate);
    }

    /// <summary>
    /// R1's polarity, per <see cref="RoleDispatch.ToBinding"/>'s <c>autoProvisionWorktree</c> doc — this
    /// mapping step declares the worktree spec but never stamps <see cref="WorkerBindingConfigEntry.IsWorktree"/>
    /// itself, so a hand-authored or prematurely-set <c>true</c> can never claim an isolation this step
    /// did not provide.
    /// </summary>
    [Fact]
    public void Worktree_is_always_declared_fresh_for_an_audited_grant_regardless_of_the_callers_directory_shape()
    {
        var binding = RoleDispatch.ToBinding(Review, "spec", adapterOverride: "agy", workingDirectory: "/any/caller/directory");

        Assert.Equal(GrantAuditMode.AuditedNotEnforced, binding.GrantAuditMode);
        Assert.NotNull(binding.Worktree);
        Assert.Equal("/any/caller/directory", binding.Worktree!.Repository);
        Assert.Equal("HEAD", binding.Worktree!.Ref);
        Assert.Null(binding.WorkingDirectory);
        Assert.False(binding.IsWorktree);
    }
}

