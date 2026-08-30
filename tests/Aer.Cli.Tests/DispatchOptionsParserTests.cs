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
        Assert.Null(options.Timeout);
    }

    [Fact]
    public void Parses_the_timeout_override_as_minutes()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", "90"]);
        Assert.Equal(TimeSpan.FromMinutes(90), options.Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nope")]
    [InlineData("12.5")]
    public void A_non_positive_or_unparseable_timeout_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", rawValue]));
        Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_above_the_24h_ceiling_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", "1441"]));
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_at_exactly_the_24h_ceiling_is_accepted()
    {
        var options = DispatchOptionsParser.Parse(["advise", "--spec", "t.md", "--timeout", "1440"]);
        Assert.Equal(TimeSpan.FromHours(24), options.Timeout);
    }

    [Fact]
    public void A_missing_name_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["--spec", "task.md"]));
        Assert.Contains("<name>", ex.Message);
        Assert.Equal("run 'aer templates' to see available role and template names.", ex.TryInvocation);
    }

    [Fact]
    public void An_option_missing_its_value_names_the_option_in_the_Try_line()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "--spec"]));
        Assert.NotNull(ex.TryInvocation);
        Assert.Contains("--spec", ex.TryInvocation, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1382 F10.1: DispatchCommand's missing-<c>--spec</c> refusal suggests
    /// <c>aer dispatch &lt;role&gt; --spec &lt;spec-file&gt;</c> -- feed that shape back through the
    /// real parser rather than only pinning that the string was set (this is what would have caught
    /// F5's stale worktree suggestion).
    /// </summary>
    [Fact]
    public void The_suggested_missing_spec_invocation_round_trips_through_this_parser()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "<spec-file>"]);

        Assert.Equal("review", options.Name);
        Assert.Equal("<spec-file>", options.SpecFilePath);
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

    /// <summary>
    /// R2 (#1354/#1380, finding 2) -- see the parser's own comment for why the default must sit outside
    /// any workspace a dispatch might audit. <c>Aer.Adapters.AerPaths.Rooms</c> is the one place that
    /// root is resolved from (honouring <c>AER_HOME</c>); this pins that the default is built from it,
    /// not re-derives it.
    /// </summary>
    [Fact]
    public void The_default_room_directory_lives_under_AerPaths_Rooms_not_the_current_directory()
    {
        var options = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]);

        Assert.StartsWith(
            Path.GetFullPath(Aer.Adapters.AerPaths.Rooms), options.RoomDirectoryPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.GetFullPath(Directory.GetCurrentDirectory()), options.RoomDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }
}
