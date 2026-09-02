using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Status;
using Baton.Store;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1608: <see cref="MutationInterface.RecordCaptureResolutionAsync"/> — the conductor resolution
/// verb's own domain-level mutation surface. <see cref="Baton.Cli.ResolveCommand"/> is the thin CLI
/// wrapper around this; these tests drive the method directly, the same way
/// <see cref="MutationInterfaceDecisionTests"/> drives <see cref="MutationInterface.RecordDecisionAsync"/>.
/// </summary>
public class MutationInterfaceCaptureResolutionTests
{
    private static readonly StepId A = new("a");

    [Fact]
    public async Task Accepting_a_capture_writes_the_declared_output_and_settles_Succeeded()
    {
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var state = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken);

            var step = Assert.Single(state.Steps, s => s.StepId == A);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);
            Assert.Null(step.LatestCapturedResponseFile);
            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            Assert.Equal(WorkflowOutcome.Succeeded, WorkflowOutcome.Describe(state));

            var outputPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId), "advice.md");
            var written = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Equal("the worker's real answer", written);
            Assert.DoesNotContain(OutputMaterializer.CapturedResponseHeader, written, StringComparison.Ordinal);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var resolved = Assert.Single(events.OfType<FlowEvent.CaptureResolved>());
            Assert.True(resolved.Accepted);
            Assert.Equal(["advice.md"], resolved.ResolvedOutputNames);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Rejecting_a_capture_writes_nothing_and_leaves_the_step_resolved_but_Failed()
    {
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "not actually an honest advice.md");
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var state = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: false, reason: "the capture does not honestly satisfy advice.md",
                cancellationToken: TestContext.Current.CancellationToken);

            var step = Assert.Single(state.Steps, s => s.StepId == A);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.False(step.IndeterminateAwaitingResolution);

            var outputPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId), "advice.md");
            Assert.False(File.Exists(outputPath));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var resolved = Assert.Single(events.OfType<FlowEvent.CaptureResolved>());
            Assert.False(resolved.Accepted);
            Assert.Equal("the capture does not honestly satisfy advice.md", resolved.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Rejecting_without_a_reason_throws_and_appends_nothing()
    {
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "irrelevant");
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var eventsBefore = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                    accepted: false, reason: null, cancellationToken: TestContext.Current.CancellationToken));

            var eventsAfter = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(eventsBefore.Count, eventsAfter.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Resolving_an_execution_with_no_unresolved_capture_throws()
    {
        var (roomDirectory, artifactsRoot, logPath, _, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "irrelevant");
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, new ExecutionId("no-such-execution"),
                    accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_second_resolution_against_an_already_resolved_capture_throws()
    {
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                    accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_reserved_third_name_in_a_multi_output_capture_refuses_before_writing_either_earlier_name()
    {
        // F9 (#1608 review): every existing fixture before this one is single-output, so aa51b902's own
        // headline fix -- splitting validation into its own pass ahead of the write pass, specifically
        // so a later name's reserved/traversal failure can never leave an earlier name already written
        // -- was unfalsifiable: merging the two passes back into one foreach would still pass every
        // other test in this file. A two-output capture with a reserved third name is what actually
        // exercises the split, and reaches the reserved/traversal refusal arm at all (also untested
        // before this).
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");
        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
                new WorkflowTemplateId("resolve-multi-output-test"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(A, "stub-worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2))]);

            Directory.CreateDirectory(roomDirectory);
            await using (var seedWriter = new FlowEventLogWriter(logPath))
            {
                await seedWriter.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                        executionId, new WorkflowId("wf"), A, "stub-worker", [], [], TimeSpan.FromSeconds(30), [],
                        new Dictionary<StepId, ExecutionId>())),
                    TestContext.Current.CancellationToken);
                await seedWriter.AppendAsync(
                    new FlowEvent.ExecutionIndeterminate(
                        executionId, "captured, awaiting conductor resolution",
                        OutputMaterializer.CapturedResponseFileName, ["advice.md", "notes.md", "../evil.md"]),
                    TestContext.Current.CancellationToken);
            }

            var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, OutputMaterializer.CapturedResponseFileName),
                OutputMaterializer.CapturedResponseHeader + "\n\nthe worker's real answer",
                TestContext.Current.CancellationToken);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var ex = await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                    accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("../evil.md", ex.Message, StringComparison.Ordinal);

            Assert.False(File.Exists(Path.Combine(outputDirectory, "advice.md")), "the earlier name must not have been written.");
            Assert.False(File.Exists(Path.Combine(outputDirectory, "notes.md")), "the earlier name must not have been written.");

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.CaptureResolved>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_crash_between_the_journaled_fact_and_the_write_re_materializes_the_missing_output_on_the_next_resolve()
    {
        // #1608 review finding 5: RecordCaptureResolutionAsync now journals CaptureResolved BEFORE
        // writing the declared output(s) ("fact then files"), so a crash in that window leaves exactly
        // this shape -- durably recorded as accepted, but the file it describes never written. Fabricated
        // by hand rather than driven through CrashTestHost: the thing under test is the reconciliation
        // predicate (fact present + file missing -> repair), not crash mechanics: a real kill mid-write
        // would produce the identical durable shape this constructs directly.
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            await using (var crashWriter = new FlowEventLogWriter(logPath))
            {
                await crashWriter.AppendAsync(
                    new FlowEvent.CaptureResolved(A, executionId, Accepted: true, ResolvedOutputNames: ["advice.md"]),
                    TestContext.Current.CancellationToken);
            }

            var outputPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId), "advice.md");
            Assert.False(File.Exists(outputPath), "the fixture must reproduce fact-present/file-missing, not an ordinary accept.");

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var state = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken);

            var step = Assert.Single(state.Steps, s => s.StepId == A);
            Assert.Equal(StepStatus.Succeeded, step.Status);

            var written = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Equal("the worker's real answer", written);

            // The repair must not append a second fact -- the crashed attempt's own CaptureResolved is
            // still the only one on the ledger, matching the exactly-once invariant the ordinary
            // (non-repair) duplicate-resolution refusal above also protects.
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.CaptureResolved>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_crash_that_also_took_the_captured_response_file_fails_closed_instead_of_reporting_a_repair()
    {
        // Polarity partner of the repair test above, one condition apart (the raw capture file is also
        // gone, not just the declared output) -- proving the repair path actually depends on the
        // capture surviving, rather than reporting success regardless.
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            FileCleanup.EnsureDeleted(Path.Combine(outputDirectory, OutputMaterializer.CapturedResponseFileName));

            await using (var crashWriter = new FlowEventLogWriter(logPath))
            {
                await crashWriter.AppendAsync(
                    new FlowEvent.CaptureResolved(A, executionId, Accepted: true, ResolvedOutputNames: ["advice.md"]),
                    TestContext.Current.CancellationToken);
            }

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var ex = await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                    accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("manual repair", ex.Message, StringComparison.Ordinal);

            var outputPath = Path.Combine(outputDirectory, "advice.md");
            Assert.False(File.Exists(outputPath));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.CaptureResolved>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_crash_DURING_the_write_leaving_a_zero_length_output_is_repaired_like_a_missing_one()
    {
        // #1608 re-review finding 3: the repair predicate used to be existence-only, which reported the
        // likeliest crash shape as nothing-to-repair -- see ReconcileAcceptedCaptureAsync's own remarks
        // for what "missing" now means and why. This is the arm that shape reaches.
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            await using (var crashWriter = new FlowEventLogWriter(logPath))
            {
                await crashWriter.AppendAsync(
                    new FlowEvent.CaptureResolved(A, executionId, Accepted: true, ResolvedOutputNames: ["advice.md"]),
                    TestContext.Current.CancellationToken);
            }

            var outputPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId), "advice.md");
            await File.WriteAllTextAsync(outputPath, string.Empty, TestContext.Current.CancellationToken);
            Assert.Equal(0, new FileInfo(outputPath).Length);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var state = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StepStatus.Succeeded, Assert.Single(state.Steps, s => s.StepId == A).Status);
            Assert.Equal(
                "the worker's real answer",
                await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.CaptureResolved>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_non_empty_declared_output_edited_after_acceptance_is_left_exactly_as_it_is()
    {
        // Polarity partner of the zero-length repair above, one condition apart (the file has content).
        // Widening "missing" to zero-length must NOT widen it to "differs from the capture": the repair
        // is what stops a later `baton resolve` clobbering a human's edit, so this arm is what keeps the
        // finding-3 fix from becoming "always rewrite".
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            await using (var crashWriter = new FlowEventLogWriter(logPath))
            {
                await crashWriter.AppendAsync(
                    new FlowEvent.CaptureResolved(A, executionId, Accepted: true, ResolvedOutputNames: ["advice.md"]),
                    TestContext.Current.CancellationToken);
            }

            var outputPath = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId), "advice.md");
            await File.WriteAllTextAsync(outputPath, "the conductor's own edit", TestContext.Current.CancellationToken);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var ex = await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                    accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);

            Assert.Equal(
                "the conductor's own edit",
                await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1608: pins <see cref="MutationInterface"/>'s own private <c>ToOutcomeEvent</c> switch —
    /// every other test above fabricates <see cref="FlowEvent.ExecutionIndeterminate"/> directly, so
    /// none of them proves the real dispatch pipeline actually reaches
    /// <see cref="OutcomeVerdict.Indeterminate"/>'s new arm rather than the pre-existing
    /// <see cref="ArgumentOutOfRangeException"/> default (or, worse, silently falling back to the old
    /// <see cref="FlowEvent.ExecutionFailed"/> mapping). Drives a real dispatch through
    /// <see cref="MutationInterface.StartWorkflowAsync"/> with a <see cref="StubCoreDispatcher"/> and a
    /// response-parser-carrying <see cref="WorkerBinding.Process"/>, the same #1594 captured-response
    /// shape <see cref="OutcomeClassifierTests"/> exercises at the classifier layer alone.
    /// </summary>
    [Fact]
    public async Task A_real_dispatch_with_a_missing_prose_safe_output_reaches_ExecutionIndeterminate_not_ExecutionFailed()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
            new WorkflowTemplateId("resolve-dispatch-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(A, "stub-worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var contract = new WorkerContract("stub-worker", [], [new ProducedOutput("advice.md")], []);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["stub-worker"] = new WorkerBinding.Process(
                    contract, new CoreDispatchTarget("stub", []), TimeSpan.FromSeconds(30),
                    ResponseParser: new FakeResponseParser("the worker's real answer")),
            };

            var stub = new StubCoreDispatcher();
            var dispatchResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                cancellationToken: TestContext.Current.CancellationToken);

            var readTask = stub.DispatchStarted.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            Assert.Same(readTask, completed);
            Assert.Equal(A, await readTask);

            var accepted = Assert.Single(
                (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.ExecutionRequestAccepted>());
            var executionId = accepted.Request.ExecutionId;

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, ".stdout.log"),
                """{"event":"result","result":{"status":"SUCCESS","response":"the worker's real answer"}}""" + "\n",
                TestContext.Current.CancellationToken);

            dispatchResult.SetResult(new CoreDispatchResult(0, CoreExitReason.Natural));

            var finalState = await pumpTask;

            var step = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.True(step.IndeterminateAwaitingResolution);
            Assert.Equal(OutputMaterializer.CapturedResponseFileName, step.LatestCapturedResponseFile);

            var finalEvents = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(finalEvents.OfType<FlowEvent.ExecutionIndeterminate>());
            Assert.Empty(finalEvents.OfType<FlowEvent.ExecutionFailed>());

            // Chains the real producer (OutputMaterializer.TryCaptureFinalResponse, above, via the real
            // dispatch pipeline) into the real resolver (StripCapturedResponseHeader) -- every other
            // resolution test hand-fabricates the captured file with the exact "\n\n" separator the
            // strip expects, which proves the strip logic but never proves the producer's own separator
            // actually matches it. This is the one test where both ends are the real code.
            var resolvedState = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: true, reason: null, cancellationToken: TestContext.Current.CancellationToken);

            var resolvedStep = Assert.Single(resolvedState.Steps);
            Assert.Equal(StepStatus.Succeeded, resolvedStep.Status);

            var writtenOutput = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "advice.md"), TestContext.Current.CancellationToken);
            Assert.Equal("the worker's real answer", writtenOutput);
            Assert.DoesNotContain(OutputMaterializer.CapturedResponseHeader, writtenOutput, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1593 replay fixture from measured journal: uncaptured exit-0 contract failure settles Indeterminate (spec/baton.md §3).
    /// </summary>
    [Fact]
    public async Task An_exit_0_worker_with_missing_contract_reaches_ExecutionIndeterminate_no_retry_scheduled_and_describes_Indeterminate()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
            new WorkflowTemplateId("replay-1593-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(A, "stub-worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var contract = new WorkerContract("stub-worker", [], [new ProducedOutput("advice.md")], []);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["stub-worker"] = new WorkerBinding.Process(
                    contract, new CoreDispatchTarget("stub", []), TimeSpan.FromSeconds(30)),
            };

            var stub = new StubCoreDispatcher();
            var dispatchResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                cancellationToken: TestContext.Current.CancellationToken);

            var readTask = stub.DispatchStarted.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            Assert.Same(readTask, completed);
            Assert.Equal(A, await readTask);

            var accepted = Assert.Single(
                (await reader.ReadAllAsync(TestContext.Current.CancellationToken)).OfType<FlowEvent.ExecutionRequestAccepted>());
            var executionId = accepted.Request.ExecutionId;

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId);
            Directory.CreateDirectory(outputDirectory);

            // Natural exit 0, but no outputs written
            dispatchResult.SetResult(new CoreDispatchResult(0, CoreExitReason.Natural));

            var finalState = await pumpTask;

            var step = Assert.Single(finalState.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
            Assert.True(step.IndeterminateAwaitingResolution);
            Assert.Null(step.LatestCapturedResponseFile);
            Assert.Equal(["advice.md"], step.LatestUnsatisfiedOutputNames);
            Assert.Equal(WorkflowOutcome.Indeterminate, WorkflowOutcome.Describe(finalState));
            Assert.False(Baton.Scheduling.RetryEngine.MayRetry(step, snapshot.Steps[0].RetryPolicy));

            var finalEvents = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(finalEvents.OfType<FlowEvent.ExecutionIndeterminate>());
            Assert.Empty(finalEvents.OfType<FlowEvent.ExecutionFailed>());
            Assert.Empty(finalEvents.OfType<FlowEvent.StepRetryScheduled>());

            // Confirm conductor can resolve via rejection to leave it Failed for redispatch
            var resolvedState = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: false, reason: "conductors inspection of worktree confirms retry needed",
                cancellationToken: TestContext.Current.CancellationToken);

            var resolvedStep = Assert.Single(resolvedState.Steps);
            Assert.Equal(StepStatus.Failed, resolvedStep.Status);
            Assert.False(resolvedStep.IndeterminateAwaitingResolution);
            Assert.Equal(WorkflowOutcome.Failed, WorkflowOutcome.Describe(resolvedState));

            // F8 (#1593 review): a reject of a ContractFailure producer must not hand the step back to
            // blind retry -- the review's own finding was that this test asserted the post-reject state
            // was Failed but never checked MayRetry on it, so a reject that silently re-armed retry on
            // a possibly-mutated workspace would have passed unnoticed.
            Assert.True(resolvedStep.RetryForeclosed);
            Assert.False(Baton.Scheduling.RetryEngine.MayRetry(resolvedStep, snapshot.Steps[0].RetryPolicy));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Polarity partner for #1593: A worker that exits non-zero with contract missing follows the ordinary retry path.
    /// </summary>
    [Fact]
    public async Task Polarity_partner_a_non_zero_exit_with_missing_contract_reaches_ExecutionFailed_and_schedules_retry()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
            new WorkflowTemplateId("polarity-1593-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(A, "stub-worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(2))]);

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        try
        {
            var contract = new WorkerContract("stub-worker", [], [new ProducedOutput("advice.md")], []);
            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["stub-worker"] = new WorkerBinding.Process(
                    contract, new CoreDispatchTarget("stub", []), TimeSpan.FromSeconds(30)),
            };

            var stub = new StubCoreDispatcher();
            var dispatchResult = stub.EnqueueResult(A);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                cancellationToken: TestContext.Current.CancellationToken);

            var readTask = stub.DispatchStarted.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
            Assert.Same(readTask, completed);
            Assert.Equal(A, await readTask);

            // Non-zero exit code (e.g. 1)
            dispatchResult.SetResult(new CoreDispatchResult(1, CoreExitReason.Natural));

            // Wait until retry is scheduled
            var finalEvents = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            while (!finalEvents.OfType<FlowEvent.StepRetryScheduled>().Any())
            {
                await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: bounded polling interval
                finalEvents = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            }

            Assert.Single(finalEvents.OfType<FlowEvent.ExecutionFailed>());
            Assert.Empty(finalEvents.OfType<FlowEvent.ExecutionIndeterminate>());
            Assert.Single(finalEvents.OfType<FlowEvent.StepRetryScheduled>());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private sealed class FakeResponseParser(string response) : IWorkerResponseParser
    {
        public bool TryParseFinalResponse(string rawLine, out string? parsedResponse)
        {
            parsedResponse = response;
            return true;
        }
    }

    /// <summary>
    /// #1623/#1644 merge: `Indeterminate` has three producers now, but only one of them leaves a
    /// captured response, and `baton resolve` exists solely to accept or reject that capture. A step
    /// settled Indeterminate by <see cref="FlowEvent.VerifyFailed"/> must therefore be REFUSED here
    /// even though its <see cref="StepState.IndeterminateAwaitingResolution"/> reads true — the flag
    /// alone is not this verb's admission test.
    /// <para>
    /// Asserted on the <c>--reject</c> path specifically, because that is the one that was reachable:
    /// <c>--accept</c> already refused such a step further in, on its null captured file.
    /// <see cref="MutationInterface.RecordCaptureResolutionAsync"/>'s own guard comment states what
    /// admitting it would have cost; not restated here. The second assertion (no
    /// <see cref="FlowEvent.CaptureResolved"/> in the log) is what pins that consequence rather than
    /// only the message.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_verify_failed_Indeterminate_step_is_refused_by_baton_resolve()
    {
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) =
            await SeedVerifyFailedRoomAsync();
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var ex = await Assert.ThrowsAsync<InvalidCaptureResolutionException>(() =>
                MutationInterface.RecordCaptureResolutionAsync(
                    roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                    accepted: false, reason: "not my problem",
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("no unresolved indeterminate capture", ex.Message, StringComparison.Ordinal);

            var events = await new FlowEventLogReader(logPath).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Empty(events.OfType<FlowEvent.CaptureResolved>());

            // The refusal must not have quietly settled the step either: it is still Indeterminate,
            // still awaiting a fix-and-redispatch rather than a resolution.
            Assert.True(Baton.Projection.StateProjector.Project(events, snapshot)
                .Steps.Single().IndeterminateAwaitingResolution);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The discriminating control for the test above, one condition apart: the identical call shape
    /// against a step whose Indeterminate DID come with a captured response is admitted and resolves.
    /// Without this arm, the refusal above would pass equally against a guard that had simply broken
    /// <c>baton resolve</c> outright.
    /// </summary>
    [Fact]
    public async Task A_captured_response_Indeterminate_step_is_still_admitted_by_the_same_call_shape()
    {
        var (roomDirectory, artifactsRoot, logPath, executionId, snapshot) = await SeedIndeterminateRoomAsync(
            outputName: "advice.md", capturedBody: "the worker's real answer");
        try
        {
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);

            var state = await MutationInterface.RecordCaptureResolutionAsync(
                roomDirectory, snapshot, artifactsRoot, reader, writer, executionId,
                accepted: false, reason: "not my problem",
                cancellationToken: TestContext.Current.CancellationToken);

            var step = state.Steps.Single();
            Assert.False(step.IndeterminateAwaitingResolution);

            // F8 (#1593 review) polarity control: unlike a ContractFailure producer's reject (asserted
            // above in An_exit_0_worker_with_missing_contract_...), a CapturedResponse producer's reject
            // stays retry-eligible -- #1608's own ruling. That shape is "substantial work happened",
            // never "the workspace may have been mutated", so nothing here needs foreclosing.
            Assert.False(step.RetryForeclosed);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Fabricates a room settled Indeterminate by the #1623 verify producer rather than the #1608
    /// capture one: same <see cref="StepState.IndeterminateAwaitingResolution"/> flag, deliberately no
    /// captured response anywhere.
    /// </summary>
    private static async Task<(string RoomDirectory, string ArtifactsRoot, string LogPath, ExecutionId ExecutionId, WorkflowDefinitionSnapshot Snapshot)>
        SeedVerifyFailedRoomAsync()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
            new WorkflowTemplateId("resolve-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(A, "stub-worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        Directory.CreateDirectory(roomDirectory);

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    executionId, new WorkflowId("wf"), A, "stub-worker", [], [], TimeSpan.FromSeconds(30), [],
                    new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.VerifyFailed(executionId, ["fmt-check"], "GATES: FAIL 1 of 25 -- fmt-check"),
                TestContext.Current.CancellationToken);
        }

        ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
        return (roomDirectory, artifactsRoot, logPath, executionId, snapshot);
    }

    /// <summary>
    /// Fabricates a room already settled Indeterminate: one step's execution carries a projected
    /// <see cref="FlowEvent.ExecutionIndeterminate"/>, and its output directory holds the real captured
    /// file (header + body), the same shape <see cref="OutputMaterializer.TryCaptureFinalResponse"/>
    /// would have written.
    /// </summary>
    private static async Task<(string RoomDirectory, string ArtifactsRoot, string LogPath, ExecutionId ExecutionId, WorkflowDefinitionSnapshot Snapshot)>
        SeedIndeterminateRoomAsync(string outputName, string capturedBody)
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId($"exec-{Guid.NewGuid():N}");

        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
            new WorkflowTemplateId("resolve-test"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(A, "stub-worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

        Directory.CreateDirectory(roomDirectory);

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(
                new FlowEvent.ExecutionRequestAccepted(new ExecutionRequest(
                    executionId, new WorkflowId("wf"), A, "stub-worker", [], [], TimeSpan.FromSeconds(30), [],
                    new Dictionary<StepId, ExecutionId>())),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionIndeterminate(
                    executionId, "captured, awaiting conductor resolution",
                    OutputMaterializer.CapturedResponseFileName, [outputName]),
                TestContext.Current.CancellationToken);
        }

        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRoot, executionId);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, OutputMaterializer.CapturedResponseFileName),
            OutputMaterializer.CapturedResponseHeader + "\n\n" + capturedBody,
            TestContext.Current.CancellationToken);

        return (roomDirectory, artifactsRoot, logPath, executionId, snapshot);
    }
}
