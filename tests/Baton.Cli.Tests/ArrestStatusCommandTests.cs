using System.Text.Json;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli.Tests;

public class ArrestStatusCommandTests
{
    [Fact]
    public async Task Status_text_and_json_include_the_room_arrest_ledger()
    {
        var room = Path.Combine(Path.GetTempPath(), $"arrest-status-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(room);
            var step = new WorkflowStepDefinition(new StepId("step"), "worker", [], [], [], new RetryPolicy(1));
            var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(new WorkflowTemplateId("arrest-status"), 1, [step]));
            await SnapshotBinder.PersistAsync(snapshot, Path.Combine(room, BatonPaths.SnapshotFileName), TestContext.Current.CancellationToken);

            var executionId = new ExecutionId("execution-arrest-status");
            var request = new ExecutionRequest(
                executionId,
                new WorkflowId("arrest-status"),
                step.StepId,
                step.Worker,
                [],
                [],
                TimeSpan.FromMinutes(1),
                [],
                new Dictionary<StepId, ExecutionId>());

            await using (var flowWriter = new FlowEventLogWriter(Path.Combine(room, BatonPaths.FlowLogFileName)))
            {
                await flowWriter.AppendAsync(
                    new FlowEvent.ExecutionRequestAccepted(request),
                    TestContext.Current.CancellationToken);
            }

            var requestedAt = new DateTimeOffset(2026, 9, 5, 16, 0, 0, TimeSpan.Zero);
            await using (var roomWriter = new RoomEventLogWriter(Path.Combine(room, BatonPaths.RoomLogFileName)))
            {
                await roomWriter.AppendAsync(
                    new RoomEvent.ArrestRequested("request-status", "latest", "cli", requestedAt),
                    TestContext.Current.CancellationToken);
                await roomWriter.AppendAsync(
                    new RoomEvent.ArrestRejected("request-status", executionId, "ambiguous target", requestedAt.AddSeconds(1)),
                    TestContext.Current.CancellationToken);
            }

            var text = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(room), text, TestContext.Current.CancellationToken);

            Assert.Contains("Arrests:", text.ToString());
            Assert.Contains("rejected: request=request-status", text.ToString());
            Assert.Contains("askedBy=cli", text.ToString());
            Assert.Contains("requestedAt=", text.ToString());
            Assert.Contains("terminalAt=", text.ToString());
            Assert.Contains("reason=ambiguous target", text.ToString());

            var json = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(room, Json: true), json, TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(json.ToString());
            var arrest = Assert.Single(document.RootElement.GetProperty("arrests").EnumerateArray());
            Assert.Equal("request-status", arrest.GetProperty("requestId").GetString());
            Assert.Equal("rejected", arrest.GetProperty("state").GetString());
            Assert.Equal("execution-arrest-status", arrest.GetProperty("executionId").GetString());
            Assert.Equal("cli", arrest.GetProperty("requestedBy").GetString());
            Assert.Equal(requestedAt, arrest.GetProperty("requestedAt").GetDateTimeOffset());
            Assert.Equal(requestedAt.AddSeconds(1), arrest.GetProperty("rejectedAt").GetDateTimeOffset());
            Assert.Equal("ambiguous target", arrest.GetProperty("reason").GetString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(room);
        }
    }

    [Fact]
    public void Arrest_state_sets_cover_every_terminal_and_pending_outcome()
    {
        Assert.Equal(
            [ArrestLedgerStates.Delivered, ArrestLedgerStates.Expired, ArrestLedgerStates.Rejected, ArrestLedgerStates.Requested],
            ArrestLedgerStates.All.OrderBy(state => state, StringComparer.Ordinal));
        Assert.Equal(
            [ArrestLedgerStates.Delivered, ArrestLedgerStates.Expired, ArrestLedgerStates.Rejected],
            ArrestLedgerStates.Terminal.OrderBy(state => state, StringComparer.Ordinal));
        Assert.False(ArrestLedgerStates.IsTerminal(ArrestLedgerStates.Requested));
        Assert.True(ArrestLedgerStates.IsTerminal(ArrestLedgerStates.Delivered));
        Assert.True(ArrestLedgerStates.IsTerminal(ArrestLedgerStates.Rejected));
        Assert.True(ArrestLedgerStates.IsTerminal(ArrestLedgerStates.Expired));
    }
}