using System.Text.Json;
using Baton.Flow.Domain;

namespace Baton.Flow.Tests.Domain;

public class JsonScalarSerializationTests
{
    public static IEnumerable<object[]> AllVariants()
    {
        yield return [new JsonScalar.String("approved"), "\"approved\""];
        yield return [new JsonScalar.Number(80), "80"];
        yield return [new JsonScalar.Boolean(true), "true"];
        yield return [JsonScalar.Null.Instance, "null"];
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void Serializes_as_a_raw_JSON_literal(JsonScalar scalar, string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(scalar));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void RoundTrips_without_data_loss(JsonScalar original, string json)
    {
        var deserialized = JsonSerializer.Deserialize<JsonScalar>(json);
        Assert.NotNull(deserialized);

        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void OutputCondition_serializes_its_comparison_value_under_the_spec_field_name()
    {
        var condition = new OutputCondition("/status", new JsonScalar.String("approved"));

        var json = JsonSerializer.Serialize(condition);

        Assert.Contains("\"equals\":\"approved\"", json);
    }
}
