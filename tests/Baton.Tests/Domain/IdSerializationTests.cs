using System.Text.Json;
using Baton.Domain;

namespace Baton.Tests.Domain;

public class IdSerializationTests
{
    [Fact]
    public void ExecutionId_serializes_as_a_plain_string_not_a_wrapper_object()
    {
        var id = new ExecutionId("exec-1");

        Assert.Equal("\"exec-1\"", JsonSerializer.Serialize(id));
        Assert.Equal(id, JsonSerializer.Deserialize<ExecutionId>("\"exec-1\""));
    }

    [Fact]
    public void StepId_is_usable_as_a_dictionary_key_and_round_trips()
    {
        var map = new Dictionary<StepId, ExecutionId> { [new StepId("architect")] = new ExecutionId("exec-0") };

        var json = JsonSerializer.Serialize(map);
        var deserialized = JsonSerializer.Deserialize<Dictionary<StepId, ExecutionId>>(json);
        Assert.NotNull(deserialized);

        Assert.Equal("{\"architect\":\"exec-0\"}", json);
        Assert.Equal(map, deserialized);
    }
}
