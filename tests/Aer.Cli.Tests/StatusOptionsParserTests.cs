namespace Aer.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class StatusOptionsParserTests
{
    [Fact]
    public void A_bare_room_directory_parses_with_follow_false()
    {
        var options = StatusOptionsParser.Parse(["task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.False(options.Follow);
    }

    [Fact]
    public void The_follow_flag_parses_to_true()
    {
        var options = StatusOptionsParser.Parse(["task", "--follow"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.True(options.Follow);
    }

    [Fact]
    public void The_follow_flag_may_precede_the_positional_room_directory()
    {
        var options = StatusOptionsParser.Parse(["--follow", "task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.True(options.Follow);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => StatusOptionsParser.Parse(["--follow"]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => StatusOptionsParser.Parse(["task", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => StatusOptionsParser.Parse(["task", "extra"]));
    }

    [Fact]
    public void The_json_flag_parses_to_true()
    {
        var options = StatusOptionsParser.Parse(["task", "--json"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.True(options.Json);
    }

    [Fact]
    public void Follow_and_json_together_are_refused()
    {
        var exception = Assert.Throws<CliArgumentException>(() => StatusOptionsParser.Parse(["task", "--follow", "--json"]));
        Assert.Contains("incompatible", exception.Message, StringComparison.Ordinal);
    }
}
