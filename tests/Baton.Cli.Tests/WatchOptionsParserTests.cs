namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton watch</c>'s argument parser — three shapes (spec/baton.md §2):
/// <c>&lt;room-dir&gt; --notify &lt;target&gt;</c>, <c>--list</c>, and <c>--clear-fired</c>.
/// </summary>
public sealed class WatchOptionsParserTests
{
    [Fact]
    public void Parse_RoomDirAndNotify_ParsesAsRegister()
    {
        var options = WatchOptionsParser.Parse(["room1", "--notify", "https://example.invalid/hook"]);

        Assert.Equal(WatchMode.Register, options.Mode);
        Assert.Equal(Path.GetFullPath("room1"), options.RoomDirectoryPath);
        Assert.Equal("https://example.invalid/hook", options.NotifyTarget);
    }

    [Fact]
    public void Parse_List_ParsesAsListWithNoOtherFields()
    {
        var options = WatchOptionsParser.Parse(["--list"]);

        Assert.Equal(WatchMode.List, options.Mode);
        Assert.Null(options.RoomDirectoryPath);
        Assert.Null(options.NotifyTarget);
    }

    [Fact]
    public void Parse_ClearFired_ParsesAsClearFiredWithNoOtherFields()
    {
        var options = WatchOptionsParser.Parse(["--clear-fired"]);

        Assert.Equal(WatchMode.ClearFired, options.Mode);
        Assert.Null(options.RoomDirectoryPath);
        Assert.Null(options.NotifyTarget);
    }

    [Fact]
    public void Parse_MissingRoomDir_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => WatchOptionsParser.Parse(["--notify", "cmd"]));

        Assert.Contains("Missing required <room-dir>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingNotify_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => WatchOptionsParser.Parse(["room1"]));

        Assert.Contains("Missing required --notify", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownOption_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => WatchOptionsParser.Parse(["room1", "--notify", "cmd", "--bogus"]));

        Assert.Contains("Unknown option '--bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExtraPositionalArgument_Throws()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => WatchOptionsParser.Parse(["room1", "room2", "--notify", "cmd"]));

        Assert.Contains("Unexpected extra argument 'room2'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NotifyValueLooksLikeACommandLine_TakenVerbatim()
    {
        var options = WatchOptionsParser.Parse(["room1", "--notify", "curl -X POST https://ntfy.sh/mytopic"]);

        Assert.Equal("curl -X POST https://ntfy.sh/mytopic", options.NotifyTarget);
    }
}
