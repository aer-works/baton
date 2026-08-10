using Aer.Tests.Shared;

namespace Aer.Cli.Tests;

/// <summary>
/// #445: <see cref="HookCheckCommand"/>'s third outcome. Until this the hook was binary — allow or
/// exit 2 — and an ungranted capability in a chat session hard-failed with a human sitting right
/// there. These drive the ternary in BOTH directions, because the ask band and the denied band are one
/// condition apart and asserting only the new one cannot see it swallowing the old.
/// </summary>
public class HookAskBandTests
{
    private static readonly string BashCall =
        """{"tool_name": "Bash", "tool_input": {"command": "ls"}}""";

    [Fact]
    public void A_tool_in_the_ask_band_returns_the_measured_ask_envelope_on_stdout_and_exits_zero()
    {
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:", askToolsRaw: "claude:Bash,WebFetch,WebSearch", stdout: stdout);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);

        // The EXACT shape, not a substring probe: gate.hook-ask-in-auto measured this envelope, and a
        // field claude does not recognise degrades silently to an allow rather than failing loudly.
        Assert.Equal(HookCheckCommand.AskDecisionJson, stdout.ToString().Trim());
        Assert.Contains("\"permissionDecision\":\"ask\"", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"hookEventName\":\"PreToolUse\"", stdout.ToString(), StringComparison.Ordinal);

        // stderr stays clean: on this vendor stdout carries the structured decision, and a stray
        // reason on the wrong channel is not what the ask path is for.
        Assert.Equal(string.Empty, stderr.ToString());
    }

    /// <summary>
    /// THE CONTROL. Identical stdin, identical denied list, ask band UNSET — today's behaviour, and
    /// nothing on stdout. If this failed, the test above would be measuring the harness rather than
    /// the flag.
    /// </summary>
    [Fact]
    public void The_same_call_with_no_ask_band_behaves_exactly_as_it_did_before()
    {
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var allowed = HookCheckCommand.Execute(stdin, stderr, "claude:", stdout: stdout);

        Assert.Equal(HookCheckCommand.AllowedExitCode, allowed);
        Assert.Equal(string.Empty, stdout.ToString());

        // And the other half of "unchanged": a withheld Bash on the DENIED list still exits 2 with its
        // reason on stderr, which is the one-shot pipeline's fail-closed default.
        using var stdin2 = new StringReader(BashCall);
        using var stderr2 = new StringWriter();
        using var stdout2 = new StringWriter();

        var denied = HookCheckCommand.Execute(stdin2, stderr2, "claude:Bash", stdout: stdout2);

        Assert.Equal(HookCheckCommand.DeniedExitCode, denied);
        Assert.Equal(string.Empty, stdout2.ToString());
        Assert.Contains("Bash", stderr2.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Order matters and is asserted, not assumed: deny wins over ask when a tool is in both bands.
    /// The reason it is deny and not ask — a decision the operator already closed is not reopened — is
    /// recorded once beside the ask-list parse in <see cref="HookCheckCommand"/>.
    /// </summary>
    [Fact]
    public void A_tool_in_both_bands_is_denied_rather_than_asked()
    {
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:Bash", askToolsRaw: "claude:Bash", stdout: stdout);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    /// <summary>
    /// The ask band is exact-match, like the denied band: <c>Bash</c> in the band must not sweep
    /// <c>BashOutput</c> into a prompt the human never needed to see.
    /// </summary>
    [Fact]
    public void The_ask_band_matches_exactly_rather_than_by_prefix()
    {
        using var stdin = new StringReader("""{"tool_name": "BashOutput"}""");
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:", askToolsRaw: "claude:Bash", stdout: stdout);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    /// <summary>
    /// Another vendor's ask band is not this gate's, exactly as #600 already rules for the denied
    /// band — and its effect here is to leave the ask path off, not to deny. Denying on a foreign ask
    /// band would make a wrong-vendor variable stricter than no variable at all, which is a different
    /// failure from the one #600 closes.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("agy:run_command")]
    [InlineData("Bash")]
    public void An_absent_blank_untagged_or_foreign_ask_band_leaves_the_ask_path_off(string? askToolsRaw)
    {
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:", askToolsRaw: askToolsRaw, stdout: stdout);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    /// <summary>
    /// The ask path must not widen any fail-closed exit. Every one of these is judged before a tool
    /// name exists to compare against a band, and all of them still deny with the ask band present.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"tool_name": ""}""")]
    public void Shapeless_stdin_still_fails_closed_with_an_ask_band_present(string stdinContent)
    {
        using var stdin = new StringReader(stdinContent);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:", askToolsRaw: "claude:Bash", stdout: stdout);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    /// <summary>
    /// An absent denied list denies (#600) before the ask band is ever consulted — the ask band does
    /// not repair a broken channel, and a gate that cannot hear what is withheld must not start
    /// prompting on its own authority.
    /// </summary>
    [Fact]
    public void An_ask_band_does_not_rescue_a_missing_denied_list()
    {
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, deniedToolsRaw: null, askToolsRaw: "claude:Bash", stdout: stdout);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    /// <summary>
    /// No stdout to write the envelope to is a gate that cannot ask, and it denies rather than
    /// allowing — the same direction everything else this command cannot judge fails in.
    /// </summary>
    [Fact]
    public void An_ask_with_no_stdout_channel_denies_rather_than_allowing()
    {
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:", askToolsRaw: "claude:Bash", stdout: null);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("rather than allowing it unchecked", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ask band reaches a write-family tool BEFORE the granted-write bound (#679) does. Under the
    /// gate a withheld write is an ask, not a silent denial for landing outside the workspace — and
    /// #679's bound still applies to everything the band does not name.
    /// </summary>
    [Fact]
    public void A_write_in_the_ask_band_asks_while_one_outside_it_is_still_bounded()
    {
        var outside = OperatingSystem.IsWindows() ? @"C:\elsewhere\x.txt" : "/elsewhere/x.txt";
        var quoted = System.Text.Json.JsonSerializer.Serialize(outside);
        var payload = """{"tool_name": "Write", "tool_input": {"file_path": QUOTED}}"""
            .Replace("QUOTED", quoted, StringComparison.Ordinal);

        using var stdinAsk = new StringReader(payload);
        using var stderrAsk = new StringWriter();
        using var stdoutAsk = new StringWriter();
        var asked = HookCheckCommand.Execute(
            stdinAsk, stderrAsk, "claude:", askToolsRaw: "claude:Write", stdout: stdoutAsk);

        Assert.Equal(HookCheckCommand.AllowedExitCode, asked);
        Assert.Equal(HookCheckCommand.AskDecisionJson, stdoutAsk.ToString().Trim());

        using var stdinBound = new StringReader(payload);
        using var stderrBound = new StringWriter();
        using var stdoutBound = new StringWriter();
        var bounded = HookCheckCommand.Execute(
            stdinBound, stderrBound, "claude:", askToolsRaw: "claude:Bash", stdout: stdoutBound);

        Assert.Equal(HookCheckCommand.DeniedExitCode, bounded);
        Assert.Equal(string.Empty, stdoutBound.ToString());
    }

    /// <summary>
    /// #445 + #649, the case the ask band could silently break: a withheld write whose target is the
    /// worker's own declared output (the outbox) must be ALLOWED, never asked. A directory-less chat withholds
    /// writes, so under the gate <c>Write</c> rides the ask band — and without the outbox exemption
    /// hoisted above the ask branch, the worker would be prompted for permission to write the very
    /// report it was dispatched to produce. Assert stdout is EMPTY (an allow), not the ask envelope.
    /// The paired arm — the same withheld write to a NON-outbox target — still asks, so this proves the
    /// exemption is scoped to the outbox rather than blanket-allowing every ask-band write.
    /// </summary>
    [Fact]
    public void A_withheld_write_into_the_outbox_is_allowed_not_asked_under_the_gate()
    {
        var outbox = Path.Combine(Path.GetTempPath(), $"hookask-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outbox);
        try
        {
            var target = Path.Combine(outbox, "response.md");
            var quoted = System.Text.Json.JsonSerializer.Serialize(target);
            var payload = """{"tool_name": "Write", "tool_input": {"file_path": QUOTED}}"""
                .Replace("QUOTED", quoted, StringComparison.Ordinal);

            // Withheld write (Write on the ask band), targeting the outbox: allowed, and nothing on stdout.
            using var stdin = new StringReader(payload);
            using var stderr = new StringWriter();
            using var stdout = new StringWriter();
            var exit = HookCheckCommand.Execute(
                stdin, stderr, "claude:", outboxDirectory: outbox, askToolsRaw: "claude:Write", stdout: stdout);

            Assert.Equal(HookCheckCommand.AllowedExitCode, exit);
            Assert.Equal(string.Empty, stdout.ToString());

            // Control: the SAME withheld write to a non-outbox target still asks — the exemption is the
            // outbox, not the ask band being toothless.
            var elsewhere = OperatingSystem.IsWindows() ? @"C:\elsewhere\response.md" : "/elsewhere/response.md";
            var elsewherePayload = """{"tool_name": "Write", "tool_input": {"file_path": QUOTED}}"""
                .Replace("QUOTED", System.Text.Json.JsonSerializer.Serialize(elsewhere), StringComparison.Ordinal);
            using var stdin2 = new StringReader(elsewherePayload);
            using var stderr2 = new StringWriter();
            using var stdout2 = new StringWriter();
            var exit2 = HookCheckCommand.Execute(
                stdin2, stderr2, "claude:", outboxDirectory: outbox, askToolsRaw: "claude:Write", stdout: stdout2);

            Assert.Equal(HookCheckCommand.AllowedExitCode, exit2);
            Assert.Equal(HookCheckCommand.AskDecisionJson, stdout2.ToString().Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(outbox);
        }
    }

    /// <summary>
    /// THE CROSS-INSTRUMENT CONTRACT (#445): the value <see cref="Aer.Adapters.ClaudeWorkerAdapter"/>
    /// actually PRODUCES for <c>AER_HOOK_ASK_TOOLS</c>, fed into the real hook — not a hand-written
    /// string. Every other test here feeds the hook a literal, and the adapter tests assert the adapter
    /// emits <c>$"claude:{withheld}"</c>; both assert against themselves, so if
    /// <c>BuildHookDeniedTools</c> ever emitted a shape <c>DeniedToolList.Parse</c> reads as
    /// NOT-Present, every branch would fall through to allow and BOTH suites would stay green while the
    /// gate silently never fired. This is the one arm that fails when the two sides disagree — the same
    /// defect shape as the doorbell test that passed with its subject deleted.
    /// </summary>
    [Fact]
    public void The_ask_band_string_the_adapter_emits_is_one_the_hook_accepts_and_asks_on()
    {
        // The grant the interactive gate actually ships under (with a directory: read+write, no shell).
        var grant = Aer.Adapters.InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory("/some/project");
        var target = new Aer.Adapters.ClaudeWorkerAdapter().Resolve(
            new Aer.Adapters.WorkerInvocation("Chat.", PermissionGrant: grant, EnablePermissionGate: true),
            new Aer.Flow.Domain.WorkerContract("architect", [], [new Aer.Flow.Domain.ProducedOutput("plan.md")], []));

        // The REAL adapter output, verbatim — not a literal this test authored.
        var askEnv = target.Environment!.Single(e => e.Name == Aer.Adapters.ClaudeWorkerAdapter.AskToolsVariable).Value;

        // Bash is withheld by that grant (no shell), so the adapter must have put it on the ask band.
        using var stdin = new StringReader(BashCall);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var exit = HookCheckCommand.Execute(stdin, stderr, "claude:", askToolsRaw: askEnv, stdout: stdout);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exit);
        Assert.Equal(HookCheckCommand.AskDecisionJson, stdout.ToString().Trim());
    }

    /// <summary>
    /// The mirror contract: <c>Aer.Adapters</c> cannot reference <c>Aer.Cli</c>, so nothing but a test
    /// holds the two spellings of this variable name in agreement. Same shape and same reason as
    /// <c>DeniedToolChannelTests</c>'s.
    /// </summary>
    [Fact]
    public void The_ask_band_variable_name_is_the_same_string_on_both_sides()
    {
        Assert.Equal(
            Aer.Adapters.ClaudeWorkerAdapter.AskToolsVariable,
            HookCheckCommand.AskToolsEnvironmentVariable);
        Assert.Equal("AER_HOOK_ASK_TOOLS", HookCheckCommand.AskToolsEnvironmentVariable);
    }
}
