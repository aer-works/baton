using Baton.Vendors;
using Baton.Flow.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// The binding-inheritance rule behind <c>baton redispatch</c> with no <c>--spec</c> (#1441):
/// <see cref="RedispatchCommand.InheritBinding"/> starts from the parent's exact recorded entry and
/// applies only the axes the operator actually passed. Exercised as a pure unit against a hand-built
/// <see cref="WorkerBindingConfigEntry"/>, the same reusable-primitive testing
/// <see cref="Baton.Vendors.Tests.RoleDispatchTests"/> already does for <see cref="RoleDispatch.ToBinding"/>.
/// </summary>
public class RedispatchBindingTests
{
    private static WorkerBindingConfigEntry ParentEntry(
        string adapter = "claude", string? model = "opus", string? effort = "careful",
        string? workingDirectory = "/repo", WorktreeWorkspace? worktree = null,
        TimeSpan? timeout = null) =>
        new(
            Adapter: adapter,
            Contract: new WorkerContract("advise", [], [new ProducedOutput("advice.md")], []),
            PromptTemplate: "Weigh the options.\n\nRequired outputs:\n- Write advice.md",
            Timeout: timeout ?? TimeSpan.FromMinutes(30),
            Model: model,
            Effort: effort,
            WorkingDirectory: worktree is null ? workingDirectory : null,
            Worktree: worktree,
            SessionId: "prior-session-id",
            ResumeSession: false);

    [Fact]
    public void With_no_overrides_every_axis_is_inherited_verbatim()
    {
        var parent = ParentEntry();
        var options = new RedispatchOptions("parent-room", "new-room");

        var entry = RedispatchCommand.InheritBinding(parent, options);

        Assert.Equal(parent.Adapter, entry.Adapter);
        Assert.Equal(parent.Model, entry.Model);
        Assert.Equal(parent.Effort, entry.Effort);
        Assert.Equal(parent.WorkingDirectory, entry.WorkingDirectory);
        Assert.Equal(parent.Timeout, entry.Timeout);
        Assert.Equal(parent.PromptTemplate, entry.PromptTemplate);
        Assert.Equal(parent.Contract, entry.Contract);
    }

    [Fact]
    public void A_fresh_binding_never_inherits_the_parents_resumed_session_state()
    {
        var parent = ParentEntry();
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Null(entry.SessionId);
        Assert.False(entry.ResumeSession);
    }

    [Fact]
    public void An_explicit_adapter_override_wins_over_the_inherited_one()
    {
        var parent = ParentEntry(adapter: "claude");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room", Adapter: "agy"));

        Assert.Equal("agy", entry.Adapter);
    }

    [Fact]
    public void A_differently_cased_adapter_is_normalized_and_is_not_a_vendor_swap()
    {
        // The registry lookup is case-sensitive; ToBinding normalizes its winner, so this path must
        // too — and "Claude" over a "claude" parent is the SAME vendor, so model/effort survive.
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room", Adapter: " Claude "));

        Assert.Equal("claude", entry.Adapter);
        Assert.Equal("opus", entry.Model);
        Assert.Equal("careful", entry.Effort);
    }

    [Fact]
    public void Stream_json_is_recomputed_for_the_new_adapter_not_inherited()
    {
        // Adapter-derived (#1089): agy streams, claude doesn't. Both swap directions.
        var fromAgy = ParentEntry(adapter: "agy") with { StreamJson = true };
        Assert.False(RedispatchCommand.InheritBinding(fromAgy, new RedispatchOptions("parent-room", "new-room", Adapter: "claude")).StreamJson);

        var fromClaude = ParentEntry(adapter: "claude") with { StreamJson = false };
        Assert.True(RedispatchCommand.InheritBinding(fromClaude, new RedispatchOptions("parent-room", "new-room", Adapter: "agy")).StreamJson);
    }

    /// <summary>Pins the axis rule <see cref="RedispatchCommand.InheritBinding"/>'s own comment cites (#1082).</summary>
    [Fact]
    public void An_adapter_swap_with_no_explicit_model_or_effort_drops_both_rather_than_carrying_them_across()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room", Adapter: "agy"));

        Assert.Null(entry.Model);
        Assert.Null(entry.Effort);
    }

    [Fact]
    public void An_adapter_swap_with_an_explicit_model_and_effort_keeps_them()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var options = new RedispatchOptions("parent-room", "new-room", Adapter: "agy", Model: "gemini-x", Effort: "quick");

        var entry = RedispatchCommand.InheritBinding(parent, options);

        Assert.Equal("gemini-x", entry.Model);
        Assert.Equal("quick", entry.Effort);
    }

    [Fact]
    public void Same_adapter_with_no_override_keeps_the_parents_model_and_effort()
    {
        var parent = ParentEntry(adapter: "claude", model: "opus", effort: "careful");
        var entry = RedispatchCommand.InheritBinding(parent, new RedispatchOptions("parent-room", "new-room"));

        Assert.Equal("opus", entry.Model);
        Assert.Equal("careful", entry.Effort);
    }

    [Fact]
    public void A_timeout_override_wins_over_the_inherited_timeout()
    {
        var parent = ParentEntry(timeout: TimeSpan.FromMinutes(30));
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", Timeout: TimeSpan.FromMinutes(90)));

        Assert.Equal(TimeSpan.FromMinutes(90), entry.Timeout);
    }

    [Fact]
    public void A_workspace_override_replaces_a_plain_working_directory()
    {
        var parent = ParentEntry(workingDirectory: "/repo");
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", WorkspaceDirectory: "/other-repo"));

        Assert.Equal("/other-repo", entry.WorkingDirectory);
        Assert.Null(entry.Worktree);
    }

    /// <summary>
    /// A worktree-shaped parent (an audited grant, RoleDispatch.ToBinding's autoProvisionWorktree
    /// branch) records its workspace on <see cref="WorkerBindingConfigEntry.Worktree"/>'s
    /// <c>Repository</c>, not <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> — a
    /// <c>--workspace</c> override must land on whichever one the parent actually populated.
    /// </summary>
    [Fact]
    public void A_workspace_override_replaces_the_repository_of_an_inherited_worktree_spec()
    {
        var parent = ParentEntry(worktree: new WorktreeWorkspace("/repo", "HEAD"));
        var entry = RedispatchCommand.InheritBinding(
            parent, new RedispatchOptions("parent-room", "new-room", WorkspaceDirectory: "/other-repo"));

        Assert.Null(entry.WorkingDirectory);
        Assert.NotNull(entry.Worktree);
        Assert.Equal("/other-repo", entry.Worktree!.Repository);
        Assert.Equal("HEAD", entry.Worktree!.Ref);
    }
}
