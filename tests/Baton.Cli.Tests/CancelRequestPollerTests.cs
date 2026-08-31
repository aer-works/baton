using Baton.Domain;
using Baton.Mutation;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495 second-reader finding: nothing exercised <see cref="CancelRequestPoller.TickAsync"/> directly
/// before this file. Covers the too-late no-op (a request naming an execution that is not currently
/// registered must be consumed, not left pending forever) and <see cref="CancelRequestPoller.RunAsync"/>'s
/// own resilience contract (a single tick's fault must never escape the loop).
/// </summary>
public class CancelRequestPollerTests
{
    private static readonly WorkflowDefinitionSnapshot Snapshot = new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("poller-test"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

    [Fact]
    public async Task A_request_naming_an_execution_not_currently_registered_is_consumed_as_a_too_late_no_op()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await CancelRequestFile.WriteAsync(roomDirectory, "not-currently-in-flight", TestContext.Current.CancellationToken);

            // An empty registry: nothing is registered under any ExecutionId, so
            // RequestCancellationAsync necessarily returns false (InFlightExecutionRegistry.cs:44-47) --
            // the too-late shape this test pins.
            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the request to be consumed, not left pending");
            Assert.True(File.Exists($"{requestPath}.consumed"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_survives_a_tick_that_throws_and_keeps_polling_until_cancelled()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            // A corrupt flow.jsonl: FlowEventLogReader.ReadAllAsync throws FlowEventLogReadException on
            // a malformed complete line (Baton/Store/FlowEventLogReader.cs) -- the poller's "latest"
            // branch hits this on every tick for as long as the request stays pending.
            await File.WriteAllTextAsync(logPath, "{ not valid json }\n", TestContext.Current.CancellationToken);
            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            pollCancellation.CancelAfter(TimeSpan.FromMilliseconds(1200));

            // Would throw FlowEventLogReadException out of RunAsync itself if a tick's fault were not
            // caught -- the assertion is simply that this completes at all (via the CancelAfter timeout)
            // rather than propagating.
            await CancelRequestPoller.RunAsync(
                roomDirectory, logPath, Snapshot, registry, TimeSpan.FromMilliseconds(150), pollCancellation.Token);

            // Still pending: every tick faulted before ever reaching Consume/Reject.
            Assert.True(File.Exists(CancelRequestFile.GetPath(roomDirectory)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
