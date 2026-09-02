namespace Baton.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class RunOptionsParserTests
{
    [Fact]
    public void A_workflow_file_and_bindings_option_parse_with_a_derived_default_room_directory()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json"]);

        Assert.Equal("workflow.json", options.WorkflowFilePath);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Equal(
            Path.Combine(Directory.GetCurrentDirectory(), ".baton", "workflow"),
            options.RoomDirectoryPath);
        Assert.Null(options.WorkflowId);
    }

    [Fact]
    public void An_explicit_room_dir_and_workflow_id_override_the_defaults()
    {
        var options = RunOptionsParser.Parse(
            ["workflow.json", "--bindings", "bindings.json", "--room-dir", "/tmp/task", "--workflow-id", "wf-1"]);

        Assert.Equal(Path.GetFullPath("/tmp/task"), options.RoomDirectoryPath);
        Assert.Equal("wf-1", options.WorkflowId);
    }

    [Fact]
    public void Options_may_precede_the_positional_workflow_file()
    {
        var options = RunOptionsParser.Parse(["--bindings", "bindings.json", "workflow.json"]);

        Assert.Equal("workflow.json", options.WorkflowFilePath);
        Assert.Equal("bindings.json", options.BindingsFilePath);
    }

    [Fact]
    public void A_missing_workflow_file_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["--bindings", "bindings.json"]));
    }

    [Fact]
    public void An_empty_workflow_file_argument_is_refused_like_a_missing_one()
    {
        // "" would reach File.ReadAllTextAsync and throw an untyped ArgumentException from the
        // BCL instead of the typed refusal every other bad path gets (#653 review finding).
        var exception = Assert.Throws<CliArgumentException>(
            () => RunOptionsParser.Parse(["", "--bindings", "bindings.json"]));
        Assert.Contains("Missing required <workflow-file>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_bindings_option_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json"]));
        Assert.Contains("pass --bindings <path-to-bindings.json>", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_suggested_bindings_flag_round_trips_through_this_parser()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "<path-to-bindings.json>"]);

        Assert.Equal("<path-to-bindings.json>", options.BindingsFilePath);
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json", "--bindings"]));
        Assert.Contains("--bindings", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json", "--bindings", "b.json", "--nope"]));
    }

    [Fact]
    public void Echo_worker_flag_defaults_to_false()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json"]);

        Assert.False(options.EchoWorker);
    }

    [Fact]
    public void Echo_worker_flag_parses_when_specified()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json", "--echo-worker"]);

        Assert.True(options.EchoWorker);
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json", "extra.json", "--bindings", "b.json"]));
    }

    [Fact]
    public void Register_flag_defaults_to_false()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json"]);

        Assert.False(options.Register);
    }

    [Fact]
    public void Register_flag_parses_when_specified()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json", "--register"]);

        Assert.True(options.Register);
    }

    [Fact]
    public void Wait_flag_defaults_to_false()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json"]);

        Assert.False(options.Wait);
    }

    [Fact]
    public void Wait_flag_parses_when_specified()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json", "--wait"]);

        Assert.True(options.Wait);
    }

    [Fact]
    public void Wait_timeout_defaults_to_null()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json", "--wait"]);

        Assert.Null(options.WaitTimeout);
    }

    [Fact]
    public void Wait_timeout_parses_as_minutes()
    {
        var options = RunOptionsParser.Parse(
            ["workflow.json", "--bindings", "bindings.json", "--wait", "--wait-timeout", "30"]);

        Assert.Equal(TimeSpan.FromMinutes(30), options.WaitTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nope")]
    [InlineData("12.5")]
    public void A_non_positive_or_unparseable_wait_timeout_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json", "--wait", "--wait-timeout", rawValue]));
        Assert.Contains("--wait-timeout", ex.Message, StringComparison.Ordinal);
    }
}
