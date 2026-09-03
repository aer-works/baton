using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton trust</c>'s argument parser (#1166) — three shapes: <c>&lt;project-path&gt; --ceiling
/// &lt;categories&gt;</c>, <c>--list</c>, and <c>&lt;project-path&gt; --revoke</c>.
/// </summary>
public sealed class TrustOptionsParserTests
{
    [Fact]
    public void Parse_ProjectPathAndCeilingAll_ParsesAsRegisterUnrestricted()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "all"]);

        Assert.Equal(TrustMode.Register, options.Mode);
        Assert.Equal("/repo", options.ProjectPath);
        Assert.Equal(ProjectCeiling.Unrestricted, options.Ceiling);
    }

    [Fact]
    public void Parse_CeilingNone_ParsesAsEveryCategoryClosed()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "none"]);

        Assert.Equal(new ProjectCeiling(false, false, false, false), options.Ceiling);
    }

    [Fact]
    public void Parse_CeilingCommaSeparatedCategories_ParsesOnlyThoseCategoriesOpen()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--ceiling", "ReadFiles,WriteFiles"]);

        Assert.Equal(new ProjectCeiling(true, true, false, false), options.Ceiling);
    }

    [Fact]
    public void Parse_List_ParsesAsListWithNoOtherFields()
    {
        var options = TrustOptionsParser.Parse(["--list"]);

        Assert.Equal(TrustMode.List, options.Mode);
        Assert.Null(options.ProjectPath);
        Assert.Null(options.Ceiling);
    }

    [Fact]
    public void Parse_ProjectPathAndRevoke_ParsesAsRevokeWithNoCeiling()
    {
        var options = TrustOptionsParser.Parse(["/repo", "--revoke"]);

        Assert.Equal(TrustMode.Revoke, options.Mode);
        Assert.Equal("/repo", options.ProjectPath);
        Assert.Null(options.Ceiling);
    }

    [Fact]
    public void Parse_MissingProjectPath_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["--ceiling", "all"]));

        Assert.Contains("Missing required <project-path>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingCeilingAndRevoke_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["/repo"]));

        Assert.Contains("Missing required '--ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CeilingAndRevokeCombined_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "--ceiling", "all", "--revoke"]));

        Assert.Contains("cannot be combined", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownCeilingToken_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "--ceiling", "ReadFiles,Bogus"]));

        Assert.Contains("Unknown ceiling category 'Bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "--ceiling", "all", "--bogus"]));

        Assert.Contains("Unknown option '--bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExtraPositionalArgument_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => TrustOptionsParser.Parse(["/repo", "/other", "--ceiling", "all"]));

        Assert.Contains("Unexpected extra argument '/other'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ListCombinedWithOtherArguments_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => TrustOptionsParser.Parse(["/repo", "--list"]));

        Assert.Contains("'--list' cannot be combined", ex.Message, StringComparison.Ordinal);
    }
}
