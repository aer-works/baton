namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton rooms prune</c>'s argument parser — <c>--dry-run</c> and <c>--yes</c> are mutually
/// exclusive (F1 of the #1667 review: passing both used to parse cleanly and silently perform the
/// real deletion, discarding the <c>--dry-run</c> the operator typed).
/// </summary>
public sealed class RoomsPruneOptionsParserTests
{
    [Fact]
    public void Parse_DryRunAndYesTogether_Throws()
    {
        var exception = Assert.Throws<CliArgumentException>(
            () => RoomsPruneOptionsParser.Parse(["--terminal", "--dry-run", "--yes"]));

        Assert.Contains("--dry-run and --yes contradict each other", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DryRunAlone_StillParsesAsDryRun()
    {
        var options = RoomsPruneOptionsParser.Parse(["--terminal", "--dry-run"]);

        Assert.True(options.DryRun);
        Assert.False(options.Yes);
    }
}
