using Baton.Vendors;
using Baton.Domain;

namespace Baton.Cli.Tests;

/// <summary>
/// The one test that sees both sides of #649's write-tool split. <c>Baton.Vendors</c> cannot reference
/// <c>Baton.Cli</c>, so the adapter decides which tools leave <c>--disallowedTools</c> for the hook and
/// the hook decides which tools the outbox exemption covers, with nothing holding the two in
/// agreement.
/// </summary>
/// <remarks>
/// The adapter's side is <b>derived from a real <c>Resolve</c></b>, never restated: the names that
/// appear on the hook channel but not on the deny flag *are* the tools #649 moved. Writing the list
/// out again here would be a second copy that agrees with the first until someone edits one.
/// </remarks>
public class WriteFamilyContractTests
{
    [Fact]
    public void The_tools_the_adapter_moves_onto_the_hook_are_exactly_the_ones_the_exemption_covers()
    {
        // Writes withheld, shell withheld -- the reviewer grant, and the one shape where the two
        // channels differ. Shell must stay withheld or #529's refusal rejects the binding before it
        // can be resolved.
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation(
                "Review this.",
                PermissionGrant: new PermissionGrant(
                    ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false)),
            new WorkerContract("reviewer", [], [], []));

        var flag = Split(Arg(target, "--disallowedTools"));
        var hook = Split(
            target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable)
                .Value.Split(':', 2)[1]);

        var movedToTheHook = hook.Except(flag).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(HookCheckCommand.WriteFamilyTools.OrderBy(t => t), movedToTheHook.OrderBy(t => t));
    }

    [Fact]
    public void A_tool_the_exemption_does_not_cover_cannot_reach_the_outbox()
    {
        // Polarity, and the reason the equality above matters. A name on the hook channel that
        // WriteFamilyTools does not carry gets no write target extracted, so IsInsideOutbox is asked
        // about null and denies -- a worker unable to write its own declared output, which is #629's
        // pay-then-fail rather than a permission hole.
        var outbox = Path.Combine(Path.GetTempPath(), "baton-task", "artifacts", "execution_1");
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            tool_name = "MultiEdit",
            tool_input = new { file_path = Path.Combine(outbox, "review.md") },
        });

        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(payload), stderr, "claude:MultiEdit", outbox);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.DoesNotContain("MultiEdit", string.Join(',', HookCheckCommand.WriteFamilyTools));
    }

    /// <summary>
    /// #679's agy mirror. <see cref="AgyHookCheckCommand.WriteFamilyTools"/> decides which agy calls
    /// get their target bounded; see that member for which way a missing name fails, and why it is
    /// the opposite polarity from the claude side above.
    /// </summary>
    /// <remarks>
    /// The adapter's side is derived from two real resolves that differ in one field, so the
    /// difference between their denied lists <i>is</i> the write family. Writing the four names out
    /// here would be a third copy agreeing with the other two until someone edited one.
    /// </remarks>
    [Fact]
    public void The_agy_tools_whose_target_is_bounded_are_exactly_the_adapters_write_family()
    {
        // Shell stays withheld in both arms: #529's refusal rejects a binding that withholds writes
        // while granting the shell, so varying writes alone is the only legal comparison.
        var denied = (bool writeFiles) => Split(
            new AgyWorkerAdapter().Resolve(
                new WorkerInvocation(
                    "Review this.",
                    PermissionGrant: new PermissionGrant(
                        ReadFiles: true, WriteFiles: writeFiles, RunShellCommands: false,
                        NetworkAccess: false)),
                new WorkerContract("reviewer", [], [], []))
                .Environment!.Single(v => v.Name == AgyWorkerAdapter.DeniedToolsVariable)
                .Value.Split(':', 2)[1]);

        var writeTools = denied(false).Except(denied(true)).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            AgyHookCheckCommand.WriteFamilyTools.OrderBy(t => t, StringComparer.Ordinal),
            writeTools.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// #708: every agy write-family tool must say which argument names the path it writes to, because
    /// the field is NOT uniform across the family.
    /// </summary>
    /// <remarks>
    /// What went wrong and why it stayed hidden is recorded once, on
    /// <see cref="AgyHookCheckCommand.WriteTargetFields"/>. This test is the structural half: add a
    /// tool to <see cref="AgyHookCheckCommand.WriteFamilyTools"/> without naming the argument that
    /// carries its target and this goes red, rather than that tool becoming silently always-denied.
    /// </remarks>
    [Fact]
    public void Every_agy_write_family_tool_names_the_argument_that_carries_its_target()
    {
        Assert.NotEmpty(AgyHookCheckCommand.WriteFamilyTools);

        foreach (var tool in AgyHookCheckCommand.WriteFamilyTools)
        {
            Assert.True(
                AgyHookCheckCommand.WriteTargetFields.TryGetValue(tool, out var fields),
                $"'{tool}' is bounded by the gate but names no argument carrying its write target, "
                + "so every call to it resolves to a null path and is denied even when granted (#708).");
            Assert.NotEmpty(fields);
        }

        // The reverse, so the map cannot drift into naming tools the gate never consults.
        foreach (var tool in AgyHookCheckCommand.WriteTargetFields.Keys)
        {
            Assert.Contains(tool, AgyHookCheckCommand.WriteFamilyTools);
        }
    }

    /// <summary>
    /// Both adapters tell the gate where the workspace is (#679), under the one name each side spells
    /// out independently — see <see cref="WorkerEnvironment.WorkspaceVariable"/> for which way a
    /// mismatch fails.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("agy")]
    public void An_adapter_passes_its_working_directory_to_the_gate_and_omits_it_when_there_is_none(
        string vendor)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "baton-workspace");
        // #1166: the ceiling gate now runs before this test's own concern (the workspace env var), so
        // trust the fixture path unrestricted first.
        ProjectCeilingStore.Set(workspace, ProjectCeiling.Unrestricted, ProjectCeilingStore.DefaultPath);
        IWorkerAdapter adapter = vendor == "claude"
            ? new ClaudeWorkerAdapter()
            : new AgyWorkerAdapter();
        var contract = new WorkerContract("reviewer", [], [], []);

        var withWorkspace = adapter.Resolve(
            new WorkerInvocation("Review this.", WorkingDirectory: workspace), contract);
        Assert.Equal(
            workspace,
            withWorkspace.Environment!.Single(v => v.Name == HookCheckCommand.WorkspaceEnvironmentVariable).Value);

        // The polarity arm, and the one carrying the decision: absent rather than empty, so the gate
        // can tell an undeclared workspace from a declared one it could not resolve.
        var withoutWorkspace = adapter.Resolve(new WorkerInvocation("Review this."), contract);
        Assert.DoesNotContain(
            withoutWorkspace.Environment!,
            v => v.Name == HookCheckCommand.WorkspaceEnvironmentVariable);
    }

    private static string? Arg(Baton.Dispatch.CoreDispatchTarget target, string flag)
    {
        for (var i = 0; i < target.Args.Count - 1; i++)
        {
            if (target.Args[i] == flag)
            {
                return target.Args[i + 1];
            }
        }

        return null;
    }

    private static HashSet<string> Split(string? commaJoined) =>
        string.IsNullOrEmpty(commaJoined)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(commaJoined.Split(','), StringComparer.Ordinal);
}
