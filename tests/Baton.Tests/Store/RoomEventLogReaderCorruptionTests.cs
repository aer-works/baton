using Baton.Domain;
using Baton.Store;
using Baton.Tests.Shared;
using Xunit;

namespace Baton.Tests.Store;

public class RoomEventLogReaderCorruptionTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;

    public RoomEventLogReaderCorruptionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_room_reader_corruption_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_tempDirectory);
        }
    }

    [Fact]
    public async Task A_polymorphic_line_missing_its_kind_discriminator_fails_replay_as_a_read_exception_not_a_raw_NSE()
    {
        // System.Text.Json throws NotSupportedException -- not JsonException -- for a polymorphic
        // abstract payload with no type discriminator (the EscalationSubject "kind" here). The
        // reader's loud-replay contract is FlowEventLogReadException for ANY malformed line, so
        // that wart must be wrapped like every other parse failure, never propagated raw.
        var valid = new RoomEvent.EscalationRaised(
            new WorkerId("w-1"), EscalationTrigger.Spend,
            new EscalationSubject.Decision(new DecisionId("d-1")), DateTimeOffset.UtcNow);
        await using (var writer = new RoomEventLogWriter(_roomLogPath))
        {
            await writer.AppendAsync(valid, TestContext.Current.CancellationToken);
        }

        var text = await File.ReadAllTextAsync(_roomLogPath, TestContext.Current.CancellationToken);
        var corrupted = text.Replace("\"kind\":\"decision\",", string.Empty);
        Assert.NotEqual(text, corrupted);
        await File.WriteAllTextAsync(_roomLogPath, corrupted, TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await Assert.ThrowsAsync<FlowEventLogReadException>(
            () => reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
    }
}
