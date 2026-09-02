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

    // --- #1459: the scoped-shell second layer -------------------------------------------------------

    private static int RunBash(
        string command, string? shellPatternsRaw, string? deniedShellPatternsRaw = null,
        TextWriter? stderr = null)
    {
        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        // "claude:Read" -- Bash is granted (absent from the denied-tool list), which is what lets
        // execution reach the shell-pattern check under test.
        return HookCheckCommand.Execute(
            stdin, stderr ?? new StringWriter(), "claude:Read", shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw);
    }

    [Theory]
    [InlineData("git diff; echo escaped")] // #1461's measured escape row 1
    [InlineData("git diff | grep baseline")] // #1461's measured escape row 2
    public void Regression_the_measured_chaining_escapes_are_denied_by_the_hook(string command)
    {
        // See ShellCommandPatternMatcherTests for why these ran unblocked before #1459. This is the
        // same regression asserted end-to-end through the hook rather than the evaluator directly.
        using var stderr = new StringWriter();

        var exitCode = RunBash(command, "claude:git diff*", stderr: stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("scoped shell grant", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_command_matching_the_scoped_pattern_is_allowed()
    {
        var exitCode = RunBash("git diff", "claude:git diff*");

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Fact]
    public void A_segment_outside_the_scoped_patterns_denies_naming_the_segment()
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash("git diff && npm install", "claude:git diff*", stderr: stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("npm install", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_segment_matching_the_standing_deny_list_denies_even_when_the_allow_list_would_admit_it()
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash(
            "git diff && git push", "claude:git diff*,git push*", "claude:git push*", stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("git push", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git diff $(whoami)")]
    [InlineData("git diff `whoami`")]
    [InlineData("git diff > out.txt")]
    public void An_unparseable_command_fails_closed_under_a_scoped_grant(string command)
    {
        using var stderr = new StringWriter();

        var exitCode = RunBash(command, "claude:git diff*", stderr: stderr);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("unparseable under scoped grant", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude:")] // Present, explicitly unscoped (empty pattern list)
    [InlineData(null)] // Absent -- the channel never arrived (an older AER, or a role never updated)
    [InlineData("agy:git diff*")] // WrongVendor
    public void An_unscoped_or_absent_shell_pattern_channel_leaves_the_second_layer_untouched(
        string? shellPatternsRaw)
    {
        // Point 4 of #1459's design. See HookCheckCommand.Decide's own comment on this branch for why
        // Absent/WrongVendor here reads opposite to the denied-tools channel above.
        var exitCode = RunBash("git diff; echo escaped", shellPatternsRaw);

        Assert.Equal(HookCheckCommand.AllowedExitCode, exitCode);
    }

    [Theory]
    [InlineData("git merge-base --is-ancestor a b", HookCheckCommand.AllowedExitCode)]
    [InlineData("git diff --stat", HookCheckCommand.AllowedExitCode)]
    [InlineData("git status", HookCheckCommand.AllowedExitCode)]
    [InlineData("git difftool --extcmd=calc -y HEAD~1 HEAD", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep -Ocalc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git grep --open-files-in-pager=calc foo", HookCheckCommand.DeniedExitCode)]
    [InlineData("git -c alias.x=!calc x", HookCheckCommand.DeniedExitCode)]
    [InlineData("git push --dry-run", HookCheckCommand.DeniedExitCode)]
    [InlineData("gh api repos/x", HookCheckCommand.DeniedExitCode)]
    [InlineData("gh pr view 1", HookCheckCommand.AllowedExitCode)]
    public void Review_role_command_allow_deny_polarities_from_catalog(string command, int expectedExitCode)
    {
        var review = Baton.Vendors.WorkerRoleCatalog.For("review");
        var shellPatternsRaw = review.Grant.ShellCommandPatterns is { Count: > 0 }
            ? "claude:" + string.Join(",", review.Grant.ShellCommandPatterns)
            : "claude:";
        var deniedShellPatternsRaw = review.Grant.DeniedShellCommandPatterns is { Count: > 0 }
            ? "claude:" + string.Join(",", review.Grant.DeniedShellCommandPatterns)
            : "claude:";

        var payload = """{"tool_name": "Bash", "tool_input": {"command": COMMAND_JSON}}"""
            .Replace("COMMAND_JSON", System.Text.Json.JsonSerializer.Serialize(command));
        using var stdin = new StringReader(payload);
        using var stderr = new StringWriter();

        var exitCode = HookCheckCommand.Execute(
            stdin, stderr, "claude:Edit,Write",
            shellPatternsRaw: shellPatternsRaw,
            deniedShellPatternsRaw: deniedShellPatternsRaw);

        Assert.Equal(expectedExitCode, exitCode);
    }
}
