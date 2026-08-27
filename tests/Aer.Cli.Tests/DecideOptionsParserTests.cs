using Aer.Flow.Domain;

namespace Aer.Cli.Tests;

[Collection(WorkingDirectoryCollection.Name)]
public class DecideOptionsParserTests
{
    [Fact]
    public void A_resume_decision_parses_with_null_target_step_and_supplementary()
    {
        var options = DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--type", "resume", "--bindings", "bindings.json"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
        Assert.Equal("exec-1", options.ExecutionId);
        Assert.Equal(DecisionType.Resume, options.DecisionType);
        Assert.Null(options.TargetStepId);
        Assert.Null(options.SupplementaryExecutionId);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Null(options.WorkflowId);
    }

    [Theory]
    [InlineData("reject", DecisionType.Reject)]
    [InlineData("retry-with-revision", DecisionType.RetryWithRevision)]
    [InlineData("supersede", DecisionType.Supersede)]
    public void Every_decision_type_spelling_parses(string typeText, DecisionType expected)
    {
        var options = DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--type", typeText, "--bindings", "bindings.json"]);

        Assert.Equal(expected, options.DecisionType);
    }

    [Fact]
    public void A_supersede_decision_parses_target_step_and_supplementary()
    {
        var options = DecideOptionsParser.Parse(
            [
                "task", "--execution", "exec-1", "--type", "supersede", "--target-step", "architect",
                "--supplementary", "exec-2", "--bindings", "bindings.json", "--workflow-id", "wf-1",
            ]);

        Assert.Equal(new StepId("architect"), options.TargetStepId);
        Assert.Equal("exec-2", options.SupplementaryExecutionId);
        Assert.Equal("wf-1", options.WorkflowId);
    }

    [Fact]
    public void Options_may_precede_the_positional_room_directory()
    {
        var options = DecideOptionsParser.Parse(
            ["--execution", "exec-1", "--type", "resume", "--bindings", "bindings.json", "task"]);

        Assert.Equal(Path.GetFullPath("task"), options.RoomDirectoryPath);
    }

    [Fact]
    public void An_unknown_decision_type_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--type", "nope", "--bindings", "bindings.json"]));
        Assert.Contains("--type resume", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_room_directory_throws()
    {
        Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["--execution", "exec-1", "--type", "resume", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_execution_option_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "--type", "resume", "--bindings", "bindings.json"]));
        Assert.Contains("aer status", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_type_option_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--bindings", "bindings.json"]));
        Assert.Contains("--type resume", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_bindings_option_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--type", "resume"]));
        Assert.Contains("--bindings", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--type"]));
        Assert.Contains("--type", ex.TryInvocation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "--execution", "exec-1", "--type", "resume", "--bindings", "bindings.json", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => DecideOptionsParser.Parse(
            ["task", "extra", "--execution", "exec-1", "--type", "resume", "--bindings", "bindings.json"]));
    }

    [Fact]
    public void The_suggested_execution_type_and_bindings_flags_round_trip_through_this_parser()
    {
        var options = DecideOptionsParser.Parse(
            ["task", "--execution", "<execution-id>", "--type", "resume", "--bindings", "<path-to-bindings.json>"]);

        Assert.Equal("<execution-id>", options.ExecutionId);
        Assert.Equal(DecisionType.Resume, options.DecisionType);
        Assert.Equal("<path-to-bindings.json>", options.BindingsFilePath);
    }
}
