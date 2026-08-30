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
}
