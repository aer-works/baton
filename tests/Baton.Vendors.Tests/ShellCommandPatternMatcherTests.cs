using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

public class ShellCommandPatternMatcherTests
{
    [Theory]
    [InlineData("git status")]
    [InlineData("git commit -m \"msg\"")]
    [InlineData("git commit -m \"a;b\"")] // quoted metacharacter ';' is literal
    [InlineData("git commit -m 'c&&d'")] // quoted metacharacter '&&' is literal
    [InlineData("git commit -m \"cost: \\$5\"")] // escaped '$' in double quotes does not expand; still allowed
    [InlineData("git commit -m \"a $HOME b\"")] // bare $VAR in double quotes is not word-split; no injection
    [InlineData("git push --force")]
    public void Allowed_commands_matching_patterns_pass(string commandLine)
    {
        string[] patterns = ["git *"];
        Assert.True(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("npm install")] // non-git
    [InlineData("git status; whoami")] // chained semicolon
    [InlineData("git status && rm -rf x")] // chained logical AND
    [InlineData("git log | sh")] // pipe
    [InlineData("git log $(whoami)")] // command substitution $(...)
    [InlineData("git log `whoami`")] // backtick command substitution
    [InlineData("git log \"$(whoami)\"")] // command substitution EXECUTES inside double quotes
    [InlineData("git log \"`whoami`\"")] // backtick substitution EXECUTES inside double quotes
    [InlineData("git log \"${USER}\"")] // parameter expansion inside double quotes
    [InlineData("git log \"$((1+1))\"")] // arithmetic expansion inside double quotes ($( prefix)
    [InlineData("git show > /etc/x")] // output redirection
    [InlineData("git log < /etc/passwd")] // input redirection
    [InlineData("(git status)")] // subshell
    [InlineData("git status & disown")] // backgrounding
    [InlineData("git status\nwhoami")] // newline
    [InlineData("git status\rwhoami")] // CR
    [InlineData("git status \\")] // line continuation backslash outside quotes
    [InlineData("git log ${USER}")] // variable expansion ${...}
    [InlineData("git log $HOME")] // bare unquoted $VAR is denied (quote it for a literal)
    [InlineData("git $'\\''; rm -rf / #'")] // ANSI-C $'...' escape: bash runs `git '` then a live `; rm -rf /`
    [InlineData("git $'\\x3b' whoami")] // ANSI-C $'...' with an escaped byte -- denied outright via bare $
    public void Security_controls_deny_unquoted_metacharacters_and_non_matching_commands(string commandLine)
    {
        string[] patterns = ["git *"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Theory]
    [InlineData("'git status")] // unclosed single quote
    [InlineData("\"git status")] // unclosed double quote
    public void Unclosed_quotes_are_denied(string commandLine)
    {
        string[] patterns = ["git *"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, patterns));
    }

    [Fact]
    public void Pattern_precision_allows_exact_and_denies_partial_or_other_subcommands()
    {
        string[] patterns = ["git status"];
        Assert.True(ShellCommandPatternMatcher.IsAllowed("git status", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed("git statusfoo", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed("git push", patterns));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("anything")]
    public void Empty_or_null_patterns_deny_everything(string commandLine)
    {
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, []));
        Assert.False(ShellCommandPatternMatcher.IsAllowed(commandLine, null));
    }

    [Fact]
    public void Empty_or_whitespace_commandLine_returns_false()
    {
        string[] patterns = ["git *"];
        Assert.False(ShellCommandPatternMatcher.IsAllowed("", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed("   ", patterns));
        Assert.False(ShellCommandPatternMatcher.IsAllowed(null, patterns));
    }

    // --- EvaluateChainedCommand (#1459): the hook-side second layer ---------------------------------

    [Theory]
    [InlineData("git diff; echo escaped", "echo escaped")] // #1461's measured escape row 1
    [InlineData("git diff | grep baseline", "grep baseline")] // #1461's measured escape row 2
    public void Regression_the_measured_escape_rows_are_denied_naming_the_offending_segment(
        string commandLine, string expectedSegment)
    {
        // #1461 measured both of these as executing, unblocked, under `--allowedTools "Bash(git diff*)"`
        // -- claude's own pattern match is against the WHOLE command line, so the unlisted second half
        // rode the allowed prefix past it. This is the hole the hook-side segment check exists to close.
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, null);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
        Assert.Equal(expectedSegment, result.Segment);
    }

    [Fact]
    public void A_single_command_matching_an_allowed_pattern_passes()
    {
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git diff", allowed, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.Allowed, result.Verdict);
    }

    [Theory]
    [InlineData("git diff && git status")]
    [InlineData("git diff && git status && git log")]
    [InlineData("git diff; git status")]
    [InlineData("git diff || git status")]
    public void A_chain_whose_every_segment_matches_an_allowed_pattern_passes(string commandLine)
    {
        // The capability the segment-level check adds over a blanket "any metacharacter denies": a
        // genuinely scoped chain of allowed reads is allowed, not just refused outright.
        string[] allowed = ["git diff*", "git status*", "git log*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, null);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void A_segment_matching_nothing_allowed_denies_naming_that_segment_even_mid_chain()
    {
        string[] allowed = ["git diff*", "git status*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git diff && npm install", allowed, null);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
        Assert.Equal("npm install", result.Segment);
    }

    [Fact]
    public void A_segment_matching_a_denied_pattern_denies_even_when_it_also_matches_an_allowed_one()
    {
        // Deny beats allow (0022, #390): "git push" would itself match a hypothetical "git *" allow,
        // but the standing deny list refuses it regardless of what widens the allow side.
        string[] allowed = ["git diff*", "git push*"];
        string[] denied = ["git push*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand("git diff && git push", allowed, denied);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.DeniedSegment, result.Verdict);
        Assert.Equal("git push", result.Segment);
    }

    [Theory]
    [InlineData("git diff $(whoami)")] // command substitution
    [InlineData("git diff `whoami`")] // backtick substitution
    [InlineData("git diff > out.txt")] // output redirection
    [InlineData("git diff < in.txt")] // input redirection
    [InlineData("(git diff)")] // subshell
    [InlineData("git diff\nrm -rf /")] // embedded newline
    [InlineData("git diff \\")] // trailing backslash
    [InlineData("'git diff")] // unterminated quote
    public void Unparseable_shapes_fail_closed_rather_than_guessing_a_boundary(string commandLine)
    {
        string[] allowed = ["git diff*"];

        var result = ShellCommandPatternMatcher.EvaluateChainedCommand(commandLine, allowed, null);

        Assert.Equal(ShellCommandPatternMatcher.ScopedShellVerdict.Unparseable, result.Verdict);
        Assert.Contains("unparseable under scoped grant", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Segment);
    }
}
