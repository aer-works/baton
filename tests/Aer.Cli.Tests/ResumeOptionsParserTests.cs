namespace Aer.Cli.Tests;

/// <summary>
/// #1388 review F10: <c>ResumeOptionsParser</c> shipped with no test file at all -- 7 refusal
/// branches, including the <c>--message</c>/<c>--message-file</c> mutual exclusion, entirely
/// unexercised beyond one path-resolution row. Mirrors <c>SupplyOptionsParserTests</c>' shape, the
/// closest sibling parser.
/// </summary>
[Collection(WorkingDirectoryCollection.Name)]
public class ResumeOptionsParserTests
{
    [Fact]
    public void A_worker_message_and_bindings_option_parse_with_null_workflow_id()
    {
        var options = ResumeOptionsParser.Parse(
            ["task", "--worker", "review", "--message", "continue please", "--bindings", "bindings.json"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Equal("review", options.Worker);
        Assert.Equal("continue please", options.Message);
        Assert.Null(options.MessageFilePath);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Null(options.WorkflowId);
    }

    [Fact]
    public void A_message_file_option_parses_in_place_of_message()
    {
        var options = ResumeOptionsParser.Parse(
            ["task", "--worker", "review", "--message-file", "msg.txt", "--bindings", "bindings.json"]);

        Assert.Null(options.Message);
        Assert.Equal("msg.txt", options.MessageFilePath);
    }

    [Fact]
    public void An_explicit_workflow_id_option_overrides_the_null_default()
    {
        var options = ResumeOptionsParser.Parse(
            [
                "task", "--worker", "review", "--message", "continue", "--bindings", "bindings.json",
                "--workflow-id", "wf-1",
            ]);

        Assert.Equal("wf-1", options.WorkflowId);
    }

    [Fact]
    public void Options_may_precede_the_positional_room_directory()
    {
        var options = ResumeOptionsParser.Parse(
            ["--worker", "review", "--message", "continue", "--bindings", "bindings.json", "task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["--worker", "review", "--message", "continue", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_worker_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["task", "--message", "continue", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void Neither_message_nor_message_file_throws_with_a_Try_line()
    {
        var thrown = Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["task", "--worker", "review", "--bindings", "bindings.json"]));

        Assert.NotNull(thrown.TryInvocation);
    }

    [Fact]
    public void Both_message_and_message_file_together_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            [
                "task", "--worker", "review", "--message", "continue", "--message-file", "msg.txt",
                "--bindings", "bindings.json",
            ]));
    }

    [Fact]
    public void A_missing_bindings_option_throws_with_a_Try_line()
    {
        var thrown = Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["task", "--worker", "review", "--message", "continue"]));

        Assert.NotNull(thrown.TryInvocation);
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["task", "--worker"]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["task", "--worker", "review", "--message", "continue", "--bindings", "bindings.json", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => ResumeOptionsParser.Parse(
            ["task", "extra", "--worker", "review", "--message", "continue", "--bindings", "bindings.json"]));
    }
}
