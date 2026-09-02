namespace Baton.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class ResolveOptionsParserTests
{
    [Fact]
    public void An_accept_capture_parses_with_no_execution_and_no_reason()
    {
        var options = ResolveOptionsParser.Parse(["task", "--accept-capture"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Null(options.ExecutionId);
        Assert.True(options.Accept);
        Assert.Null(options.Reason);
    }

    [Fact]
    public void An_accept_capture_may_also_carry_a_reason()
    {
        var options = ResolveOptionsParser.Parse(["task", "--accept-capture", "--reason", "honest response"]);

        Assert.True(options.Accept);
        Assert.Equal("honest response", options.Reason);
    }

    [Fact]
    public void A_reject_with_reason_and_an_explicit_execution_parses()
    {
        var options = ResolveOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--reject", "--reason", "not an honest advice.md"]);

        Assert.Equal("exec-1", options.ExecutionId);
        Assert.False(options.Accept);
        Assert.Equal("not an honest advice.md", options.Reason);
    }

    [Fact]
    public void Options_may_precede_the_positional_room_directory()
    {
        var options = ResolveOptionsParser.Parse(["--accept-capture", "task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(["--accept-capture"]));
    }

    [Fact]
    public void Passing_both_accept_capture_and_reject_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(
            ["task", "--accept-capture", "--reject", "--reason", "x"]));
    }

    [Fact]
    public void Passing_reject_then_accept_capture_also_throws()
    {
        // Polarity of the order above: the guard must fire regardless of which flag arrives first.
        Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(
            ["task", "--reject", "--reason", "x", "--accept-capture"]));
    }

    [Fact]
    public void Neither_accept_capture_nor_reject_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(["task"]));
        Assert.Contains("--accept-capture", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reject_without_reason_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(["task", "--reject"]));
        Assert.Contains("--reason", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reject_with_a_whitespace_only_reason_still_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(
            ["task", "--reject", "--reason", "   "]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(
            ["task", "--accept-capture", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(
            ["task", "extra", "--accept-capture"]));
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => ResolveOptionsParser.Parse(
            ["task", "--accept-capture", "--execution"]));
        Assert.Contains("--execution", ex.TryInvocation, StringComparison.Ordinal);
    }
}
