namespace Baton.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class KeepOptionsParserTests
{
    [Fact]
    public void A_bare_room_directory_parses()
    {
        var options = KeepOptionsParser.Parse(["task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => KeepOptionsParser.Parse([]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => KeepOptionsParser.Parse(["task", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => KeepOptionsParser.Parse(["task", "extra"]));
    }
}
