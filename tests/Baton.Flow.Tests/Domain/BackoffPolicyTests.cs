using System.Text.Json;
using Baton.Flow.Domain;
using Baton.Flow.Templates;

namespace Baton.Flow.Tests.Domain;

public class BackoffPolicyTests
{
    [Theory]
    [InlineData(1, 0.0, 500)]
    [InlineData(1, 0.5, 750)]
    [InlineData(1, 1.0, 1000)]
    [InlineData(2, 1.0, 3000)]
    [InlineData(3, 1.0, 9000)]
    [InlineData(4, 1.0, 27000)]
    [InlineData(5, 0.0, 30000)]
    [InlineData(5, 1.0, 60000)]
    [InlineData(6, 1.0, 60000)]
    public void Steady_policy_delay_for_table_verification(int attempt, double sample, double expectedMs)
    {
        var delay = BackoffPolicy.Steady.DelayFor(attempt, sample);
        Assert.Equal(expectedMs, delay.TotalMilliseconds);
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(1, 0.5)]
    [InlineData(1, 1.0)]
    [InlineData(5, 0.0)]
    [InlineData(5, 1.0)]
    public void None_preset_yields_zero_always(int attempt, double sample)
    {
        var delay = BackoffPolicy.None.DelayFor(attempt, sample);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Theory]
    [InlineData(1, 0.0, 100)]
    [InlineData(1, 1.0, 200)]
    [InlineData(6, 0.0, 2500)]
    [InlineData(6, 1.0, 5000)]
    public void Brisk_preset_growth_and_clamping(int attempt, double sample, double expectedMs)
    {
        var delay = BackoffPolicy.Brisk.DelayFor(attempt, sample);
        Assert.Equal(expectedMs, delay.TotalMilliseconds);
    }

    [Fact]
    public void Deserialization_omitted_backoff_defaults_to_steady()
    {
        const string json = """{ "MaxAttempts": 3 }""";
        var policy = JsonSerializer.Deserialize<RetryPolicy>(json);
        Assert.NotNull(policy);
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(BackoffPolicy.Steady, policy.Backoff);
    }

    [Fact]
    public void Deserialization_preset_string_names_resolve_correctly()
    {
        const string jsonNone = """{ "MaxAttempts": 2, "Backoff": "none" }""";
        var policyNone = JsonSerializer.Deserialize<RetryPolicy>(jsonNone);
        Assert.NotNull(policyNone);
        Assert.Equal(BackoffPolicy.None, policyNone.Backoff);

        const string jsonBrisk = """{ "MaxAttempts": 2, "Backoff": "BRISK" }""";
        var policyBrisk = JsonSerializer.Deserialize<RetryPolicy>(jsonBrisk);
        Assert.NotNull(policyBrisk);
        Assert.Equal(BackoffPolicy.Brisk, policyBrisk.Backoff);

        const string jsonPatient = """{ "MaxAttempts": 2, "Backoff": "patient" }""";
        var policyPatient = JsonSerializer.Deserialize<RetryPolicy>(jsonPatient);
        Assert.NotNull(policyPatient);
        Assert.Equal(BackoffPolicy.Patient, policyPatient.Backoff);
    }

    [Fact]
    public void Deserialization_object_form_deserializes_exact_values()
    {
        const string json = """
            {
              "MaxAttempts": 4,
              "Backoff": { "InitialMs": 5000, "Multiplier": 3, "MaxMs": 900000, "Jitter": "half" }
            }
            """;

        var policy = JsonSerializer.Deserialize<RetryPolicy>(json);
        Assert.NotNull(policy);
        Assert.Equal(4, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Backoff.Initial);
        Assert.Equal(3, policy.Backoff.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(15), policy.Backoff.Cap);
        Assert.Equal(JitterMode.Half, policy.Backoff.Jitter);
        Assert.Equal(BackoffPolicy.Patient, policy.Backoff);
    }

    [Fact]
    public void Deserialization_unknown_preset_throws_load_error_naming_field_bad_value_and_valid_set()
    {
        const string templateJson = """
            {
              "WorkflowTemplateId": "typo-test",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                {
                  "StepId": "step1",
                  "Worker": "test",
                  "Inputs": [],
                  "Outputs": [],
                  "DependsOn": [],
                  "RetryPolicy": { "MaxAttempts": 3, "Backoff": "pateint" }
                }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(templateJson));

        var msg = ex.ToString();
        Assert.Contains("Backoff", msg);
        Assert.Contains("pateint", msg);
        Assert.Contains("none", msg);
        Assert.Contains("brisk", msg);
        Assert.Contains("steady", msg);
        Assert.Contains("patient", msg);
    }

    [Fact]
    public void Serialization_and_deserialization_round_trips_preserves_policy()
    {
        var originalBrisk = new RetryPolicy(4, BackoffPolicy.Brisk);
        var jsonBrisk = JsonSerializer.Serialize(originalBrisk);
        var roundTrippedBrisk = JsonSerializer.Deserialize<RetryPolicy>(jsonBrisk);
        Assert.Equal(originalBrisk, roundTrippedBrisk);

        var customBackoff = new BackoffPolicy(TimeSpan.FromMilliseconds(350), 2.5, TimeSpan.FromSeconds(12), JitterMode.None);
        var originalCustom = new RetryPolicy(5, customBackoff);
        var jsonCustom = JsonSerializer.Serialize(originalCustom);
        var roundTrippedCustom = JsonSerializer.Deserialize<RetryPolicy>(jsonCustom);
        Assert.Equal(originalCustom, roundTrippedCustom);
    }

    [Fact]
    public void Fixture_workflow_json_with_only_max_attempts_parses_and_defaults_backoff_to_steady()
    {
        const string fixtureJson = """
            {
              "WorkflowTemplateId": "flaky-retry",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                {
                  "StepId": "flaky",
                  "Worker": "flaky",
                  "Inputs": [],
                  "Outputs": ["result"],
                  "DependsOn": [],
                  "RetryPolicy": { "MaxAttempts": 2 }
                }
              ]
            }
            """;

        var definition = WorkflowDefinitionParser.Parse(fixtureJson);
        Assert.NotNull(definition);
        var step = Assert.Single(definition.Steps);
        Assert.Equal(new RetryPolicy(2), step.RetryPolicy);
        Assert.Equal(BackoffPolicy.Steady, step.RetryPolicy.Backoff);
    }
}
