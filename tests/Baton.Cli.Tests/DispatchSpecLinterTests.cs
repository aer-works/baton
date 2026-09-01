using Baton.Vendors;

namespace Baton.Cli.Tests;

[Collection(SerializedEnvironmentCollection.Name)]
public class DispatchSpecLinterTests
{
    [Theory]
    [InlineData("gh issue view 1500", "gh")]
    [InlineData("`gh pr view`", "gh")]
    [InlineData("$ gh repo view", "gh")]
    [InlineData("git diff HEAD~1", "git")]
    [InlineData("`git log`", "git")]
    [InlineData("Please run \"git status\" now", "git")]
    [InlineData("Please run 'dotnet test' now", "dotnet")]
    [InlineData("dotnet test", "dotnet")]
    [InlineData("(pixi run gates)", "pixi")]
    [InlineData("pixi run gates", "pixi")]
    [InlineData("curl https://api.example.com", "curl")]
    [InlineData("Please run the test suite", "run the")]
    [InlineData("Execute the migration script", "execute")]
    [InlineData("See https://github.com/philipreese/baton", "url")]
    [InlineData("Visit http://localhost:8080 for status", "url")]
    public void Heuristics_match_expected_instruction_patterns(string line, string heuristicName)
    {
        var matched = DispatchSpecLinter.Heuristics
            .Where(h => h.Name == heuristicName && h.Matches(line))
            .ToList();

        Assert.NotEmpty(matched);
    }

    [Theory]
    [InlineData("Weigh the options for database schema migrations.")]
    [InlineData("Consider relational vs document store.")]
    [InlineData("digital logic and binary analysis")]
    [InlineData("night watch monitoring")]
    [InlineData("pixie dust")]
    [InlineData("straightforward architecture")]
    public void Heuristics_do_not_falsely_match_plain_prose(string line)
    {
        var matched = DispatchSpecLinter.Heuristics
            .Where(h => h.Matches(line))
            .ToList();

        Assert.Empty(matched);
    }

    [Fact]
    public void Advise_role_with_gh_command_warns_on_both_shell_and_network()
    {
        var spec = "Line 1: Analyze requirements\nLine 2: gh issue view 1500\nLine 3: Provide advice";
        var adviseGrant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false);

        var warnings = DispatchSpecLinter.Lint(spec, adviseGrant, "advise");

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(2, w.LineNumber));
        Assert.Contains(warnings, w => w.MissingCategory == GrantCategory.Shell);
        Assert.Contains(warnings, w => w.MissingCategory == GrantCategory.Network);

        var shellWarning = warnings.First(w => w.MissingCategory == GrantCategory.Shell);
        Assert.Contains("Spec line 2", shellWarning.Format());
        Assert.Contains("shell", shellWarning.Format());
        Assert.Contains("advise", shellWarning.Format());
    }

    [Fact]
    public void Unscoped_shell_grant_with_git_diff_produces_no_shell_warning()
    {
        var spec = "git diff HEAD~1\nReview the diff for regressions.";
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: false);

        var warnings = DispatchSpecLinter.Lint(spec, grant, "review");

        Assert.Empty(warnings);
    }

    [Fact]
    public void Unscoped_shell_grant_with_url_warns_on_network_missing()
    {
        var spec = "Check https://example.com/spec.md and review findings.";
        var grant = new PermissionGrant(
            ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: false);

        var warnings = DispatchSpecLinter.Lint(spec, grant, "review");

        var warning = Assert.Single(warnings);
        Assert.Equal(1, warning.LineNumber);
        Assert.Equal(GrantCategory.Network, warning.MissingCategory);
        Assert.Contains("no-network grant", warning.Format());
    }

    // These two pin the read-only-patterned-shell exemption against the REAL catalog `review` grant,
    // not a hand-rolled double that cannot reproduce the shape which caused it (#1500 second-reader) --
    // a fabricated grant is what let the original defect through. Why that exemption exists is stated
    // once, beside the `readOnlyPatternedShell` check in DispatchSpecLinter; not restated here.

    [Fact]
    public void Real_review_role_grant_does_not_warn_on_its_own_allowlisted_gh_command()
    {
        var reviewRole = WorkerRoleCatalog.For("review");
        var spec = "gh issue view 1500\nSummarize the linked context.";

        var warnings = DispatchSpecLinter.Lint(spec, reviewRole.Grant, "review");

        Assert.Empty(warnings);
    }

    [Fact]
    public void Real_review_role_grant_still_has_no_shell_at_all_flagged_correctly()
    {
        var reviewRole = WorkerRoleCatalog.For("review");
        Assert.True(reviewRole.Grant is { RunShellCommands: true });

        // Sanity check the fixture itself carries the read-only, patterned shape the fix targets —
        // if a future catalog edit dropped either flag, this fails loudly instead of the two tests
        // above silently testing nothing.
        Assert.True(reviewRole.Grant is { ShellCommandsAreReadOnly: true, ShellCommandPatterns.Count: > 0 });
        Assert.False(reviewRole.Grant.NetworkAccess);
    }

    [Fact]
    public void Implement_role_with_dotnet_and_url_produces_no_warnings()
    {
        var spec = "dotnet build\nCheck https://github.com for references.";
        var implementGrant = new PermissionGrant(
            ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);

        var warnings = DispatchSpecLinter.Lint(spec, implementGrant, "implement");

        Assert.Empty(warnings);
    }

    [Fact]
    public void Empty_or_whitespace_spec_produces_no_warnings()
    {
        Assert.Empty(DispatchSpecLinter.Lint("", null, "advise"));
        Assert.Empty(DispatchSpecLinter.Lint("   \n\t\n  ", null, "advise"));
    }
}
