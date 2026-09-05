using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Vendors;
using Baton.Cli.Tests.TestSupport;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>--verify-cmd</c>'s CLI surface (#1882): where it is accepted, where it is refused, what the
/// refusal says, what the review prompt gains, and which execution the step's cost is attributed to.
/// The step's own spawning is pinned in <c>Baton.Tests</c>'s <c>VerifyStepRunnerTests</c>; nothing
/// here launches a process.
/// </summary>
public class DispatchVerifyStepTests
{
    [Fact]
    public void The_flag_is_repeatable_and_each_value_is_kept_verbatim()
    {
        var options = DispatchOptionsParser.Parse(
        [
            "review", "--spec", "task.md",
            "--verify-cmd", "dotnet build -warnaserror",
            "--verify-cmd", "dotnet test --minimum-expected-tests 1",
            "--verify-timeout", "5",
        ]);

        Assert.Equal(["dotnet build -warnaserror", "dotnet test --minimum-expected-tests 1"], options.VerifyCommands);
        Assert.Equal(TimeSpan.FromMinutes(5), options.VerifyTimeout);
    }

    [Fact]
    public void No_flag_means_no_verify_step_at_all()
    {
        // Nothing runs unless asked (the operator's 2026-09-05 trigger ruling): never implicit, never
        // inferred from the brief.
        var options = DispatchOptionsParser.Parse(["review", "--spec", "task.md"]);

        Assert.Null(options.VerifyCommands);
        Assert.Null(options.VerifyTimeout);
    }

    [Fact]
    public void A_command_outside_the_allowlist_is_refused_at_parse_time_and_named()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--verify-cmd", "dotnet build", "--verify-cmd", "git push origin HEAD"]));

        // The refusal names WHICH of the two commands was wrong -- the good one is not what failed.
        Assert.Contains("git push origin HEAD", ex.Message);
        Assert.DoesNotContain("--verify-cmd dotnet build'", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("1441")]
    public void A_malformed_verify_timeout_is_refused(string value)
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--verify-cmd", "dotnet build", "--verify-timeout", value]));

        Assert.Contains("--verify-timeout", ex.Message);
    }

    [Fact]
    public async Task A_non_review_role_refuses_the_flag_and_says_it_is_not_the_verify_pixi_task_one()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "spec", TestContext.Current.CancellationToken);

            var options = new DispatchOptions(
                "implement", specPath, Path.Combine(testRoot, "room"),
                VerifyCommands: ["dotnet build"]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));

            Assert.Contains("--verify-cmd", ex.Message);
            // The disambiguation the contract asks for, in the message the operator actually sees:
            // implement already has verify_pixi_task, and the two must not be conflated.
            Assert.Contains("verify_pixi_task", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_workflow_template_refuses_the_flag_rather_than_discarding_it()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "implement-review", SpecFilePath: null, Path.Combine(testRoot, "room"),
                VerifyCommands: ["dotnet build"]);

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));

            Assert.Contains("--verify-cmd", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task A_verify_timeout_with_no_verify_cmd_is_refused_because_it_would_bound_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-verify-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var specPath = Path.Combine(testRoot, "spec.md");
            await File.WriteAllTextAsync(specPath, "spec", TestContext.Current.CancellationToken);

            var options = new DispatchOptions(
                "review", specPath, Path.Combine(testRoot, "room"), VerifyTimeout: TimeSpan.FromMinutes(3));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(() => DispatchCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default, TestContext.Current.CancellationToken));

            Assert.Contains("--verify-timeout", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void The_review_prompt_gains_the_results_paragraph_only_when_a_step_ran()
    {
        var review = WorkerRoleCatalog.For("review");

        var withoutStep = RoleDispatch.ToBinding(review, "review the branch");
        var withStep = RoleDispatch.ToBinding(
            review, "review the branch", verifyResultsPath: @"C:\rooms\r1\artifacts\verify-results.md");

        // Polarity in both directions (spec/baton.md §9 states the rule): silence without a step,
        // and with one, the exact path plus the citation requirement.
        Assert.DoesNotContain("verify-results.md", withoutStep.PromptTemplate);
        Assert.Contains(@"C:\rooms\r1\artifacts\verify-results.md", withStep.PromptTemplate);
        Assert.Contains("must cite that file", withStep.PromptTemplate);
        // And it stays ABOVE the outputs block -- context for the review, read before findings form.
        Assert.True(
            withStep.PromptTemplate.IndexOf("verify-results.md", StringComparison.Ordinal)
            < withStep.PromptTemplate.IndexOf("Required outputs:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_verify_step_cost_lands_on_the_first_execution_only_never_on_a_retry_as_well()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"verify-usage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var sidecar = new VerifyStepReport.Sidecar(
                TotalWallClockMs: 125_400, ResultsBytes: 4096, Commands: [new VerifyInstrument("dotnet build", 0, 125_400)]);
            File.WriteAllText(
                Path.Combine(testRoot, VerifyStepReport.SidecarFileName), VerifyStepReport.SerializeSidecar(sidecar));

            var first = new ExecutionId("exec-1");
            var retry = new ExecutionId("exec-2");
            var t0 = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<LogEntry>
            {
                // Deliberately journaled retry-first, so a pass would have to be ordering by TIME
                // rather than by whichever key the dictionary happens to yield first.
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(retry, Pid: 2), t0.AddMinutes(10)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(retry, 0, CoreExitReason.Natural), t0.AddMinutes(11)),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(first, Pid: 1), t0),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(first, 1, CoreExitReason.Natural), t0.AddMinutes(1)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Equal(125_400, usage["exec-1"].VerifyStepMs);
            Assert.Equal(4096, usage["exec-1"].VerifyResultsBytes);
            // The step ran once. Reporting it on the retry too would double it in #1849's ledger.
            Assert.Null(usage["exec-2"].VerifyStepMs);
            Assert.Null(usage["exec-2"].VerifyResultsBytes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public void A_room_with_no_verify_step_reports_neither_telemetry_field()
    {
        // The control arm for the test above: without the sidecar, the same entries must yield no
        // fields at all -- otherwise that test would pass against a projector that always reports them.
        var testRoot = Path.Combine(Path.GetTempPath(), $"verify-usage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var executionId = new ExecutionId("exec-1");
            var t0 = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var entries = new List<LogEntry>
            {
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(executionId, Pid: 1), t0),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionExited(executionId, 0, CoreExitReason.Natural), t0.AddMinutes(1)),
            };

            var usage = ExecutionUsageProjector.BuildByExecutionId(entries, testRoot, WorkerAdapterRegistry.Default);

            Assert.Null(Assert.Single(usage).Value.VerifyStepMs);
            Assert.Null(Assert.Single(usage).Value.VerifyResultsBytes);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
