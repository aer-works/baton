using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;

namespace Aer.Ui.Tests;

/// <summary>
/// Proves that <see cref="RoomProjectionLoader.LoadAsync"/> projects step pause and decision moments
/// from <c>flow.jsonl</c> against a real room directory on disk (#1197).
/// </summary>
public class RoomProjectionLoaderDecisionTests
{
    [Fact]
    public async Task LoadAsync_carries_pause_and_decision_moments_in_order_with_pairing()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-loader-dec-{Guid.NewGuid():N}");
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");

            var exec1 = new ExecutionId("exec-step-1");
            var exec2 = new ExecutionId("exec-step-2");
            var step1 = new StepId("architect");
            var step2 = new StepId("critic");
            var decId = new DecisionId("dec-1");

            // Written by the REAL writer, deliberately: hand-rolled journal lines would pin a shape
            // production may not emit, and this fact's whole job is that what the writer writes is
            // what the loader reads. The cost is that the instants are the writer's own, so the
            // assertions below are about presence, order and pairing rather than exact values.
            var before = DateTimeOffset.UtcNow;
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.WorkflowPaused(exec1, step1), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.WorkflowPaused(exec2, step2), TestContext.Current.CancellationToken);
                await writer.AppendAsync(
                    new FlowEvent.ExternalDecisionRecorded(decId, exec1, DecisionType.Resume, null, null, null),
                    TestContext.Current.CancellationToken);
            }

            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            // Both pauses, in the order the journal carries them.
            Assert.Equal(2, projection.StepPauseMoments.Count);
            var p1 = projection.StepPauseMoments[0];
            Assert.Equal(exec1, p1.ExecutionId);
            Assert.Equal(step1, p1.StepId);
            Assert.NotNull(p1.PausedAt);
            Assert.InRange(p1.PausedAt!.Value, before, DateTimeOffset.UtcNow);

            var p2 = projection.StepPauseMoments[1];
            Assert.Equal(exec2, p2.ExecutionId);
            Assert.Equal(step2, p2.StepId);
            Assert.True(p2.PausedAt >= p1.PausedAt, "The second pause must not project as earlier than the first.");

            // The decision pairs with the execution it was recorded against — exec1's pause, not
            // exec2's, which is what a transcript needs to place it beside the right step.
            var d1 = Assert.Single(projection.RecordedDecisionMoments);
            Assert.Equal(decId, d1.DecisionId);
            Assert.Equal(exec1, d1.ReferencedExecutionId);
            Assert.NotEqual(exec2, d1.ReferencedExecutionId);
            Assert.Equal(DecisionType.Resume, d1.DecisionType);
            Assert.Null(d1.TargetStepId);
            Assert.NotNull(d1.RecordedAt);
            Assert.True(d1.RecordedAt >= p2.PausedAt, "The decision must not project as earlier than the pause it answers.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task LoadAsync_tolerates_journal_line_with_no_writer_timestamp()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-loader-dec-nullts-{Guid.NewGuid():N}");
        try
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-step-linear-workflow.json");
            var definition = await WorkflowDefinitionParser.LoadFromFileAsync(fixturePath, TestContext.Current.CancellationToken);
            var snapshot = SnapshotBinder.Bind(definition);
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");

            var exec1 = new ExecutionId("exec-step-1");
            var step1 = new StepId("architect");
            var decId = new DecisionId("dec-1");

            // Hand-rolled here, unlike the fact above, because the writer always stamps: a line
            // without one can only come from a journal older than the stamp, and the point is that
            // such a room still loads. The reader is asserted directly first so a shape this test
            // invented — rather than the loader's handling — cannot be what passes.
            var line1 = $"{{\"owner\":\"flow\",\"Event\":{{\"eventType\":\"workflowPaused\",\"ExecutionId\":\"{exec1.Value}\",\"StepId\":\"{step1.Value}\"}}}}\n";
            var line2 = $"{{\"owner\":\"flow\",\"Event\":{{\"eventType\":\"externalDecisionRecorded\",\"DecisionId\":\"{decId.Value}\",\"ReferencedExecutionId\":\"{exec1.Value}\",\"DecisionType\":\"Resume\",\"TargetStepId\":null,\"SupplementaryExecutionId\":null,\"Decider\":null}}}}\n";

            await File.WriteAllTextAsync(logPath, line1 + line2, TestContext.Current.CancellationToken);

            // Verify FlowEventLogReader parses these lines honestly
            var reader = new FlowEventLogReader(logPath);
            var entries = await reader.ReadAllEntriesWithTimestampsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, entries.Count);

            // Verify RoomProjectionLoader.LoadAsync tolerates null timestamp without throwing
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectory, TestContext.Current.CancellationToken);

            var p1 = Assert.Single(projection.StepPauseMoments);
            Assert.Equal(exec1, p1.ExecutionId);
            Assert.Equal(step1, p1.StepId);
            Assert.Null(p1.PausedAt);

            var d1 = Assert.Single(projection.RecordedDecisionMoments);
            Assert.Equal(decId, d1.DecisionId);
            Assert.Equal(exec1, d1.ReferencedExecutionId);
            Assert.Null(d1.RecordedAt);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
