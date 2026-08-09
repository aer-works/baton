using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Tests.Domain;

public class RoomEventSerializationTests
{
    private static readonly HeldWorkRef LaneRef = new("lanes/lane-1");
    private const string CitedSubject = "exec-lane-1";

    public static TheoryData<RoomEvent> AllRoomEventVariants() =>
    [
        new RoomEvent.HeldWorkDispatched(LaneRef, "shape-flow", TimeSpan.FromMinutes(10), "operator-alice"),
        new RoomEvent.HeldWorkEscalated(LaneRef, "operator-bob"),
        new RoomEvent.HeldWorkResolved(LaneRef, new HeldWorkCitation(CitedSubject, "executionSucceeded", 1)),
        new RoomEvent.TurnHostDormancyEntered(3, DateTimeOffset.UtcNow),
        new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow),
        new RoomEvent.RuntimePermissionAsked("req-1", new ExecutionId("ex-1"), new StepId("st-1"), "w-1", "claude", "corr-1", "ReadFiles", "{}", "ReadFiles", DateTimeOffset.UtcNow),
        new RoomEvent.RuntimePermissionAnswered("req-1", "AllowOnce", "{}", "ok", "op-1", DateTimeOffset.UtcNow),
        new RoomEvent.RuntimePermissionRevoked("req-1", "timeout", DateTimeOffset.UtcNow),
    ];

    [Theory]
    [MemberData(nameof(AllRoomEventVariants))]
    public void RoundTrips_through_RoomEvent_base_type_without_data_loss(RoomEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(RoomEvent), FlowEventLogJson.Options);
        var deserialized = JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options);

        Assert.NotNull(deserialized);
        var reserialized = JsonSerializer.Serialize(deserialized, typeof(RoomEvent), FlowEventLogJson.Options);

        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void TurnHostDormancy_Projects_IsDormant_State()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new RoomEvent[]
        {
            new RoomEvent.TurnHostDormancyEntered(3, now),
        };
        var state1 = RoomProjector.Project(events);
        Assert.True(state1.IsDormant);

        var events2 = new RoomEvent[]
        {
            new RoomEvent.TurnHostDormancyEntered(3, now),
            new RoomEvent.TurnHostDormancyCleared("operator", now.AddMinutes(1)),
        };
        var state2 = RoomProjector.Project(events2);
        Assert.False(state2.IsDormant);
    }

    [Fact]
    public void Deserializing_unknown_eventType_discriminator_throws()
    {
        const string json = """{"eventType":"unknownRoomEvent"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoomEvent>(json, FlowEventLogJson.Options));
    }
}
