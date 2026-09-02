using Baton.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using static Baton.Tests.TestSupport.ShellWorkerCommands;

namespace Baton.Tests.Mutation;

/// <summary>
/// Integration tests: these spawn real processes through the managed <c>BatonTask</c> engine
/// (M7 Phase 7's acceptance criteria — a three-step linear workflow runs end-to-end through
/// <see cref="MutationInterface.StartWorkflowAsync"/>). No mocking of Baton.Core itself. A clean
/// exit-0 with no output classifies <c>ExecutionIndeterminate</c>, not <c>ExecutionFailed</c>
/// (#1593) — see <see cref="StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionIndeterminate"/>.
/// </summary>
public class MutationInterfaceTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly StepId Critic = new("critic");
    private static readonly StepId Publisher = new("publisher");

    [Fact]
    public async Task StartWorkflowAsync_runs_a_three_step_linear_workflow_to_completion()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1"),
                new WorkflowTemplateId("architect-critic-publisher"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(Publisher, "publisher", ["review"], ["summary"], DependsOn: [Critic], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
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
                new WorkflowId("wf-1"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));

            var publisherExecutionId = finalState.Steps.Single(s => s.StepId == Publisher).LatestExecutionId!.Value;
            var summaryPath = Path.Combine(artifactsRoot, $"execution_{publisherExecutionId}", "summary");
            Assert.True(File.Exists(summaryPath));
            Assert.Equal("architect", (await File.ReadAllTextAsync(summaryPath, TestContext.Current.CancellationToken)).Trim());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_step_that_fails_once_then_succeeds()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var markerFilePath = Path.Combine(roomDirectory, "attempt-marker");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-3"),
                new WorkflowTemplateId("flaky-architect-critic"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(MaxAttempts: 2)),
                    new WorkflowStepDefinition(Critic, "critic", ["plan"], ["review"], DependsOn: [Architect], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    FailOnFirstAttemptThenSucceed(markerFilePath, "plan", "architect"),
                    TimeSpan.FromSeconds(30)),
                ["critic"] = new WorkerBinding.Process(
                    new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                    CopyFirstInputTo("review"),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-3"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
            Assert.Equal(0, finalState.Steps.Single(s => s.StepId == Architect).ConsecutiveFailureCount);

            // The history shape: two distinct ExecutionIds for Architect, the first failed and
            // the second succeeded — neither event mutated or removed.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var architectAttempts = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == Architect)
                .Select(e => e.Request.ExecutionId)
                .ToList();
            Assert.Equal(2, architectAttempts.Count);
            Assert.Equal(architectAttempts.Distinct().Count(), architectAttempts.Count);
            Assert.Contains(events, e => e is FlowEvent.ExecutionFailed failed && architectAttempts.Contains(failed.ExecutionId));
            Assert.Contains(events, e => e is FlowEvent.ExecutionSucceeded succeeded && architectAttempts.Contains(succeeded.ExecutionId));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_classifies_a_clean_exit_with_no_output_as_ExecutionIndeterminate()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("silent-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-2"),
                new WorkflowTemplateId("silent"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "silent", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["silent"] = new WorkerBinding.Process(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    ExitCleanlyWithoutWriting(),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-2"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);
            Assert.True(stepState.IndeterminateAwaitingResolution);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var indeterminateEvent = events.OfType<FlowEvent.ExecutionIndeterminate>().Single();
            Assert.NotNull(indeterminateEvent.Reason);
            Assert.Contains("output.txt", indeterminateEvent.Reason);
            Assert.Contains("work possibly on disk", indeterminateEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_appends_the_ZeroOutputsDespiteSubstantialWork_tripwire_through_the_live_dispatch_path()
    {
        // #1586 S1 (the #1594 ruling's tripwire), the wiring OutcomeClassifierTests cannot reach: that
        // suite pins SubstantialWorkNoOutputsEvidence at OutcomeClassifier.Classify's unit level with a
        // fake usage parser; nothing exercised MutationInterface's own
        // AppendZeroOutputsTripwireIfAnyAsync call site actually appending the event to a real journal
        // off a real dispatch's own ExecutionStreamLogger-captured .stdout.log. This is that proof, for
        // the live-dispatch call site specifically (the crash-recovery ToClassify call site is the
        // other one MutationInterface.cs wires this from; that one is exercised by the projection-level
        // tests, not a second live process here).
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var scriptDirectory = Path.Combine(roomDirectory, "scripts");
        try
        {
            var stepId = new StepId("substantial-but-silent");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-tripwire"),
                new WorkflowTemplateId("tripwire"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "silent", [], ["output.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["silent"] = new WorkerBinding.Process(
                    new WorkerContract("silent", [], [new ProducedOutput("output.txt")], []),
                    EmitSubstantialUsageThenExitWithoutWriting(scriptDirectory),
                    TimeSpan.FromSeconds(30),
                    Adapter: "agy"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-tripwire"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var tripwire = Assert.Single(events.OfType<FlowEvent.ZeroOutputsDespiteSubstantialWork>());
            Assert.Contains("4 turn", tripwire.Evidence);
            Assert.Contains("500", tripwire.Evidence);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_records_a_Retryable_ExecutionFailed_when_the_OS_itself_refuses_the_spawn()
    {
        // The refusal family's generic member (#747's review): BatonException, not the typed guard.
        // Retryable — not Permanent — because an OS refusal is not proven deterministic; a stuck
        // cause terminates through RetryPolicy exhaustion instead. Polarity partner to the
        // Permanent assert in the CommandLineTooLongException test below.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("os-refused-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-os-refusal"),
                new WorkflowTemplateId("os-refusal"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "os-refused", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["os-refused"] = new WorkerBinding.Process(
                    new WorkerContract("os-refused", [], [new ProducedOutput("out.txt")], []),
                    new CoreDispatchTarget("dummy", []),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-os-refusal"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer,
                new OsRefusingCoreDispatcher(), cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.Retryable, failedEvent.FailureClassification);
            Assert.StartsWith("Spawn refused:", failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_records_ExecutionFailed_when_dispatch_throws_CommandLineTooLongException()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var stepId = new StepId("long-cmd-step");
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-refusal"),
                new WorkflowTemplateId("refusal"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(stepId, "long-cmd", [], ["out.txt"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["long-cmd"] = new WorkerBinding.Process(
                    new WorkerContract("long-cmd", [], [new ProducedOutput("out.txt")], []),
                    new CoreDispatchTarget("dummy", []),
                    TimeSpan.FromSeconds(30)),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var refusalMessage = "Command line length 40000 exceeds maximum allowable length of 32767.";
            var dispatcher = new RefusingCoreDispatcher(refusalMessage);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-refusal"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var stepState = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, stepState.Status);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var failedEvent = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(FailureClassification.Permanent, failedEvent.FailureClassification);
            Assert.NotNull(failedEvent.Reason);
            Assert.Contains(refusalMessage, failedEvent.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_runs_the_engine_verify_step_and_settles_Succeeded_when_it_passes()
    {
        // #1623: the real end-to-end path through a REAL pixi subprocess -- MutationInterface's own
        // gating (Verdict == Succeeded && binding.VerifyPixiTask is not null) plus the real
        // VerifyRunner.RunAsync's "pixi" spawn, not a fake. `buildlock-selftest` is an existing,
        // already-fast (a few seconds), already-deterministic pixi task (tools/buildlock.py's own
        // control arm) -- reused as the fixture rather than adding a new pixi.toml entry just for this
        // test. The FAIL half is covered by VerifyRunnerTests against a fake command instead of a real
        // gates failure, which would be slow and not actually more informative about this wiring.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify"),
                new WorkflowTemplateId("verify"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RepoRoot() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_settles_Indeterminate_when_VerifyPixiTask_fails()
    {
        // #1623 / F6: a failing verify task appends VerifyFailed, does NOT append ExecutionSucceeded,
        // and settles the step Indeterminate.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-fail"),
                new WorkflowTemplateId("verify-fail"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RepoRoot() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "this-task-definitely-does-not-exist"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-fail"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.NotNull(architect.IndeterminateReason);
            Assert.True(architect.RetryForeclosed);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            var verifyFailed = Assert.Single(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Equal(VerifyFailedKind.GatesFailed, verifyFailed.Kind);
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_skips_verify_when_execution_classification_is_failed()
    {
        // #1623 / F6: a failed worker never triggers verify. #1593 (found-while-fixing, this PR): a
        // clean exit-0 with a missing declared output no longer classifies ExecutionFailed -- it
        // settles Indeterminate instead, so ExitCleanlyWithoutWriting() no longer produces the "an
        // ordinary Failed worker" shape this test needs. Swapped for ExitWithFailureCode(), the same
        // migration the review found legitimate across MutationInterfaceRetryBackoffTests,
        // PumpCheckpointCarryTests, LiveCancellationEndToEndTests and ResolveCommandEndToEndTests --
        // missed here originally since this file wasn't in that sweep.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-skip"),
                new WorkflowTemplateId("verify-skip"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    ExitWithFailureCode() with { WorkingDirectory = RepoRoot() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-skip"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyPassed>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_does_not_spawn_verify_when_role_has_no_VerifyPixiTask()
    {
        // #1623 / F6: a role without VerifyPixiTask does not spawn verify
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-none"),
                new WorkflowTemplateId("verify-none"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect"),
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: null),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-none"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Succeeded, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Single(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_operator_cancel_during_verify_settles_Cancelled_not_Indeterminate()
    {
        // #1623 re-review N3: the operator's own cancel landing inside the verify window is journalled
        // as ExecutionCancelled, not VerifyFailed/Indeterminate -- see MutationInterface.cs's own
        // comment on the branch under test for why. Foreclosing retry here would leave no discharge
        // verb (U1). VerifyStarted still survives as the diagnostic record that verify was running
        // when the cancel landed.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-verify-cancel"),
                new WorkflowTemplateId("verify-cancel"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    WriteFile("plan", "architect") with { WorkingDirectory = RepoRoot() },
                    TimeSpan.FromSeconds(30),
                    VerifyPixiTask: "buildlock-selftest"),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            using var cts = new CancellationTokenSource();
            var dispatcher = new CancellingAtCompletionDispatcher(writer, cts);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-verify-cancel"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: cts.Token);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Cancelled, architect.Status);
            Assert.Null(architect.IndeterminateReason);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.VerifyStarted>());
            Assert.Empty(events.OfType<FlowEvent.VerifyFailed>());
            Assert.Single(events.OfType<FlowEvent.ExecutionCancelled>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate pixi.toml above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task StartWorkflowAsync_arrests_an_execution_that_crosses_its_token_budget()
    {
        // #1623 ruling addendum: exercises the real MutationInterface wiring (the linked
        // CancellationTokenSource, the OnStdoutLine composition, the ExecutionArrested append instead
        // of an ordinary outcome) against a fake ICoreDispatcher that never spawns a real process --
        // TokenBudgetMonitorTests already pins the accumulation logic in isolation, and this is the
        // "wired correctly, not just correct in isolation" proof, the same split
        // StartWorkflowAsync_appends_the_ZeroOutputsDespiteSubstantialWork_tripwire... above already
        // uses for OutcomeClassifier's own tripwire.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-arrest"),
                new WorkflowTemplateId("arrest"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3))]);

            const string usageLine = """{"type":"assistant","message":{"usage":{"input_tokens":500000,"output_tokens":200000}}}""";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    new CoreDispatchTarget("cmd", ["/c", "exit 0"]),
                    TimeSpan.FromSeconds(30),
                    Adapter: "claude",
                    TokenBudget: 1000),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new ArrestingCoreDispatcher(usageLine);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-arrest"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.NotNull(architect.IndeterminateReason);
            Assert.True(architect.RetryForeclosed);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.ExecutionSucceeded>());
            Assert.Empty(events.OfType<FlowEvent.ExecutionCancelled>());
            var arrested = Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Equal(500000, arrested.Usage?.TokensIn);
            Assert.Equal(200000, arrested.Usage?.TokensOut);
            // #1682: billed (what the budget actually arrested on) is Σ input + Σ output for this
            // single line -- 700,000, crossing the 1,000 budget -- and the reason is recorded on the
            // wire, not just inferred from Arrested being true.
            Assert.Equal(700000, arrested.Usage?.BilledTokens);
            Assert.Equal(ArrestReason.TokenBudget, arrested.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_arrests_an_execution_that_crosses_its_tool_step_cap_with_zero_usage_lines()
    {
        // #1682: the SECOND, independent producer -- exercises the real MutationInterface wiring the
        // same way the token-budget test above does, but with NO TokenBudget set at all and a stream
        // that never parses as usage, proving the cap fires "independent of usage parsing" through the
        // live dispatch path, not just in TokenBudgetMonitorTests' isolation.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-toolcap"),
                new WorkflowTemplateId("toolcap"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(Architect, "architect", [], ["plan"], DependsOn: [], RetryPolicy: new RetryPolicy(3))]);

            const string toolStepLine = """{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"run_command"}}""";
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["architect"] = new WorkerBinding.Process(
                    new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                    new CoreDispatchTarget("cmd", ["/c", "exit 0"]),
                    TimeSpan.FromSeconds(30),
                    Adapter: "agy",
                    MaxToolSteps: 2),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new ArrestingCoreDispatcher(toolStepLine, repeatCount: 3);

            var finalState = await MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-toolcap"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher, cancellationToken: TestContext.Current.CancellationToken);

            var architect = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, architect.Status);
            Assert.True(architect.RetryForeclosed);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var arrested = Assert.Single(events.OfType<FlowEvent.ExecutionArrested>());
            Assert.Equal(ArrestReason.ToolStepCap, arrested.Reason);
            Assert.Equal(3, arrested.ToolStepCount);
            Assert.Null(arrested.Usage?.BilledTokens);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// A fake dispatch whose stdout is <paramref name="repeatCount"/> copies of a single,
    /// arrest-triggering line — mirrors a real worker process being torn down once
    /// <see cref="TokenBudgetMonitor"/>'s own linked cancellation fires, per
    /// <c>ICoreDispatcher.DispatchAsync</c>'s documented "cancellation comes back as a normal
    /// CoreDispatchResult" contract (never <see cref="OperationCanceledException"/>).
    /// </summary>
    private sealed class ArrestingCoreDispatcher(string usageLine, int repeatCount = 1) : ICoreDispatcher
    {
        public async Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < repeatCount; i++)
            {
                target.OnStdoutLine?.Invoke(usageLine);
            }

            var tcs = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            // Not a timing expectation: the arrest cancels this token in milliseconds. The ceiling only
            // stops a regression from hanging the suite forever, so it is set well above any plausible
            // real wait rather than tuned to the expected one.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken: CancellationToken.None);

            return new CoreDispatchResult(-1, CoreExitReason.CancelRequested);
        }
    }

    private sealed class RefusingCoreDispatcher(string refusalMessage) : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            throw new CommandLineTooLongException(refusalMessage);
        }
    }

    private sealed class OsRefusingCoreDispatcher : ICoreDispatcher
    {
        public Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            // The binding's own exception type, the shape a missing binary or a bad working
            // directory actually surfaces as (#747's review, finding 3).
            throw new Baton.Core.BatonException(Baton.Core.BatonErrorCode.SpawnFailed);
        }
    }

    private sealed class CancellingAtCompletionDispatcher(FlowEventLogWriter writer, CancellationTokenSource cts) : ICoreDispatcher
    {
        private readonly CoreDispatcher _inner = new(writer);

        public async Task<CoreDispatchResult> DispatchAsync(ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
        {
            var result = await _inner.DispatchAsync(request, target, cancellationToken).ConfigureAwait(false);
            cts.Cancel();
            return result;
        }
    }
}


