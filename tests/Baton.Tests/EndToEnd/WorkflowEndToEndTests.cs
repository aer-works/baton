using Baton.Tests.TestSupport;
using System.Diagnostics;
using Baton.Concurrency;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Templates;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.EndToEnd;

/// <summary>
/// M7's completion gate (issue #14): loads a real <c>WorkflowDefinition</c> template from a
/// fixture file — not one constructed in-memory — binds it, and runs the full linear happy path
/// through the single mutation surface, on a real filesystem, with the concurrency guard
/// engaged for the whole run. No mocking of Baton.Core itself.
/// </summary>
public class WorkflowEndToEndTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId D = new("d");

    private static readonly StepId Flaky = new("flaky");
    private static readonly StepId Downstream = new("downstream");
    private static readonly StepId Reviewer = new("reviewer");
    private static readonly StepId Permanent = new("permanent");

    [Fact]
    public async Task A_three_step_linear_workflow_loaded_from_a_fixture_file_runs_to_completion()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "the-plan"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
                ["publisher"] = new WorkerBinding.Process(
                    new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                    CopyFirstInputTo("summary"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-e2e"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(3, finalState.Steps.Count);
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Architect], "plan", "the-plan");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Critic], "review", "the-plan");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Publisher], "summary", "the-plan");

            // The guard was held for the whole run above; its lock file is left on disk once
            // released, proving the run actually went through it and that release doesn't erase
            // the file (a sentinel-file scheme would instead delete it to signal "unlocked").
            Assert.True(File.Exists(Path.Combine(roomDirectory, "flow.lock")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_second_concurrent_run_against_the_same_room_directory_is_rejected()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            using var heldByAnotherInstance = ConcurrencyGuard.Acquire(roomDirectory);

            await using var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl"));
            var reader = new FlowEventLogReader(Path.Combine(roomDirectory, "flow.jsonl"));
            var dispatcher = new CoreDispatcher(writer);

            await Assert.ThrowsAsync<WorkflowLockedException>(() => MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-e2e-locked"),
                roomDirectory,
                snapshot,
                new Dictionary<string, WorkerBinding>(),
                Path.Combine(roomDirectory, "artifacts"),
                reader,
                writer,
                dispatcher, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_diamond_dag_workflow_loaded_from_a_fixture_file_runs_all_four_steps_to_completion()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "diamond-dag-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["a"] = new WorkerBinding.Process(
                    new WorkerContract("a", [], [new ProducedOutput("out_a")], []),
                    WriteFile("out_a", "a-out"),
                    TimeSpan.FromSeconds(30)),
                ["b"] = new WorkerBinding.Process(
                    new WorkerContract("b", ["out_a"], [new ProducedOutput("out_b")], []),
                    CopyFirstInputTo("out_b"),
                    TimeSpan.FromSeconds(30)),
                ["c"] = new WorkerBinding.Process(
                    new WorkerContract("c", ["out_a"], [new ProducedOutput("out_c")], []),
                    CopyFirstInputTo("out_c"),
                    TimeSpan.FromSeconds(30)),
                ["d"] = new WorkerBinding.Process(
                    new WorkerContract("d", ["out_b", "out_c"], [new ProducedOutput("out_d")], []),
                    ConcatBothInputsTo("out_d"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-diamond"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(4, finalState.Steps.Count);
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);

            // D's UpstreamExecutionIds reference B's and C's successful executions.
            Assert.Equal(stepStateById[B].LatestExecutionId, stepStateById[D].UpstreamExecutionIds[B]);
            Assert.Equal(stepStateById[C].LatestExecutionId, stepStateById[D].UpstreamExecutionIds[C]);

            await AssertOutputExistsAsync(artifactsRoot, stepStateById[A], "out_a", "a-out");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[B], "out_b", "a-out");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[C], "out_c", "a-out");
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[D], "out_d");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_mechanically_flaky_worker_retries_and_downstream_uses_the_successful_attempts_output()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flaky-retry-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerFilePath = Path.Combine(roomDirectory, "flaky.marker");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["flaky"] = new WorkerBinding.Process(
                    new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                    FailOnFirstAttemptThenSucceed(markerFilePath, "result", "second-attempt-result"),
                    TimeSpan.FromSeconds(30)),
                ["downstream"] = new WorkerBinding.Process(
                    new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                    CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-flaky"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            Assert.Equal(StepStatus.Succeeded, stepStateById[Flaky].Status);
            Assert.Equal(StepStatus.Succeeded, stepStateById[Downstream].Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var flakyExecutionIds = GetAcceptedExecutionIds(events, Flaky);

            // The history shape: two attempts, distinct ExecutionIds, first failed then succeeded.
            Assert.Equal(2, flakyExecutionIds.Count);
            Assert.NotEqual(flakyExecutionIds[0], flakyExecutionIds[1]);
            Assert.Equal(StepStatus.Failed, GetTerminalStatus(events, flakyExecutionIds[0]));
            Assert.Equal(StepStatus.Succeeded, GetTerminalStatus(events, flakyExecutionIds[1]));

            // History is never cleaned up: both attempts' artifact directories persist.
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{flakyExecutionIds[0]}")));
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{flakyExecutionIds[1]}")));

            // Downstream ran against the successful attempt's output, not the failed one's.
            await AssertOutputExistsAsync(artifactsRoot, stepStateById[Downstream], "final", "second-attempt-result");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_worker_using_bounded_self_iteration_retries_until_its_output_condition_is_satisfied()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "self-iteration-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        var markerFilePath = Path.Combine(roomDirectory, "reviewer.marker");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["reviewer"] = new WorkerBinding.Process(
                    new WorkerContract(
                        "reviewer",
                        [],
                        [new ProducedOutput("verdict", new OutputCondition("/status", new JsonScalar.String("approved")))],
                        []),
                    WriteVerdictNeedsRevisionThenApproved(scriptDirectory, markerFilePath, "verdict"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-self-iteration"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var reviewerState = finalState.Steps.Single(s => s.StepId == Reviewer);
            Assert.Equal(StepStatus.Succeeded, reviewerState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var executionIds = GetAcceptedExecutionIds(events, Reviewer);
            Assert.Equal(2, executionIds.Count);
            Assert.Equal(StepStatus.Failed, GetTerminalStatus(events, executionIds[0]));
            Assert.Equal(StepStatus.Succeeded, GetTerminalStatus(events, executionIds[1]));

            // Exit 0 with an unsatisfied OutputCondition classifies ExecutionFailed with no
            // self-reported classification — only the condition, not the worker, drove the retry.
            var firstAttemptOutcome = events.OfType<FlowEvent.ExecutionFailed>().Single(e => e.ExecutionId == executionIds[0]);
            Assert.Null(firstAttemptOutcome.FailureClassification);
            Assert.NotNull(firstAttemptOutcome.Reason);
            Assert.Contains("verdict", firstAttemptOutcome.Reason);
        }

        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_worker_reporting_a_permanent_failure_classification_is_not_retried_despite_remaining_budget()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "permanent-failure-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["permanent"] = new WorkerBinding.Process(
                    new WorkerContract("permanent", [], [new ProducedOutput("result")], ["result-metadata.json"]),
                    FailPermanently(scriptDirectory, "result-metadata.json"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-permanent"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var permanentState = finalState.Steps.Single(s => s.StepId == Permanent);
            Assert.Equal(StepStatus.Failed, permanentState.Status);
            Assert.Equal(FailureClassification.Permanent, permanentState.LatestFailureClassification);

            // Exactly one attempt despite MaxAttempts: 3 remaining — the Permanent short-circuit.
            var executionIds = GetAcceptedExecutionIds(await reader.ReadAllAsync(TestContext.Current.CancellationToken), Permanent);
            Assert.Single(executionIds);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task An_always_failing_worker_is_retried_exactly_up_to_MaxAttempts_then_stays_terminally_failed()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "exhaustion-workflow.json");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["flaky"] = new WorkerBinding.Process(
                    new WorkerContract("flaky", [], [new ProducedOutput("result")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
                ["downstream"] = new WorkerBinding.Process(
                    new WorkerContract("downstream", ["result"], [new ProducedOutput("final")], []),
                    CopyFirstInputTo("final"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-exhaustion"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepStateById = finalState.Steps.ToDictionary(s => s.StepId);
            Assert.Equal(StepStatus.Failed, stepStateById[Flaky].Status);

            // Downstream never dispatched — the workflow reached a fixed point instead.
            Assert.Equal(StepStatus.Pending, stepStateById[Downstream].Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var executionIds = GetAcceptedExecutionIds(events, Flaky);
            Assert.Equal(2, executionIds.Count);
            Assert.All(executionIds, id => Assert.Equal(StepStatus.Failed, GetTerminalStatus(events, id)));
            Assert.Empty(GetAcceptedExecutionIds(events, Downstream));

            // Both attempts' artifact directories persist — history is never cleaned up.
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionIds[0]}")));
            Assert.True(Directory.Exists(Path.Combine(artifactsRoot, $"execution_{executionIds[1]}")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Re_reading_the_full_event_log_every_scheduling_round_scales_linearly_not_worse()
    {
        // The "manifest cache if scale demands" question, measured as a SHAPE rather than a
        // wall-clock budget. The M8 Phase 3 reactive loop re-reads the whole flow.jsonl every
        // scheduling round instead of tailing it, so what actually decides whether a manifest cache
        // is warranted is whether that re-read's cost stays proportional to event count
        // (linear — no cache needed) or grows faster (super-linear — it is).
        //
        // The old form asserted a fixed 50ms/round budget, which silently encoded "this machine,
        // mostly idle" as a precondition it never stated and could not check: on a box also building
        // a lane's test suite it measured 55ms and red a change that touched nothing on the path
        // (#861). Comparing the cost at two log sizes removes that — a machine 15x slower inflates
        // BOTH measurements equally, so their ratio is load-invariant while an O(n²) regression still
        // moves it. See docs/milestone-history.md (M8, "Manifest cache deferred") for the shape this
        // is drawn around.
        const int small = 500;
        const int large = 2000; // 4x, so quadratic (~16x time) is well clear of linear (~4x)

        // A single small/large pair is one sample of a noisy process: a GC pause or scheduler
        // stall landing on only one of the two calls can inflate the ratio even when the underlying
        // read cost is linear (#1418 — flaked once under a full-gates run with overlapped audits
        // loading the machine, passed repeatedly in isolation). Retrying the whole pair does not
        // weaken what this proves: a genuine O(n^2) regression is a property of the code, not the
        // machine, so it reproduces on every attempt, while a noise-driven outlier is unlikely to
        // survive several. Fail only if every attempt shows super-linear growth.
        const int maxAttempts = 5;
        var attempts = new List<(double SmallMs, double LargeMs)>();
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var smallMs = await MeasureReadMsPerRound(small);
            var largeMs = await MeasureReadMsPerRound(large);
            attempts.Add((smallMs, largeMs));

            if (ReadCostScalesLinearly(smallMs, small, largeMs, large))
            {
                return;
            }
        }

        Assert.Fail(
            $"Re-read cost grew faster than linearly in all {maxAttempts} attempts: "
            + string.Join(", ", attempts.Select(a => $"{a.SmallMs:F3}ms->{a.LargeMs:F3}ms"))
            + $" at {small}->{large} events (a {large / small}x size increase should stay within ~{large / small}x "
            + "time, checked on every attempt). This is the signal the manifest cache exists for, not a "
            + "machine-speed check.");
    }

    /// <summary>
    /// The control for the shape test above: proves the classifier actually rejects a super-linear
    /// re-read and accepts a linear one, with synthetic timings, so the guard's discrimination does
    /// not depend on wall-clock at all (which is the whole point of #861). Polarity is asserted in
    /// both directions at the threshold.
    /// </summary>
    [Fact]
    public void ReadCostScalesLinearly_accepts_linear_and_rejects_super_linear()
    {
        // 4x events, ~4x time (plus noise) — linear, accepted.
        Assert.True(ReadCostScalesLinearly(smallMs: 10, smallEvents: 500, largeMs: 42, largeEvents: 2000));
        // 4x events, ~16x time — quadratic, rejected: the manifest-cache-worthy blowup reds.
        Assert.False(ReadCostScalesLinearly(smallMs: 10, smallEvents: 500, largeMs: 160, largeEvents: 2000));
        // Right at the linear-with-slack boundary (ratio == 4 * 2) stays accepted; a hair past reds.
        Assert.True(ReadCostScalesLinearly(smallMs: 10, smallEvents: 500, largeMs: 80, largeEvents: 2000));
        Assert.False(ReadCostScalesLinearly(smallMs: 10, smallEvents: 500, largeMs: 81, largeEvents: 2000));
    }

    /// <summary>
    /// Load-invariant shape check: does re-read time scale no worse than proportionally to event
    /// count? Linear cost gives <c>timeRatio ≈ sizeRatio</c>; a super-linear cost gives
    /// <c>timeRatio ≈ sizeRatio^k</c>. Allowing up to <paramref name="slack"/>× the size ratio keeps
    /// a linear reader (whose ratio is actually ≤ sizeRatio, since fixed per-read overhead does not
    /// scale) comfortably inside while a quadratic one at a 4× size increase (16×) is well outside.
    /// </summary>
    private static bool ReadCostScalesLinearly(
        double smallMs, int smallEvents, double largeMs, int largeEvents, double slack = 2.0)
    {
        var sizeRatio = (double)largeEvents / smallEvents;
        var timeRatio = largeMs / smallMs;
        return timeRatio <= sizeRatio * slack;
    }

    private static async Task<double> MeasureReadMsPerRound(int events)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"perf-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                for (var i = 0; i < events / 2; i++)
                {
                    var executionId = new ExecutionId($"exec-{i}");
                    var request = new ExecutionRequest(
                        executionId,
                        new WorkflowId("wf-perf"),
                        new StepId($"step-{i}"),
                        "worker",
                        [],
                        ["out"],
                        TimeSpan.FromSeconds(30),
                        [],
                        new Dictionary<StepId, ExecutionId>());

                    await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
                    await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
                }
            }

            var reader = new FlowEventLogReader(logPath);
            const int rounds = 50;

            // MIN across rounds, not the average: the fastest observed round is the one the scheduler
            // stole the least time from, so it tracks compute cost. An average folds contention spikes
            // back into the number — exactly what #861 is removing — and would also break the
            // load-invariance the two-size ratio depends on.
            var best = double.MaxValue;
            for (var i = 0; i < rounds; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                await reader.ReadAllAsync(TestContext.Current.CancellationToken);
                stopwatch.Stop();
                best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
            }

            return best;
        }
        finally
        {
            FileCleanup.Delete(logPath);
        }
    }

    private static async Task AssertOutputExistsAsync(
        string artifactsRoot, StepState stepState, string outputName, string? expectedContent = null)
    {
        var executionId = stepState.LatestExecutionId!.Value;
        var outputPath = Path.Combine(artifactsRoot, $"execution_{executionId}", outputName);

        Assert.True(File.Exists(outputPath));

        if (expectedContent is not null)
        {
            Assert.Equal(expectedContent, (await File.ReadAllTextAsync(outputPath)).Trim());
        }
    }

    [Fact]
    public async Task ExecutionFailed_event_records_stderr_fragment_when_real_worker_exits_1_with_stderr()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        try
        {
            var step = new WorkflowStepDefinition(
                new StepId("failing_step"),
                Worker: "failing_worker",
                Inputs: [],
                Outputs: [],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(1));
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-stderr"),
                new WorkflowTemplateId("wf-stderr-test"),
                WorkflowTemplateVersion: 1,
                Steps: [step]);
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var distinctiveStderr = "DISTINCTIVE_STDERR_FAILURE_FRAGMENT_759";
            var target = OperatingSystem.IsWindows()
                ? new CoreDispatchTarget("cmd", ["/c", $"echo {distinctiveStderr} 1>&2 & exit 1"])
                : new CoreDispatchTarget("sh", ["-c", $"echo {distinctiveStderr} >&2; exit 1"]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["failing_worker"] = new WorkerBinding.Process(
                    new WorkerContract("failing_worker", [], [], []),
                    target,
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-stderr-test"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = events.OfType<FlowEvent.ExecutionFailed>().Single();

            Assert.NotNull(failedEvent.Reason);
            Assert.Contains(distinctiveStderr, failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static IReadOnlyList<ExecutionId> GetAcceptedExecutionIds(IReadOnlyList<FlowEvent> events, StepId stepId) => events
        .OfType<FlowEvent.ExecutionRequestAccepted>()
        .Where(e => e.Request.StepId == stepId)
        .Select(e => e.Request.ExecutionId)
        .ToList();

    private static StepStatus? GetTerminalStatus(IReadOnlyList<FlowEvent> events, ExecutionId executionId) => events
        .Select(flowEvent => flowEvent switch
        {
            FlowEvent.ExecutionSucceeded succeeded when succeeded.ExecutionId == executionId => StepStatus.Succeeded,
            FlowEvent.ExecutionFailed failed when failed.ExecutionId == executionId => StepStatus.Failed,
            FlowEvent.ExecutionCancelled cancelled when cancelled.ExecutionId == executionId => StepStatus.Cancelled,
            _ => (StepStatus?)null,
        })
        .FirstOrDefault(status => status is not null);
}
