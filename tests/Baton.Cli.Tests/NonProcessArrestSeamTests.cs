using System.Diagnostics;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Store;

namespace Baton.Cli.Tests;

/// <summary>
/// #1556 PR 2: the pump-side arrest seam for non-process work, reached through the SAME
/// <c>cancel.request</c> file channel #1495/#1563 already use.
/// <see cref="CancelRequestPoller.TickAsync"/> marks the intent
/// (<see cref="InFlightExecutionRegistry.MarkArrestIntent"/>) instead of falling through to the
/// bounded retry #1530 left it on, and the pump's own derived-obligation block
/// (<c>MutationInterface.SettleArrestIntentsAsync</c>) settles it within the pump's own next couple
/// of rounds — without ever waiting for a sibling <see cref="WorkerBinding.Process"/> dispatch to
/// complete first, proving the busy-wait's wake wiring fires for this general (non-parked) case, not
/// just the quota-parked one <c>Baton.Tests.Mutation.QuotaParkCancelArrestTests</c> already covers.
/// </summary>
public class NonProcessArrestSeamTests
{
    private static readonly StepId ProcessStep = new("p");
    private static readonly StepId NonProcessStep = new("n");
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    // A short, real OS process (not a stub) -- Baton.Cli.Tests has no StubCoreDispatcher of its own
    // (that determinism seam is Baton.Tests-internal), and a real sibling dispatch is exactly the
    // "something else keeps the pump alive" shape this seam needs: a lone non-process step with
    // nothing else in flight makes the pump return immediately, with no wait for a mark to wake.
    private static CoreDispatchTarget Sleep(TimeSpan duration) =>
        new("cmd", ["/c", $"ping -n {(int)duration.TotalSeconds + 1} 127.0.0.1 >nul"]);

    [Fact]
    public async Task A_running_non_process_step_arrested_via_the_cancel_request_file_settles_without_waiting_for_a_sibling_process_dispatch()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cli-arrest-seam-{Guid.NewGuid():N}");
        var artifactsRoot = Path.Combine(roomDirectory, "artifacts");
        var logPath = Path.Combine(roomDirectory, "flow.jsonl");

        try
        {
            var snapshot = new WorkflowDefinitionSnapshot(
                new WorkflowDefinitionSnapshotId("snapshot-1556-seam"),
                new WorkflowTemplateId("template-1556-seam"),
                WorkflowTemplateVersion: 1,
                Steps:
                [
                    new WorkflowStepDefinition(ProcessStep, "worker-p", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                    new WorkflowStepDefinition(NonProcessStep, "worker-n", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
                ]);

            var bindings = new Dictionary<string, WorkerBinding>
            {
                ["worker-p"] = new WorkerBinding.Process(
                    new WorkerContract("worker-p", [], [], []),
                    Sleep(TimeSpan.FromSeconds(6)),
                    TimeSpan.FromSeconds(30)),
                // A required, never-written output -- an empty contract's vacuously-satisfied outputs
                // would let NonProcessCompletionDetector settle n Succeeded on its very first
                // opportunity (completion beats arrest within a round, ruling Q2), before the mark
                // below could ever land in time; this keeps it genuinely Running until arrested.
                ["worker-n"] = new WorkerBinding.NonProcess(new WorkerContract("worker-n", [], [new ProducedOutput("never.txt")], [])),
            };

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);
            var registry = new InFlightExecutionRegistry();

            var pumpTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf-1556-seam"),
                roomDirectory,
                snapshot,
                bindings,
                artifactsRoot,
                reader,
                writer,
                dispatcher,
                inFlightExecutions: registry,
                cancellationToken: TestContext.Current.CancellationToken);

            // Both steps are ready from round 1 with no dependency between them -- poll for n's own
            // accept rather than assume ordering against p's (real, slower) OS process dispatch.
            var acceptStopwatch = Stopwatch.StartNew();
            ExecutionId? nExecutionId = null;
            while (nExecutionId is null)
            {
                var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
                nExecutionId = events.OfType<FlowEvent.ExecutionRequestAccepted>()
                    .Where(e => e.Request.StepId == NonProcessStep)
                    .Select(e => (ExecutionId?)e.Request.ExecutionId)
                    .FirstOrDefault();

                if (nExecutionId is not null)
                {
                    break;
                }

                Assert.True(acceptStopwatch.Elapsed < WaitTimeout, "Timed out waiting for n's own accept.");
                Assert.False(pumpTask.IsCompleted, "expected the pump still running p's sleep");
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: poll interval inside a 30s-bounded loop
            }

            await CancelRequestFile.WriteAsync(roomDirectory, nExecutionId.Value.Value, TestContext.Current.CancellationToken);

            // The real file-channel delivery point: marks the intent (no live process registered for
            // a non-process step) and wakes whichever pump wait p's still-live dispatch parks in.
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, snapshot, registry, TestContext.Current.CancellationToken);

            var settleStopwatch = Stopwatch.StartNew();
            while (true)
            {
                var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
                if (events.OfType<FlowEvent.ExecutionCancelled>().Any(e => e.ExecutionId == nExecutionId.Value))
                {
                    break;
                }

                Assert.True(settleStopwatch.Elapsed < WaitTimeout, "Timed out waiting for n's ExecutionCancelled.");
                // The money assertion: n settles WITHOUT p's (6s) sleep ever completing first --
                // proves the wake fired the pump's own next round rather than waiting for p.
                Assert.False(pumpTask.IsCompleted, "expected n to settle before p's sibling dispatch completes");
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: poll interval inside a 30s-bounded loop
            }

            var finalState = await pumpTask.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);
            Assert.Equal(StepStatus.Succeeded, finalState.Steps.Single(s => s.StepId == ProcessStep).Status);
            Assert.Equal(StepStatus.Cancelled, finalState.Steps.Single(s => s.StepId == NonProcessStep).Status);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            // Never consumed by the single TickAsync call above -- marking (not yet settled at that
            // instant) leaves the file pending; a later, real poll loop is what would consume it once
            // it observes the settle. Not this test's own scope (CancelRequestPollerTests already
            // pins that consume-on-settle branch).
            Assert.True(File.Exists(requestPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
