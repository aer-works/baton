using Baton.Vendors;
using Baton.Flow.Domain;

namespace Baton.Cli.SmokeTests;

/// <summary>
/// #649's completion gate: a worker whose grant withholds writes produces its declared output and
/// does not touch its workspace. Three mechanisms have to compose for that — the write tools stay
/// pre-approved on <c>--allowedTools</c>, they are absent from <c>--disallowedTools</c>, and AER's
/// <c>PreToolUse</c> hook confines them to <c>BATON_OUTPUT_DIR</c>. Unit tests cover each part; only a
/// live run covers the composition, because the way it fails is the vendor refusing a tool call while
/// AER sees nothing but a missing artifact.
/// </summary>
/// <remarks>
/// Excluded from <c>AerFlow.slnx</c> and default CI like its siblings. <c>pixi run smoke-readonly</c>;
/// runbook at <c>docs/runbooks/live-claude-smoke.md</c>.
/// </remarks>
public class LiveReadOnlyReviewerSmokeTest
{
    [Fact]
    public async Task A_withheld_write_reaches_the_outbox_and_not_the_workspace()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var testRoot = Path.Combine(Path.GetTempPath(), $"readonly-smoke-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var workspace = Directory.CreateDirectory(Path.Combine(testRoot, "workspace")).FullName;
        try
        {
            // Written here rather than shipped, because WorkingDirectory must be an absolute path on
            // this machine — and a relative one silently makes AER and the worker disagree about
            // BATON_OUTPUT_DIR (#668).
            var bindingsPath = Path.Combine(testRoot, "bindings.json");
            var bindings = System.Text.Json.Nodes.JsonNode.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(fixtures, "readonly-reviewer-bindings.json"),
                    TestContext.Current.CancellationToken))!;
            bindings["reviewer"]!["WorkingDirectory"] = workspace;
            await File.WriteAllTextAsync(
                bindingsPath, bindings.ToJsonString(), TestContext.Current.CancellationToken);

            var options = new RunOptions(
                Path.Combine(fixtures, "readonly-reviewer-workflow.json"), bindingsPath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(
                options, WorkerAdapterRegistry.Default,
                cancellationToken: TestContext.Current.CancellationToken)).State;

            var step = Assert.Single(finalState.Steps);

            // All three together, because two of three is a different failure each way: an empty
            // workspace with no report is #629's pay-then-fail, and a report plus a leaked file is
            // the grant not being enforced at all.
            Assert.Equal(StepStatus.Succeeded, step.Status);

            var outbox = Path.Combine(roomDirectory, "artifacts", $"execution_{step.LatestExecutionId}");
            var report = Path.Combine(outbox, "review.md");
            Assert.True(File.Exists(report), $"the withheld write never reached its outbox at '{report}'.");
            Assert.False(
                string.IsNullOrWhiteSpace(
                    await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken)));

            Assert.Empty(Directory.GetFileSystemEntries(workspace));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
