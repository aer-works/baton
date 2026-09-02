using System.Reflection;
using System.Text.Json;
using Baton.Domain;
using Baton.Store;

namespace Baton.Tests.Domain;

public class EnvironmentVariableSerializationTests
{
    [Fact]
    public void Deserializing_legacy_aerComputed_discriminator_yields_BatonComputed()
    {
        const string json = """{"kind":"aerComputed","Name":"BATON_INPUT_0","Value":"/artifacts/execution_1/goal.md"}""";

        var deserialized = JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options);

        var computed = Assert.IsType<EnvironmentVariable.BatonComputed>(deserialized);
        Assert.Equal("BATON_INPUT_0", computed.Name);
        Assert.Equal("/artifacts/execution_1/goal.md", computed.Value);
    }

    [Fact]
    public void Deserializing_current_batonComputed_discriminator_yields_BatonComputed()
    {
        const string json = """{"kind":"batonComputed","Name":"BATON_INPUT_0","Value":"/artifacts/execution_1/goal.md"}""";

        var deserialized = JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options);

        var computed = Assert.IsType<EnvironmentVariable.BatonComputed>(deserialized);
        Assert.Equal("BATON_INPUT_0", computed.Name);
        Assert.Equal("/artifacts/execution_1/goal.md", computed.Value);
    }

    [Fact]
    public void Deserializing_passThrough_discriminator_yields_PassThrough()
    {
        const string json = """{"kind":"passThrough","Name":"ANTHROPIC_API_KEY"}""";

        var deserialized = JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options);

        var passThrough = Assert.IsType<EnvironmentVariable.PassThrough>(deserialized);
        Assert.Equal("ANTHROPIC_API_KEY", passThrough.Name);
    }

    [Fact]
    public void Serializing_BatonComputed_emits_batonComputed_discriminator_never_aerComputed()
    {
        var variable = new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", "/artifacts/execution_2");

        var json = JsonSerializer.Serialize<EnvironmentVariable>(variable, FlowEventLogJson.Options);

        Assert.Contains("\"kind\":\"batonComputed\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":\"aerComputed\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserializing_legacy_ExecutionRequestAccepted_line_with_aerComputed_environment_replays_successfully()
    {
        var request = new ExecutionRequest(
            new ExecutionId("exec-1"),
            new WorkflowId("wf-1"),
            new StepId("step-1"),
            "claude",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/plan.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment:
            [
                new EnvironmentVariable.BatonComputed("BATON_OUTPUT_DIR", "/artifacts/execution_2"),
                new EnvironmentVariable.PassThrough("ANTHROPIC_API_KEY"),
            ],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        // Replace batonComputed with legacy aerComputed
        var legacyJson = currentJson.Replace("\"kind\":\"batonComputed\"", "\"kind\":\"aerComputed\"", StringComparison.Ordinal);
        Assert.NotEqual(currentJson, legacyJson);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(legacyJson, FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(2, accepted.Request.Environment.Count);
        var computed = Assert.IsType<EnvironmentVariable.BatonComputed>(accepted.Request.Environment[0]);
        Assert.Equal("BATON_OUTPUT_DIR", computed.Name);
        Assert.Equal("/artifacts/execution_2", computed.Value);
        var passThrough = Assert.IsType<EnvironmentVariable.PassThrough>(accepted.Request.Environment[1]);
        Assert.Equal("ANTHROPIC_API_KEY", passThrough.Name);
    }

    [Fact]
    public void Deserializing_unknown_discriminator_throws()
    {
        const string json = """{"kind":"somethingElse","Name":"FOO"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options));
    }

    [Theory]
    [InlineData("""{"kind":"batonComputed","Value":"/path"}""")]
    [InlineData("""{"kind":"aerComputed","Value":"/path"}""")]
    [InlineData("""{"kind":"passThrough"}""")]
    public void Deserializing_without_Name_property_throws_JsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options));
    }

    [Theory]
    [InlineData("""{"kind":"batonComputed","Name":"FOO"}""")]
    [InlineData("""{"kind":"aerComputed","Name":"FOO"}""")]
    public void Deserializing_computed_without_Value_property_throws_JsonException(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options));
    }

    [Fact]
    public void Deserializing_missing_kind_discriminator_throws_JsonException()
    {
        const string json = """{"Name":"FOO","Value":"BAR"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentVariable>(json, FlowEventLogJson.Options));
    }

    [Fact]
    public void Deserializing_as_specific_derived_type_checks_type_compatibility()
    {
        const string computedJson = """{"kind":"aerComputed","Name":"FOO","Value":"BAR"}""";
        var computed = JsonSerializer.Deserialize<EnvironmentVariable.BatonComputed>(computedJson, FlowEventLogJson.Options);
        Assert.NotNull(computed);
        Assert.Equal("FOO", computed.Name);

        const string passThroughJson = """{"kind":"passThrough","Name":"FOO"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EnvironmentVariable.BatonComputed>(passThroughJson, FlowEventLogJson.Options));
    }

    [Fact]
    public void Serializing_PassThrough_emits_passThrough_discriminator()
    {
        var variable = new EnvironmentVariable.PassThrough("ANTHROPIC_API_KEY");

        var json = JsonSerializer.Serialize<EnvironmentVariable>(variable, FlowEventLogJson.Options);

        Assert.Contains("\"kind\":\"passThrough\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"ANTHROPIC_API_KEY\"", json, StringComparison.Ordinal);
    }

    // The converter's Write hand-enumerates each arm's members (EnvironmentVariableJsonConverter.cs).
    // A member added to a record without a matching Write branch would compile and round-trip
    // silently, because Read builds each arm by hand too — this pins the emitted property set
    // against the constructor's, so an added member fails here instead of vanishing on every
    // journal write.
    [Fact]
    public void Write_emits_every_constructor_parameter_for_each_EnvironmentVariable_arm()
    {
        var arms = typeof(EnvironmentVariable).GetNestedTypes()
            .Where(t => typeof(EnvironmentVariable).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var arm in arms)
        {
            var ctor = arm.GetConstructors().Single();
            var parameters = ctor.GetParameters();
            var instance = (EnvironmentVariable)ctor.Invoke(
                parameters.Select(p => (object)$"test-{p.Name}").ToArray());

            var json = JsonSerializer.Serialize(instance, typeof(EnvironmentVariable), FlowEventLogJson.Options);
            using var doc = JsonDocument.Parse(json);

            var emitted = doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => !string.Equals(name, "kind", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var expected = parameters.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.True(
                expected.SetEquals(emitted),
                $"{arm.Name}: Write emitted [{string.Join(", ", emitted)}] but the constructor declares [{string.Join(", ", expected)}].");
        }
    }
}
