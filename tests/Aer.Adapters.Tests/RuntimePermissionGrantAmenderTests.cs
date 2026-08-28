using Aer.Adapters.Tests.TestSupport;
using Aer.Flow.Domain;

namespace Aer.Adapters.Tests;

/// <summary>
/// Revoking a room's standing, pre-cleared <see cref="PermissionGrant"/> (0022's permission ladder,
/// #1238) so the NEXT turn enforces the withdrawal. These are cross-instrument on purpose — the
/// load-bearing claim is not "the revoker clears a pattern" but "the pattern the revoker clears is
/// the one <see cref="ClaudeWorkerAdapter"/> stops pre-approving", and the revoker and the adapter
/// each only ever assert against themselves. A drift between the two is exactly the class of defect a
/// single-instrument test cannot see, so the round-trip runs through the real bindings file, the real
/// parser, and the real translator. Grants are seeded directly (via <see cref="WriteSeedRoomAsync"/>)
/// rather than through an answer path — #1417 retired the mid-lane ask/answer/revoke machinery that
/// used to build them; a lane is now dispatched fully pre-cleared.
/// </summary>
public class RuntimePermissionGrantAmenderTests
{
    private const string Worker = InteractiveSessionMaterializer.DefaultWorkerName;

    // A minimal chat-worker binding with NO shell grant -- the pre-amend baseline. Read-only so the
    // "shell got granted" assertions below are unambiguously caused by the amend, not the seed.
    private static WorkerBindingConfigEntry SeedEntry(PermissionGrant? grant = null) =>
        new(
            "claude",
            new WorkerContract(Worker, RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
            "Chat.",
            TimeSpan.FromMinutes(5),
            PermissionGrant: grant ?? new PermissionGrant(ReadFiles: true));

    private static async Task<string> WriteSeedRoomAsync(
        string roomDir, PermissionGrant? grant, CancellationToken ct)
    {
        Directory.CreateDirectory(roomDir);
        var bindingsPath = Path.Combine(roomDir, "bindings.json");
        await WorkerBindingConfigWriter.SaveToFileAsync(
            new Dictionary<string, WorkerBindingConfigEntry> { [Worker] = SeedEntry(grant) }, bindingsPath, ct);
        return bindingsPath;
    }

    private static async Task<PermissionGrant?> ReloadGrantAsync(string bindingsPath, CancellationToken ct)
    {
        var reparsed = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, ct);
        return reparsed[Worker].PermissionGrant;
    }

    // The tools string ClaudeWorkerAdapter pre-approves for a grant, split into individual tokens so a
    // scoped "Bash(rm *)" is distinguishable from an unscoped bare "Bash".
    private static string[] PreApprovedTools(PermissionGrant grant)
    {
        Assert.True(new ClaudeWorkerAdapter().TryTranslatePermissionGrant(grant, out var resolved, out _));
        return resolved!.Split(',');
    }

    // The actual --disallowedTools value the claude adapter spawns for a grant, through the REAL
    // interactive dispatch path.
    private static string ClaudeDisallowedToolsFor(PermissionGrant grant)
    {
        var target = new ClaudeWorkerAdapter().Resolve(
            new WorkerInvocation("Chat.", PermissionGrant: grant),
            new WorkerContract("chat-worker", RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []));
        for (var i = 0; i < target.Args.Count - 1; i++)
        {
            if (target.Args[i] == "--disallowedTools")
            {
                return target.Args[i + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// #1238: revoking the room-wide shell permission actually narrows what the NEXT turn is given.
    /// Cross-instrument — the claim is not "a boolean flipped in a file" but "the adapter stops
    /// pre-approving the shell", and only the round trip through the real writer, parser and
    /// translator can say that.
    /// </summary>
    [Fact]
    public async Task Revoking_the_room_shell_narrows_what_the_adapter_pre_approves()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-room-{Guid.NewGuid():N}");
        try
        {
            // Seeded with the widest rung already granted -- the shape answering the ladder used to
            // produce (unscoped RunShellCommands, empty pattern list) -- so what is revoked is the
            // same shape a real standing permission takes.
            var bindingsPath = await WriteSeedRoomAsync(
                roomDir, new PermissionGrant(ReadFiles: true, RunShellCommands: true, ShellCommandPatterns: []), ct);

            var granted = await ReloadGrantAsync(bindingsPath, ct);
            Assert.True(granted!.RunShellCommands);
            Assert.Contains("Bash", PreApprovedTools(granted));

            var outcome = await RuntimePermissionGrantAmender.RevokeAsync(
                roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct);

            Assert.Equal(PermissionRevokeOutcome.Revoked, outcome);

            var revoked = await ReloadGrantAsync(bindingsPath, ct);
            Assert.False(revoked!.RunShellCommands);
            Assert.Empty(revoked.ShellCommandPatterns ?? []);
            Assert.DoesNotContain("Bash", PreApprovedTools(revoked));

            // Everything else the worker was given is untouched — a revocation that quietly reset the
            // whole grant would pass every assertion above.
            Assert.True(revoked.ReadFiles);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// #1238: revoking ONE command family takes that one back and leaves its siblings alone. The
    /// sibling is the discriminator — a revocation that cleared the list would pass an assertion that
    /// only checked the named family was gone.
    /// </summary>
    [Fact]
    public async Task Revoking_one_command_leaves_the_others_standing()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-cmd-{Guid.NewGuid():N}");
        try
        {
            var bindingsPath = await WriteSeedRoomAsync(
                roomDir,
                new PermissionGrant(ReadFiles: true, RunShellCommands: true, ShellCommandPatterns: ["rm *", "git *"]),
                ct);

            var outcome = await RuntimePermissionGrantAmender.RevokeAsync(
                roomDir, Worker, PermissionRevokeKind.CommandInRoom, "rm *", ct);

            Assert.Equal(PermissionRevokeOutcome.Revoked, outcome);

            var revoked = await ReloadGrantAsync(bindingsPath, ct);
            Assert.Equal(["git *"], revoked!.ShellCommandPatterns);

            // Both directions at the enforcing matcher, which is what a worker actually meets.
            Assert.False(ShellCommandPatternMatcher.IsAllowed("rm -rf build/", revoked.ShellCommandPatterns));
            Assert.True(ShellCommandPatternMatcher.IsAllowed("git status", revoked.ShellCommandPatterns));

            // The shell itself stays granted: taking back one family is not taking back the shell.
            Assert.True(revoked.RunShellCommands);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// #1256, whose mechanism is stated where the fix is (<c>TryRevoke</c>'s last-pattern branch).
    /// What this pins is the polarity: the sibling above leaves a pattern behind, and that surviving
    /// sibling is exactly what hid this case for a whole PR cycle.
    /// </summary>
    /// <remarks>
    /// Asserted at the translator, not only on the fields: the fields are where the bug looks
    /// harmless, and <c>--allowedTools</c> is where it becomes authority. A version that only cleared
    /// the list would satisfy every field assertion here and still emit a bare <c>Bash</c>.
    /// </remarks>
    [Fact]
    public async Task Revoking_the_last_command_takes_the_shell_with_it_rather_than_widening_to_any_command()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-last-{Guid.NewGuid():N}");
        try
        {
            var bindingsPath = await WriteSeedRoomAsync(
                roomDir, new PermissionGrant(ReadFiles: true, RunShellCommands: true, ShellCommandPatterns: ["rm *"]), ct);

            // The premise, asserted rather than assumed: one family stands, and the shell is granted
            // in the scoped form. Without this the test could pass against a room that never granted.
            var granted = await ReloadGrantAsync(bindingsPath, ct);
            Assert.Equal(["rm *"], granted!.ShellCommandPatterns);
            Assert.True(granted.RunShellCommands);

            var outcome = await RuntimePermissionGrantAmender.RevokeAsync(
                roomDir, Worker, PermissionRevokeKind.CommandInRoom, "rm *", ct);

            Assert.Equal(PermissionRevokeOutcome.Revoked, outcome);

            var revoked = await ReloadGrantAsync(bindingsPath, ct);
            Assert.Empty(revoked!.ShellCommandPatterns!);
            Assert.False(revoked.RunShellCommands);

            // What the worker actually meets. An unscoped "Bash" here is the widening this exists to
            // catch, and it is invisible from the fields alone.
            Assert.True(new ClaudeWorkerAdapter().TryTranslatePermissionGrant(revoked, out var allowedTools, out _));
            Assert.DoesNotContain("Bash", allowedTools ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// #1238's polarity pair, and the one this feature could most easily get wrong: revocation is not
    /// a route back into a standing refusal. <see cref="PermissionRevokeKind"/> states why; this pins
    /// that the code obeys it.
    /// </summary>
    [Fact]
    public async Task Revoking_an_allow_never_lifts_a_standing_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-deny-{Guid.NewGuid():N}");
        try
        {
            var bindingsPath = await WriteSeedRoomAsync(
                roomDir,
                new PermissionGrant(
                    ReadFiles: true, RunShellCommands: true, ShellCommandPatterns: [],
                    DeniedShellCommandPatterns: ["curl *"]),
                ct);

            // Both revocations, since either could be the one that reaches too far.
            Assert.Equal(
                PermissionRevokeOutcome.Revoked,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct));

            var afterRoomRevoke = await ReloadGrantAsync(bindingsPath, ct);
            Assert.Equal(["curl *"], afterRoomRevoke!.DeniedShellCommandPatterns);

            // And it is still ENFORCED, not merely still written down: the deny reaches the claude
            // adapter's --disallowedTools on the interactive path even with the shell withdrawn.
            Assert.Contains("Bash(curl *)", ClaudeDisallowedToolsFor(afterRoomRevoke));

            // There is no revoke kind that names the deny list at all — asking for one is a caller bug,
            // refused loudly rather than quietly interpreted as some nearby operation.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionDecisionKind.DenyAlways, "curl *", ct));

            var afterAttempt = await ReloadGrantAsync(bindingsPath, ct);
            Assert.Equal(["curl *"], afterAttempt!.DeniedShellCommandPatterns);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// #1238: revoking what was never held is not a failure. A surface that offered revocation would
    /// otherwise have to prove first what is held, and revoking twice would report an error the second
    /// time for a state that is exactly what the operator asked for.
    /// </summary>
    [Fact]
    public async Task Revoking_what_was_never_held_is_a_no_op_not_a_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-noop-{Guid.NewGuid():N}");
        try
        {
            var bindingsPath = await WriteSeedRoomAsync(roomDir, grant: null, ct);

            Assert.Equal(
                PermissionRevokeOutcome.NothingToRevoke,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct));
            Assert.Equal(
                PermissionRevokeOutcome.NothingToRevoke,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.CommandInRoom, "rm *", ct));

            // Idempotence, on the arm that DID something: granting, revoking, revoking again.
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    [Worker] = SeedEntry(new PermissionGrant(ReadFiles: true, RunShellCommands: true, ShellCommandPatterns: [])),
                },
                bindingsPath,
                ct);
            Assert.Equal(
                PermissionRevokeOutcome.Revoked,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct));
            Assert.Equal(
                PermissionRevokeOutcome.NothingToRevoke,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct));

            var grant = await ReloadGrantAsync(bindingsPath, ct);
            Assert.False(grant!.RunShellCommands);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// #1238's second reader: a worker bound with NO grant at all is a real, reachable state —
    /// <c>WorkerBindingConfigEntry.PermissionGrant</c> defaults to null, and a worker that has never
    /// answered a persisting rung sits in exactly it. Every other test here seeds a non-null grant
    /// (<c>SeedEntry</c>'s own default), so this branch had no coverage: a refactor that threw on the
    /// null, or that answered <c>Revoked</c> for it, would have passed the whole file.
    /// </summary>
    [Fact]
    public async Task Revoking_from_a_worker_with_no_grant_at_all_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-nogrant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);
        try
        {
            // Deliberately NOT through WriteSeedRoomAsync, whose seed coalesces null to a real grant.
            var bindingsPath = Path.Combine(roomDir, "bindings.json");
            await WorkerBindingConfigWriter.SaveToFileAsync(
                new Dictionary<string, WorkerBindingConfigEntry>
                {
                    [Worker] = new(
                        "claude",
                        new WorkerContract(Worker, RequiredInputs: [], ProducedOutputs: [], OptionalMetadata: []),
                        "Chat.",
                        TimeSpan.FromMinutes(5)),
                },
                bindingsPath,
                ct);

            // The premise, asserted rather than assumed — the writer/parser round trip could have
            // materialized a default grant, which would make the rest of this test vacuous.
            Assert.Null(await ReloadGrantAsync(bindingsPath, ct));

            Assert.Equal(
                PermissionRevokeOutcome.NothingToRevoke,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct));
            Assert.Equal(
                PermissionRevokeOutcome.NothingToRevoke,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.CommandInRoom, "rm *", ct));

            // And nothing was written — a no-op that invented an empty grant would be a change to a
            // binding the operator never touched.
            Assert.Null(await ReloadGrantAsync(bindingsPath, ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }

    /// <summary>
    /// #1238: a room with no <c>bindings.json</c> reports <see cref="PermissionRevokeOutcome.CouldNotPersist"/>,
    /// NOT <see cref="PermissionRevokeOutcome.NothingToRevoke"/>. The two are a real distinction here:
    /// "you hold nothing" and "I cannot tell you what you hold" mean different things to someone who
    /// just asked for a permission to be withdrawn.
    /// </summary>
    [Fact]
    public async Task Revoking_in_a_room_with_no_bindings_is_reported_as_a_failure_not_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var roomDir = Path.Combine(Path.GetTempPath(), $"revoke-nofile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDir);
        try
        {
            Assert.Equal(
                PermissionRevokeOutcome.CouldNotPersist,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, Worker, PermissionRevokeKind.RoomShell, cancellationToken: ct));

            // And the same for a bindings file that exists but has no such worker.
            await WriteSeedRoomAsync(roomDir, grant: null, ct);
            Assert.Equal(
                PermissionRevokeOutcome.CouldNotPersist,
                await RuntimePermissionGrantAmender.RevokeAsync(roomDir, "not-a-worker-in-this-room", PermissionRevokeKind.RoomShell, cancellationToken: ct));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDir);
        }
    }
}
