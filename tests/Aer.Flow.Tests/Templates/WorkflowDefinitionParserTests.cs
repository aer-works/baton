using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Templates;

namespace Aer.Flow.Tests.Templates;

public class WorkflowDefinitionParserTests
{
    private static WorkflowDefinition ThreeStepLinearDefinition() => new(
        new WorkflowTemplateId("architect-critic-synth"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                new StepId("architect"),
                "architect",
                Inputs: ["goal"],
                Outputs: ["plan"],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(MaxAttempts: 3)),
            new WorkflowStepDefinition(
                new StepId("critic"),
                "critic",
                Inputs: ["plan"],
                Outputs: ["review"],
                DependsOn: [new StepId("architect")],
                RetryPolicy: new RetryPolicy(MaxAttempts: 1),
                PausePoint: new PausePoint(SupersedeTargets: [new StepId("architect")])),
            new WorkflowStepDefinition(
                new StepId("synth"),
                "synth",
                Inputs: ["review"],
                Outputs: ["result"],
                DependsOn: [new StepId("critic")],
                RetryPolicy: new RetryPolicy(MaxAttempts: 1)),
        ]);

    [Fact]
    public void A_valid_three_step_definition_parses_successfully()
    {
        var json = JsonSerializer.Serialize(ThreeStepLinearDefinition());

        var parsed = WorkflowDefinitionParser.Parse(json);

        Assert.Equal(3, parsed.Steps.Count);
        Assert.Equal("architect-critic-synth", parsed.WorkflowTemplateId.Value);
    }

    [Fact]
    public async Task LoadFromFileAsync_reads_and_parses_a_template_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ThreeStepLinearDefinition()), TestContext.Current.CancellationToken);
        try
        {
            var parsed = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(3, parsed.Steps.Count);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void Malformed_json_is_rejected_with_a_clear_error()
    {
        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse("{ not valid json"));

        Assert.Contains(ex.Errors, e => e.Contains("Malformed"));
    }

    [Fact]
    public void A_string_WorkflowTemplateVersion_names_the_expected_int_shape()
    {
        // #562: a hand-authored template quoting the version ("1.0.0" instead of 1) used to
        // surface System.Text.Json's raw converter message with no guidance toward the fix.
        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(
            """{"WorkflowTemplateId":"x","WorkflowTemplateVersion":"1.0.0","Steps":[]}"""));

        Assert.Contains(ex.Errors, e => e.Contains("WorkflowTemplateVersion") && e.Contains("integer") && e.Contains("not a quoted string"));
    }

    [Fact]
    public void An_object_Inputs_names_the_expected_array_shape()
    {
        // #562: "Inputs": {} instead of [] used to surface a raw IReadOnlyList`1 converter message.
        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(
            """{"WorkflowTemplateId":"x","WorkflowTemplateVersion":1,"Steps":[{"StepId":"a","Worker":"w","Inputs":{},"Outputs":[],"DependsOn":[],"RetryPolicy":{"MaxAttempts":1}}]}"""));

        Assert.Contains(ex.Errors, e => e.Contains("Steps[0].Inputs") && e.Contains("array of strings") && e.Contains("not an object"));
    }

    [Fact]
    public async Task LoadFromFileAsync_names_the_file_in_a_malformed_json_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ not valid json", TestContext.Current.CancellationToken);
        try
        {
            var ex = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
                () => WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken));

            Assert.Contains(ex.Errors, e => e.Contains(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void A_null_json_document_is_rejected()
    {
        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse("null"));

        Assert.Contains(ex.Errors, e => e.Contains("did not contain a WorkflowDefinition"));
    }

    [Fact]
    public void A_template_missing_the_steps_array_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        // System.Text.Json does not enforce non-nullable reference-typed record parameters by
        // default, so a template that simply omits "Steps" deserializes with Steps == null.
        var json = """{"WorkflowTemplateId":"x","WorkflowTemplateVersion":1}""";

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("Steps is missing"));
    }

    [Fact]
    public void A_step_missing_its_dependsOn_array_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                { "StepId": "a", "Worker": "worker", "Inputs": [], "Outputs": [], "RetryPolicy": { "MaxAttempts": 1 } }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("missing DependsOn"));
    }

    [Fact]
    public void A_pausePoint_missing_its_supersedeTargets_array_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                {
                  "StepId": "a",
                  "Worker": "worker",
                  "Inputs": [],
                  "Outputs": [],
                  "DependsOn": [],
                  "RetryPolicy": { "MaxAttempts": 1 },
                  "PausePoint": {}
                }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("missing SupersedeTargets"));
    }

    [Fact]
    public void A_step_missing_its_RetryPolicy_is_rejected_instead_of_throwing_a_null_reference_exception()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                { "StepId": "a", "Worker": "worker", "Inputs": [], "Outputs": [], "DependsOn": [] }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        Assert.Contains(ex.Errors, e => e.Contains("missing RetryPolicy"));
    }

    [Fact]
    public void A_RetryPolicy_with_MaxAttempts_less_than_one_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("bad-retry"),
            1,
            Steps: [new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(MaxAttempts: 0))]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("MaxAttempts '0'"));
    }

    [Fact]
    public void Duplicate_StepIds_are_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("dup"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("Duplicate StepId 'a'"));
    }

    [Fact]
    public void An_undeclared_DependsOn_reference_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("bad-dep"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [new StepId("ghost")], new RetryPolicy(1)),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("'ghost'"));
    }

    [Fact]
    public void A_cyclic_DependsOn_graph_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("cycle"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [new StepId("b")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "worker", [], [], [new StepId("a")], new RetryPolicy(1)),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("Cyclic"));
    }

    [Fact]
    public void A_SupersedeTarget_that_is_not_a_transitive_ancestor_is_rejected()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("bad-supersede"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("c"),
                    "worker",
                    [],
                    [],
                    [new StepId("a")],
                    new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [new StepId("b")])),
            ]);

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition)));

        Assert.Contains(ex.Errors, e => e.Contains("SupersedeTarget 'b'"));
    }

    [Fact]
    public void A_SupersedeTarget_that_is_a_transitive_but_not_direct_ancestor_is_accepted()
    {
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("transitive-supersede"),
            1,
            Steps:
            [
                new WorkflowStepDefinition(new StepId("a"), "worker", [], [], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("b"), "worker", [], [], [new StepId("a")], new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    new StepId("c"),
                    "worker",
                    [],
                    [],
                    [new StepId("b")],
                    new RetryPolicy(1),
                    PausePoint: new PausePoint(SupersedeTargets: [new StepId("a")])),
            ]);

        var parsed = WorkflowDefinitionParser.Parse(JsonSerializer.Serialize(definition));

        Assert.Equal(3, parsed.Steps.Count);
    }

    [Fact]
    public void An_unknown_Backoff_preset_surfaces_converter_message_without_malformed_preamble()
    {
        var json = """
            {
              "WorkflowTemplateId": "x",
              "WorkflowTemplateVersion": 1,
              "Steps": [
                {
                  "StepId": "a",
                  "Worker": "w",
                  "Inputs": [],
                  "Outputs": [],
                  "DependsOn": [],
                  "RetryPolicy": { "MaxAttempts": 3, "Backoff": "unknown_preset" }
                }
              ]
            }
            """;

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        var error = Assert.Single(ex.Errors);
        Assert.StartsWith("Unknown Backoff preset 'unknown_preset' for field 'Backoff'", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Malformed template JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncated_or_malformed_json_retains_malformed_preamble()
    {
        var json = """{"WorkflowTemplateId":"x","WorkflowTemplateVersion":1,"Steps":[{"StepId":"a","Worker":""";

        var ex = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionParser.Parse(json));

        var error = Assert.Single(ex.Errors);
        Assert.StartsWith("Malformed template JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_converter_reachable_from_the_template_graph_is_known_to_the_prefix_list()
    {
        // #792: ConverterMessagePrefixes is an enumerated list, so a converter added to the
        // template graph later would have its value errors re-buried under the malformed-JSON
        // preamble with nothing red anywhere. This walk is the tripwire: when it fails, extend
        // ConverterMessagePrefixes in WorkflowDefinitionParser for the new converter's messages,
        // then add it to the covered set below.
        var covered = new HashSet<string>
        {
            "Aer.Flow.Domain.BackoffPolicyJsonConverter",
            "Aer.Flow.Domain.StepId+Converter",
            "Aer.Flow.Domain.WorkflowTemplateId+Converter",
        };

        var discovered = new HashSet<string>();
        var visited = new HashSet<Type>();

        void Walk(Type type)
        {
            if (type.IsArray)
            {
                Walk(type.GetElementType()!);
                return;
            }

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    Walk(arg);
                }
            }

            if (type.Namespace?.StartsWith("Aer.Flow", StringComparison.Ordinal) != true
                || !visited.Add(type))
            {
                return;
            }

            var attribute = type.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonConverterAttribute), inherit: false);
            foreach (System.Text.Json.Serialization.JsonConverterAttribute a in attribute)
            {
                discovered.Add(a.ConverterType!.FullName!);
            }

            foreach (var property in type.GetProperties())
            {
                Walk(property.PropertyType);
            }
        }

        Walk(typeof(WorkflowDefinition));

        Assert.True(discovered.Count > 0, "the walk found no converters at all -- it is broken, not the graph clean");
        Assert.True(discovered.SetEquals(covered),
            $"template-graph converters and the covered set diverge. discovered: [{string.Join(", ", discovered)}] " +
            $"covered: [{string.Join(", ", covered)}]. See ConverterMessagePrefixes in WorkflowDefinitionParser.");
    }

    [Fact]
    public async Task LoadFromFileAsync_on_a_missing_file_throws_the_typed_exception_not_a_raw_FileNotFound()
    {
        // Missing --workflow -> the typed WorkflowDefinitionValidationException, never a raw
        // FileNotFoundException; RunCommand's fresh-bind path reads through here with no existence check.
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-template-{Guid.NewGuid():N}.json");

        var ex = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.LoadFromFileAsync(missing, TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", ex.Message);
        Assert.Null(ex.TryInvocation); // The missing file ends in .json, so it gets no Try: built-in suggestion.
    }

    [Fact]
    public async Task LoadFromFileAsync_on_a_missing_file_without_json_extension_suggests_aer_dispatch()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-template-{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(
            () => WorkflowDefinitionParser.LoadFromFileAsync(missing, TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", ex.Message);
        Assert.Contains("'aer run' takes a workflow FILE; built-in templates are used via 'aer dispatch <role>'", ex.TryInvocation, StringComparison.Ordinal);
    }
}
