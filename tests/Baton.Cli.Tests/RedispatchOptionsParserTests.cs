namespace Baton.Cli.Tests;

/// <summary>
/// <c>baton redispatch</c>'s argument parsing (#1441): parse-level shapes only, mirroring
/// <see cref="DispatchOptionsParserTests"/>'s own pin for <c>baton dispatch</c> — what each override
/// flag means once applied is <see cref="RedispatchBindingTests"/>'s job, not this file's.
/// </summary>
public class RedispatchOptionsParserTests
{
    [Fact]
    public void Parses_the_parent_room_dir_and_every_override_flag()
    {
        var options = RedispatchOptionsParser.Parse(
            [
                "parent-room", "--spec", "amended.md", "--adapter", "agy", "--model", "opus",
                "--effort", "careful", "--workspace", ".", "--output", "custom.md", "--timeout", "90",
                "--label", "env-snapshot lane",
            ]);

        Assert.EndsWith("parent-room", options.ParentRoomDirectoryPath);
        Assert.Equal("amended.md", options.SpecFilePath);
        Assert.Equal("agy", options.Adapter);
        Assert.Equal("opus", options.Model);
        Assert.Equal("careful", options.Effort);
        Assert.Equal(Path.GetFullPath("."), options.WorkspaceDirectory);
        Assert.Equal(Path.GetFullPath("custom.md"), options.OutputPath);
        Assert.Equal(TimeSpan.FromMinutes(90), options.Timeout);
        Assert.Equal("env-snapshot lane", options.Label);
    }

    [Fact]
    public void Every_override_flag_defaults_to_null_when_absent()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room"]);

        Assert.Null(options.SpecFilePath);
        Assert.Null(options.Adapter);
        Assert.Null(options.Model);
        Assert.Null(options.Effort);
        Assert.Null(options.WorkspaceDirectory);
        Assert.Null(options.OutputPath);
        Assert.Null(options.Timeout);
        Assert.Null(options.Label);
        Assert.False(options.LabelSpecified);
        Assert.Null(options.Attachments);
    }

    /// <summary>#1576: mirrors <c>DispatchOptionsParserTests.Parses_repeatable_attach_flags_in_order</c>.</summary>
    [Fact]
    public void Parses_repeatable_attach_flags_in_order()
    {
        var options = RedispatchOptionsParser.Parse(
            ["parent-room", "--spec", "amended.md", "--attach", "context.txt", "--attach", "notes.md"]);

        Assert.NotNull(options.Attachments);
        Assert.Equal(new[] { "context.txt", "notes.md" }, options.Attachments);
    }

    [Fact]
    public void Attach_without_value_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--spec", "amended.md", "--attach"]));

        Assert.Contains("--attach", ex.TryInvocation);
    }

    [Fact]
    public void A_label_is_sanitized_the_same_way_dispatchs_own_is()
    {
        // Shared sanitizer (DispatchOptionsParser.SanitizeLabel) -- one cap/trim/newline-fold rule for
        // both verbs, not a second implementation that could drift.
        var raw = new string('x', 90);
        var options = RedispatchOptionsParser.Parse(["parent-room", "--label", raw]);
        Assert.Equal(60, options.Label!.Length);
        Assert.True(options.LabelSpecified);
    }

    [Fact]
    public void A_blank_label_sets_LabelSpecified_true_and_Label_null()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room", "--label", "   "]);
        Assert.Null(options.Label);
        Assert.True(options.LabelSpecified);
    }

    [Fact]
    public void A_missing_room_dir_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["--spec", "amended.md"]));
        Assert.Contains("<room-dir>", ex.Message);
    }

    [Fact]
    public void A_second_positional_argument_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "extra"]));
    }

    [Fact]
    public void An_unknown_option_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "--nope", "x"]));
    }

    [Fact]
    public void An_option_missing_its_value_names_the_option_in_the_Try_line()
    {
        var ex = Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "--spec"]));
        Assert.Contains("--spec", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("nope")]
    public void A_non_positive_or_unparseable_timeout_is_a_typed_argument_error(string rawValue)
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--timeout", rawValue]));
        Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timeout_above_the_24h_ceiling_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--timeout", "1441"]));
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_room_dir_flag_a_fresh_room_directory_is_always_generated()
    {
        Assert.Throws<CliArgumentException>(() => RedispatchOptionsParser.Parse(["parent-room", "--room-dir", "x"]));
    }

    [Fact]
    public void The_new_room_directory_is_unique_per_invocation_and_never_equals_the_parent()
    {
        var first = RedispatchOptionsParser.Parse(["parent-room"]);
        var second = RedispatchOptionsParser.Parse(["parent-room"]);

        Assert.NotEqual(first.RoomDirectoryPath, second.RoomDirectoryPath);
        Assert.NotEqual(first.ParentRoomDirectoryPath, first.RoomDirectoryPath);
    }

    [Fact]
    public void The_new_room_directory_lives_under_BatonPaths_Rooms()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room"]);

        Assert.StartsWith(
            Path.GetFullPath(Baton.Status.BatonPaths.Rooms), options.RoomDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_workstream_is_sanitized_the_same_way_dispatchs_own_is()
    {
        // Shared sanitizer (DispatchOptionsParser.SanitizeWorkstream) -- one grammar/cap rule for both
        // verbs, not a second implementation that could drift.
        var options = RedispatchOptionsParser.Parse(["parent-room", "--workstream", "w1619"]);
        Assert.Equal("w1619", options.Workstream);
        Assert.True(options.WorkstreamSpecified);
    }

    [Fact]
    public void A_blank_workstream_sets_WorkstreamSpecified_true_and_Workstream_null()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room", "--workstream", "   "]);
        Assert.Null(options.Workstream);
        Assert.True(options.WorkstreamSpecified);
    }

    [Fact]
    public void A_path_unsafe_workstream_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(
            () => RedispatchOptionsParser.Parse(["parent-room", "--workstream", "a/b"]));
        Assert.Contains("--workstream", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Workstream_is_absent_and_unspecified_when_never_passed()
    {
        var options = RedispatchOptionsParser.Parse(["parent-room"]);
        Assert.Null(options.Workstream);
        Assert.False(options.WorkstreamSpecified);
    }
}
