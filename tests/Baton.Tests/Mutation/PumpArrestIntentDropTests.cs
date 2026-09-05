using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.Projection;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Mutation;

/// <summary>
/// #1556 PR 2: <c>MutationInterface.SettleArrestIntentsAsync</c>'s fail-closed drop path — an arrest
/// intent the registry's drain hands the pump for an execution
/// <see cref="Baton.Projection.ArrestableExecutions.Find"/> no longer admits is dropped, named on a
/// stderr line AND (#1916 fix round 2) appended durably as a <see cref="FlowEvent.CancellationRejected"/>,
/// rather than silently discarded. This targets the "already settled" reason
/// specifically: a mark that arrives after the target has already reached a terminal outcome (the
/// ordinary "the operator's request lost the race to a legitimate finish" case, not a bug).
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class PumpArrestIntentDropTests
{
    private static readonly StepId H = new("h");
    private static readonly WorkerContract HumanContract = new("human", [], [new ProducedOutput("revision.md")], []);

    [Fact]
    public async Task An_arrest_intent_marked_for_an_already_settled_execution_is_dropped_with_the_reason()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var originalError = Console.Error;

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1556-drop"),
                new WorkflowTemplateId("template-1556-drop"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(H, "human", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding> { ["human"] = new WorkerBinding.NonProcess(HumanContract) };
            var workflowId = new WorkflowId("wf-1556-drop");
            var stub = new StubCoreDispatcher();

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var registry = new InFlightExecutionRegistry();
            // Bound explicitly (StartWorkflowAsync below rebinds it to the same writer regardless) so
            // the durable append this test asserts on below is provably reachable, not an accident of
            // StartWorkflowAsync's own internal Bind call landing before the drain that needs it.
            registry.Bind(writer);

            // Round 1: h is accepted, Running (a non-process step dispatches nothing to Core).
            var firstState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);
            var hExecutionId = firstState.Steps.Single().LatestExecutionId!.Value;

            // The test plays the human: satisfy the contract, then let round 2 settle it Succeeded --
            // BEFORE any arrest intent is ever marked, so this is genuinely the "already settled by
            // the time the mark drains" case, not a race with the seam's own settle.
            var outputDirectory = Path.Combine(artifactsRoot, $"execution_{hExecutionId}");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "revision.md"), "done", TestContext.Current.CancellationToken);
            var secondState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(StepStatus.Succeeded, secondState.Steps.Single().Status);

            // Marked directly on the same registry a live pump call will bind next -- this is the
            // registry-level seam in isolation, the same determinism seam
            // QuotaParkCancelArrestTests uses for the parked case.
            registry.MarkArrestIntent(hExecutionId, "test: already succeeded");

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            // Round 3: nothing left to dispatch, but the pump's very first round still drains and
            // evaluates the dangling mark before it can ever reach its own fixed-point return.
            var thirdState = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, thirdState.Status);
            Assert.Equal(StepStatus.Succeeded, thirdState.Steps.Single().Status);

            var stderrText = stderr.ToString();
            Assert.Contains(hExecutionId.Value, stderrText, StringComparison.Ordinal);
            Assert.Contains("already settled", stderrText, StringComparison.Ordinal);
            Assert.Contains("test: already succeeded", stderrText, StringComparison.Ordinal);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested);
            Assert.DoesNotContain(events, e => e is FlowEvent.ExecutionCancelled);

            // #1916 fix round 2: the drop's durable half -- reverting the
            // RecordCancellationRejectedAsync append in SettleArrestIntentsAsync leaves the stderr
            // assertions above still green, so this is the one assertion that actually pins it.
            var cancellationRejected = Assert.Single(events.OfType<FlowEvent.CancellationRejected>());
            Assert.Equal(hExecutionId, cancellationRejected.ExecutionId);
            Assert.Contains("already settled", cancellationRejected.Reason, StringComparison.Ordinal);
            Assert.Contains("test: already succeeded", cancellationRejected.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task An_arrest_intent_marked_for_an_execution_id_that_was_never_accepted_is_dropped_as_unknown()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var originalError = Console.Error;

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1556-drop-unknown"),
                new WorkflowTemplateId("template-1556-drop-unknown"),
                WorkflowTemplateVersion: 1,
                Steps: [new WorkflowStepDefinition(H, "human", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

            var bindings = new Dictionary<string, WorkerBinding> { ["human"] = new WorkerBinding.NonProcess(HumanContract) };
            var workflowId = new WorkflowId("wf-1556-drop-unknown");
            var stub = new StubCoreDispatcher();
            var bogusExecutionId = new ExecutionId("never-accepted");

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var registry = new InFlightExecutionRegistry();
            // Bound explicitly, same reasoning as the sibling "already settled" test above.
            registry.Bind(writer);
            registry.MarkArrestIntent(bogusExecutionId, "test: bogus target");

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            var state = await MutationInterface.StartWorkflowAsync(
                workflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub,
                inFlightExecutions: registry, cancellationToken: TestContext.Current.CancellationToken);

            // The real step still ran normally -- the bogus mark never touched it.
            Assert.Equal(StepStatus.Running, state.Steps.Single().Status);

            var stderrText = stderr.ToString();
            Assert.Contains(bogusExecutionId.Value, stderrText, StringComparison.Ordinal);
            Assert.Contains("unknown execution id", stderrText, StringComparison.Ordinal);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(events, e => e is FlowEvent.CancellationRequested);

            // #1916 fix round 2: same discriminating pin as the sibling test above, for the "unknown
            // execution id" reason instead of "already settled".
            var cancellationRejected = Assert.Single(events.OfType<FlowEvent.CancellationRejected>());
            Assert.Equal(bogusExecutionId, cancellationRejected.ExecutionId);
            Assert.Contains("unknown execution id", cancellationRejected.Reason, StringComparison.Ordinal);
            Assert.Contains("test: bogus target", cancellationRejected.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
