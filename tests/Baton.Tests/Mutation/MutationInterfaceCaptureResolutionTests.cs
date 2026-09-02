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

    private sealed class FakeResponseParser(string response) : IWorkerResponseParser
    {
        public bool TryParseFinalResponse(string rawLine, out string? parsedResponse)
        {
            parsedResponse = response;
            return true;
        }
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
