namespace Baton.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class SupplyOptionsParserTests
{
    [Fact]
    public void A_worker_output_file_and_bindings_option_parse_with_null_workflow_id()
    {
        var options = SupplyOptionsParser.Parse(
            ["task", "--worker", "human", "--output", "revision.md", "--file", "revised.md", "--bindings", "bindings.json"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Equal("human", options.Worker);
        Assert.Equal("revision.md", options.OutputName);
        Assert.Equal("revised.md", options.SourceFilePath);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Null(options.WorkflowId);
    }

    [Fact]
    public void An_explicit_workflow_id_option_overrides_the_null_default()
    {
        var options = SupplyOptionsParser.Parse(
            [
                "task", "--worker", "human", "--output", "revision.md", "--file", "revised.md",
                "--bindings", "bindings.json", "--workflow-id", "wf-1",
            ]);

        Assert.Equal("wf-1", options.WorkflowId);
    }

    [Fact]
    public void Options_may_precede_the_positional_room_directory()
    {
        var options = SupplyOptionsParser.Parse(
            ["--worker", "human", "--output", "revision.md", "--file", "revised.md", "--bindings", "bindings.json", "task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["--worker", "human", "--output", "revision.md", "--file", "revised.md", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_worker_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "--output", "revision.md", "--file", "revised.md", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_output_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "--worker", "human", "--file", "revised.md", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_file_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "--worker", "human", "--output", "revision.md", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_bindings_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "--worker", "human", "--output", "revision.md", "--file", "revised.md"]));
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "--worker", "human", "--output"]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "--worker", "human", "--output", "revision.md", "--file", "revised.md", "--bindings", "bindings.json", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => SupplyOptionsParser.Parse(
            ["task", "extra", "--worker", "human", "--output", "revision.md", "--file", "revised.md", "--bindings", "bindings.json"]));
    }
}
