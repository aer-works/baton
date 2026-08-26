namespace Aer.Cli.Tests;

/// <summary>
/// <c>aer dispatch</c>'s argument parsing: the name is positional and <c>--spec</c> is optional at parse
/// time (<see cref="DispatchOptionsParser"/> has the why). These pin the parse-level shapes — the
/// positional name, an optional spec, and a typed error on every malformed invocation.
/// </summary>
public class DispatchOptionsParserTests
{
    [Fact]
    public void Parses_the_name_spec_adapter_room_dir_and_workflow_id()
    {
        var options = DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--adapter", "agy", "--room-dir", "out", "--workflow-id", "wf"]);

        Assert.Equal("review", options.Name);
        Assert.Equal("task.md", options.SpecFilePath);
        Assert.Equal("agy", options.Adapter);
        Assert.Equal("wf", options.WorkflowId);
        Assert.EndsWith("out", options.RoomDirectoryPath);
    }

    [Fact]
    public void A_name_without_a_spec_parses_because_a_template_takes_none()
    {
        // The parser no longer requires --spec: a template dispatch has none, and rejecting it here
        // would refuse a valid invocation before the catalog is even consulted.
        var options = DispatchOptionsParser.Parse(["implement-review"]);

        Assert.Equal("implement-review", options.Name);
        Assert.Null(options.SpecFilePath);
    }

    [Fact]
    public void Parses_the_independent_model_effort_and_workspace_axes()
    {
        // Vendor/model/effort are three independent axes (0017/0033); the CLI exposes each, plus the
        // workspace the worker reads (#1082/#1083).
        var options = DispatchOptionsParser.Parse(
            ["advise", "--spec", "t.md", "--adapter", "claude", "--model", "opus", "--effort", "careful", "--workspace", "."]);

        Assert.Equal("claude", options.Adapter);
        Assert.Equal("opus", options.Model);
        Assert.Equal("careful", options.Effort);
        Assert.Equal(System.IO.Path.GetFullPath("."), options.WorkspaceDirectory);
    }

    [Fact]
    public void Parses_the_output_path_axis()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--output", "custom-report.md"]);
        Assert.Equal(System.IO.Path.GetFullPath("custom-report.md"), options.OutputPath);
    }

    [Fact]
    public void The_new_axis_flags_default_to_null_when_absent()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md"]);
        Assert.Null(options.Model);
        Assert.Null(options.Effort);
        Assert.Null(options.WorkspaceDirectory);
    }

    [Fact]
    public void A_missing_name_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["--spec", "task.md"]));
        Assert.Contains("<name>", ex.Message);
    }

    [Fact]
    public void An_unknown_option_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--nope", "x"]));
    }

    [Fact]
    public void A_second_positional_argument_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "extra", "--spec", "t.md"]));
    }

    [Fact]
    public void The_default_room_directory_is_unique_per_invocation_so_a_redispatch_does_not_resume()
    {
        // A one-shot dispatch must run anew each time; two default directories that collided would
        // make the second invocation resume — and replay — the first's terminal snapshot.
        var first = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).RoomDirectoryPath;
        var second = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).RoomDirectoryPath;
        Assert.NotEqual(first, second);
    }
}
