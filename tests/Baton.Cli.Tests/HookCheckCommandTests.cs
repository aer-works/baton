namespace Baton.Cli.Tests;

/// <summary>
/// #543: <see cref="HookCheckCommand"/> is the executable target Claude Code spawns directly (exec
/// form, no shell) for every <c>PreToolUse</c> event. These drive <see cref="HookCheckCommand.Execute"/>
/// directly against the exact stdin shape <c>.vendor-survey/corpus/claude__hooks.md</c> documents
/// (<c>{"tool_name": "...", ...}</c>), rather than only asserting against pre-shaped fixtures, so a
/// regression in field-name handling shows up here.
/// </summary>
public class HookCheckCommandTests
{
    [Fact]
    public void A_tool_named_in_the_denied_list_is_blocked_with_exit_code_2()
    {
        using var stdin = new StringReader("""{"tool_name": "Bash", "tool_input": {"command": "ls"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Edit,Write,Bash");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("Bash", stderr.ToString());
    }

    [Fact]
    public void A_tool_not_named_in_the_denied_list_is_allowed()
    {
        using var stdin = new StringReader("""{"tool_name": "Read", "tool_input": {"file_path": "x"}}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Edit,Write,Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_denied_list_now_denies_because_the_gate_cannot_know(string? deniedToolsRaw)
    {
        using var stdin = new StringReader("""{"tool_name": "Bash"}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, deniedToolsRaw);

        // #600 inverted this deliberately. It used to allow, which meant "AER set the list and nothing
        // is withheld" and "the list never arrived" were the same observable outcome — so a channel
        // that had stopped working looked exactly like one that was. An empty list AER actually sent
        // still allows; it now arrives tagged (`claude:`), which is what makes the two tellable apart.
        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void Matching_is_exact_not_a_substring_or_prefix_match()
    {
        // "Bash" denied must not accidentally deny "BashOutput" or match on a scoped
        // "Bash(rm *)"-shaped tool_input; BuildDisallowedTools never emits scoped entries, so
        // hook-check has no reason to parse them, but an accidental substring match would silently
        // widen the denial beyond what was actually withheld.
        using var stdin = new StringReader("""{"tool_name": "BashOutput"}""");
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"tool_name": null}""")]
    [InlineData("""{"tool_name": ""}""")]
    [InlineData("[]")]
    [InlineData("""{"tool_name": "Write", "tool_input": {"file_path":""")] // truncated mid-payload
    public void Shapeless_stdin_fails_closed_because_writes_ride_this_hook_alone(string stdinContent)
    {
        // Every one of these allowed until #649, on the argument that --disallowedTools covered the
        // same names anyway. #649 moved the write tools off that flag so this hook could allow the
        // one write landing in BATON_OUTPUT_DIR — which makes a parse failure here an ungated write,
        // not a duplicate of an enforcement that still exists elsewhere.
        using var stdin = new StringReader(stdinContent);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(stdin, stderr, "claude:Bash,Edit,Write");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("rather than allowing it unchecked", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_well_formed_payload_still_decides_on_the_grant_rather_than_denying_everything()
    {
        // The control for the theory above. Without it, a change that denied unconditionally would
        // pass every fail-closed assertion while making the gate useless — the worker cannot call a
        // single tool, and the reason string would be identical in both worlds.
        using var denied = new StringReader("""{"tool_name": "Bash"}""");
        using var allowed = new StringReader("""{"tool_name": "Read"}""");
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            HookCheckCommand.Execute(denied, stderr, "claude:Bash,Edit,Write"));
        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(allowed, stderr, "claude:Bash,Edit,Write"));
    }

    [Fact]
    public void An_unreadable_stdin_denies_rather_than_allowing()
    {
        // The IOException arm, which no shaped-input case can reach.
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(new ThrowingReader(), stderr, "claude:Bash,Edit,Write");

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new IOException("pipe closed");
    }

    [Fact]
    public void A_null_stdin_reader_throws_rather_than_silently_allowing()
    {
        using var stderr = new StringWriter();

        Assert.Throws<ArgumentNullException>(() => HookCheckCommand.Execute(null!, stderr, "claude:Bash"));
    }
}
