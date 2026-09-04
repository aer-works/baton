using Baton.Status;

namespace Baton.Cli.Tests;

public sealed class DeliverOptionsParserTests
{
    [Fact]
    public void Parse_WithFileOnly_ResolvesDefaultConductorRoom()
    {
        var options = DeliverOptionsParser.Parse(["my-doc.md"]);

        Assert.Equal("my-doc.md", options.SourceFilePath);
        Assert.Null(options.Title);
        Assert.Equal(Path.Combine(BatonPaths.Rooms, "conductor"), options.RoomDirectoryPath);
    }

    [Fact]
    public void Parse_WithTitleAndRoom_ResolvesAllOptions()
    {
        var customRoom = Path.Combine(Path.GetTempPath(), "custom-conductor-room");
        var options = DeliverOptionsParser.Parse(["my-doc.md", "--title", "My Title", "--room", customRoom]);

        Assert.Equal("my-doc.md", options.SourceFilePath);
        Assert.Equal("My Title", options.Title);
        Assert.Equal(Path.GetFullPath(customRoom), options.RoomDirectoryPath);
    }

    [Fact]
    public void Parse_WithRoomDirAlias_Resolves()
    {
        var customRoom = Path.Combine(Path.GetTempPath(), "custom-conductor-room-2");
        var options = DeliverOptionsParser.Parse(["my-doc.md", "--room-dir", customRoom]);

        Assert.Equal(Path.GetFullPath(customRoom), options.RoomDirectoryPath);
    }

    [Fact]
    public void Parse_MissingFile_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DeliverOptionsParser.Parse([]));
        Assert.Contains("Missing required <file> argument", ex.Message);
    }

    [Fact]
    public void Parse_UnknownOption_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DeliverOptionsParser.Parse(["my-doc.md", "--invalid-flag"]));
        Assert.Contains("Unknown option '--invalid-flag'", ex.Message);
    }

    [Fact]
    public void Parse_ExtraPositional_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DeliverOptionsParser.Parse(["my-doc.md", "extra.md"]));
        Assert.Contains("Unexpected extra argument 'extra.md'", ex.Message);
    }

    [Fact]
    public void Parse_MissingOptionValue_ThrowsCliArgumentException()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DeliverOptionsParser.Parse(["my-doc.md", "--title"]));
        Assert.Contains("Option '--title' requires a value", ex.Message);
    }
}
