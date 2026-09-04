using Baton.Vendors;
using Baton.Domain;

namespace Baton.Vendors.Tests;

/// <summary>
/// #649 split Claude's two enforcement channels apart: <c>--disallowedTools</c> keeps every withheld
/// category except writes, and the <c>PreToolUse</c> hook's list keeps all of them. Asserted across
/// every grant rather than on hand-picked ones, because the failure this guards against is a
/// category silently falling off <em>one</em> channel — which two examples cannot see and sixteen can.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class ChannelPopulationTests
{
    private static readonly WorkerContract Contract =
        new("reviewer", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []);

    private static readonly string[] WriteTools = ["Edit", "Write", "NotebookEdit"];

    public static TheoryData<bool, bool, bool, bool> EveryGrant()
    {
        var data = new TheoryData<bool, bool, bool, bool>();
        foreach (var read in new[] { false, true })
        {
            foreach (var write in new[] { false, true })
            {
                foreach (var shell in new[] { false, true })
                {
                    foreach (var network in new[] { false, true })
                    {
                        data.Add(read, write, shell, network);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryGrant))]
    public void The_hook_list_is_the_deny_flag_plus_exactly_the_write_tools_when_writes_are_withheld(
        bool read, bool write, bool shell, bool network)
    {
        var grant = new PermissionGrant(
            ReadFiles: read, WriteFiles: write, RunShellCommands: shell, NetworkAccess: network);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Review this.", PermissionGrant: grant, AllowsSubagents: true), Contract);

        var flag = Split(target.Args.SkipWhile(a => a != "--disallowedTools").Skip(1).FirstOrDefault());
        var hook = Split(
            target.Environment!.Single(v => v.Name == ClaudeWorkerAdapter.DeniedToolsVariable).Value
                .Split(':', 2)[1]);

        var expected = new HashSet<string>(flag, StringComparer.Ordinal);
        if (!write)
        {
            expected.UnionWith(WriteTools);
        }

        Assert.Equal(expected, hook);

        // Polarity, both directions: a withheld write is on the hook and off the flag; a granted one
        // is on neither. Without this, a build that put writes back on the flag would still satisfy
        // the set equation above, since the flag is what the expectation is derived from.
        foreach (var tool in WriteTools)
        {
            Assert.DoesNotContain(tool, flag);
            Assert.Equal(!write, hook.Contains(tool));
        }
    }

    [Theory]
    [MemberData(nameof(EveryGrant))]
    public void No_withheld_category_other_than_writes_leaves_the_deny_flag(
        bool read, bool write, bool shell, bool network)
    {
        var grant = new PermissionGrant(
            ReadFiles: read, WriteFiles: write, RunShellCommands: shell, NetworkAccess: network);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Review this.", PermissionGrant: grant), Contract);

        var flag = Split(target.Args.SkipWhile(a => a != "--disallowedTools").Skip(1).FirstOrDefault());

        Assert.Equal(!read, flag.Contains("Read"));
        Assert.Equal(!shell, flag.Contains("Bash"));
        Assert.Equal(!network, flag.Contains("WebFetch"));
        Assert.Equal(!network, flag.Contains("WebSearch"));
    }

    [Theory]
    [MemberData(nameof(EveryGrant))]
    public void The_allow_list_pre_approves_the_write_tools_under_every_grant_including_all_deny(
        bool read, bool write, bool shell, bool network)
    {
        // The population this actually touches is wider than "reviewers". A directory-less
        // interactive session's grant is all-false, so it moved from writes actively denied on
        // --disallowedTools to writes pre-approved and hook-confined -- which is what lets a chat
        // worker write its response file at all. Pre-approval is not a ceiling
        // (gate.allowedtools-is-preapproval-not-ceiling); the hook is what still refuses the
        // workspace.
        var grant = new PermissionGrant(
            ReadFiles: read, WriteFiles: write, RunShellCommands: shell, NetworkAccess: network);
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Review this.", PermissionGrant: grant), Contract);

        var allowed = Split(target.Args.SkipWhile(a => a != "--allowedTools").Skip(1).FirstOrDefault());

        foreach (var tool in WriteTools)
        {
            Assert.Contains(tool, allowed);
        }

        // Polarity on a category that did NOT change, so a build that pre-approved everything fails
        // here rather than passing the assertion above for the wrong reason.
        Assert.Equal(read, allowed.Contains("Read"));
        Assert.Equal(network, allowed.Contains("WebFetch"));
    }

    private static HashSet<string> Split(string? commaJoined) =>
        string.IsNullOrEmpty(commaJoined)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(commaJoined.Split(','), StringComparer.Ordinal);
}
