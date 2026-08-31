namespace Baton.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class UnkeepOptionsParserTests
{
    [Fact]
    public void A_bare_room_directory_parses()
    {
        var options = UnkeepOptionsParser.Parse(["task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => UnkeepOptionsParser.Parse([]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => UnkeepOptionsParser.Parse(["task", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => UnkeepOptionsParser.Parse(["task", "extra"]));
    }
}
