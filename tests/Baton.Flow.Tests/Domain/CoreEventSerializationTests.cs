using System.Text.Json;
using Baton.Flow.Domain;

using Baton.Flow.Store;

namespace Baton.Flow.Tests.Domain;

public class CoreEventSerializationTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");

    public static IEnumerable<object[]> AllEventVariants()
    {
        yield return [new CoreEvent.ExecutionStarted(ExecutionId, Pid: 4242)];
        yield return [new CoreEvent.ExecutionExited(ExecutionId, ExitCode: 0, CoreExitReason.Natural)];
        yield return [new CoreEvent.ExecutionExited(ExecutionId, ExitCode: -1, CoreExitReason.TimedOut)];
        yield return [new CoreEvent.ExecutionExited(ExecutionId, ExitCode: -1, CoreExitReason.CancelRequested)];
    }

    [Theory]
    [MemberData(nameof(AllEventVariants))]
    public void RoundTrips_through_the_CoreEvent_base_type_without_data_loss(CoreEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(CoreEvent), FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<CoreEvent>(json, FlowEventLogJson.Options);
        Assert.NotNull(deserialized);

        var reserialized = JsonSerializer.Serialize(deserialized, typeof(CoreEvent), FlowEventLogJson.Options);
        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void Deserializing_an_unknown_event_type_discriminator_throws()
    {
        const string json = """{"eventType":"somethingElse"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CoreEvent>(json, FlowEventLogJson.Options));
    }
}
