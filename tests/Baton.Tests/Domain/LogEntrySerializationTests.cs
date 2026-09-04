using System.Text.Json;
using Baton.Domain;

using Baton.Store;

namespace Baton.Tests.Domain;

public class LogEntrySerializationTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");

    public static IEnumerable<object[]> AllEntryVariants()
    {
        yield return [new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId))];
        yield return [new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, Pid: 99))];
        yield return [new LogEntry.RoomLogEntry(new RoomEvent.HeldWorkDispatched(new HeldWorkRef("lane-1"), "shape-1", TimeSpan.FromMinutes(5), "decider-1"))];
    }

    [Theory]
    [MemberData(nameof(AllEntryVariants))]
    public void RoundTrips_through_the_LogEntry_base_type_without_data_loss(LogEntry original)
    {
        var json = JsonSerializer.Serialize(original, typeof(LogEntry), FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<LogEntry>(json, FlowEventLogJson.Options);
        Assert.NotNull(deserialized);

        var reserialized = JsonSerializer.Serialize(deserialized, typeof(LogEntry), FlowEventLogJson.Options);
        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void FlowLogEntry_CoreLogEntry_and_RoomLogEntry_serialize_with_distinct_owner_discriminators()
    {
        var flowJson = JsonSerializer.Serialize(
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId)), typeof(LogEntry), FlowEventLogJson.Options);
        var coreJson = JsonSerializer.Serialize(
            new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(ExecutionId, Pid: 1)), typeof(LogEntry), FlowEventLogJson.Options);
        var roomJson = JsonSerializer.Serialize(
            new LogEntry.RoomLogEntry(new RoomEvent.HeldWorkDispatched(new HeldWorkRef("lane-1"), "shape-1", TimeSpan.FromMinutes(5), "decider-1")), typeof(LogEntry), FlowEventLogJson.Options);

        Assert.Contains("\"owner\":\"flow\"", flowJson);
        Assert.Contains("\"owner\":\"core\"", coreJson);
        Assert.Contains("\"owner\":\"room\"", roomJson);
    }


    /// <summary>
    /// The <c>owner</c> counterpart of <see cref="FlowEventSerializationTests.Deserializing_an_unknown_event_type_discriminator_does_not_throw"/>
    /// -- see that test's remarks for the #1779 rationale, shared verbatim one layer up the union.
    /// </summary>
    [Fact]
    public void Deserializing_an_unknown_owner_discriminator_does_not_throw()
    {
        const string json = """{"owner":"somethingElse"}""";

        var deserialized = FlowEventLogJson.DeserializeLine(json);

        var unknown = Assert.IsType<LogEntry.UnknownLogEntry>(deserialized);
        Assert.Equal("somethingElse", unknown.Owner);
    }

    /// <summary>
    /// Polarity control for the test above: a KNOWN owner ("flow") whose payload is missing a required
    /// member still throws -- "loud beats silent" is unchanged for that case, only for a genuinely
    /// unknown discriminator (#1779).
    /// </summary>
    [Fact]
    public void Deserializing_a_known_owner_with_a_malformed_event_still_throws()
    {
        const string json = """{"owner":"flow","Event":{"eventType":"executionFailed"}}""";

        Assert.Throws<JsonException>(() => FlowEventLogJson.DeserializeLine(json));
    }

    [Fact]
    public void FlowLogEntry_with_WriterUtcTimestamp_roundtrips()
    {
        var timestamp = new DateTime(2026, 7, 30, 12, 30, 45, DateTimeKind.Utc);
        var original = new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId), timestamp);

        var json = JsonSerializer.Serialize(original, typeof(LogEntry), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<LogEntry>(json, FlowEventLogJson.Options) as LogEntry.FlowLogEntry;

        Assert.NotNull(deserialized);
        Assert.Equal(original.Event, deserialized.Event);
        Assert.Equal(timestamp, deserialized.WriterUtcTimestamp);
    }

    [Fact]
    public void FlowLogEntry_without_WriterUtcTimestamp_roundtrips_as_null()
    {
        var original = new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId));

        var json = JsonSerializer.Serialize(original, typeof(LogEntry), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<LogEntry>(json, FlowEventLogJson.Options) as LogEntry.FlowLogEntry;

        Assert.NotNull(deserialized);
        Assert.Equal(original.Event, deserialized.Event);
        Assert.Null(deserialized.WriterUtcTimestamp);
    }

    [Fact]
    public void Old_journal_line_without_WriterUtcTimestamp_deserializes()
    {
        // Serialize an entry without a timestamp, then remove the WriterUtcTimestamp field
        // to simulate an old journal line (backward compatibility).
        var entryWithoutTimestamp = new LogEntry.FlowLogEntry(new FlowEvent.ExecutionSucceeded(ExecutionId));
        var json = JsonSerializer.Serialize(entryWithoutTimestamp, typeof(LogEntry), FlowEventLogJson.Options);

        // The JSON should deserialize even if WriterUtcTimestamp is missing in old journal lines
        var deserialized = JsonSerializer.Deserialize<LogEntry>(json, FlowEventLogJson.Options) as LogEntry.FlowLogEntry;

        Assert.NotNull(deserialized);
        var succeeded = Assert.IsType<FlowEvent.ExecutionSucceeded>(deserialized.Event);
        Assert.Equal("exec-1", succeeded.ExecutionId.Value);
        Assert.Null(deserialized.WriterUtcTimestamp);
    }
}
