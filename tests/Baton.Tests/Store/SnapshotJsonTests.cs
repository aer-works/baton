using System.Text.Json;
using System.Text.Json.Nodes;
using Baton.Domain;
using Baton.Store;
using Baton.Templates;

namespace Baton.Tests.Store;

/// <summary>
/// #619: the snapshot's wire contract. <c>snapshot.json</c> is durable, unreconstructable state.
/// Enums used to persist as ordinals so reordering a declaration reinterpreted every snapshot on disk.
/// </summary>
public class SnapshotJsonTests
{
    private static WorkflowDefinitionSnapshot SampleSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snap-1"),
        new WorkflowTemplateId("template-1"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(
                new StepId("step-1"),
                "worker-1",
                Inputs: ["in1"],
                Outputs: ["out1"],
                DependsOn: [],
                RetryPolicy: new RetryPolicy(3, BackoffPolicy.Steady),
                PausePoint: new PausePoint([], PausePointKind.NeedsInput)),
        ]);

    [Fact]
    public void Enums_persist_by_name_so_reordering_a_declaration_cannot_reinterpret_the_snapshot()
    {
        var snapshot = SampleSnapshot();
        var json = JsonSerializer.Serialize(snapshot, SnapshotJson.Options);

        Assert.Contains("\"NeedsInput\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\":1", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control for the test above: under the default options this contract replaced, the same
    /// snapshot really does emit the ordinal the probe string looks for. Without this, a property
    /// rename or naming policy could make <c>"Kind":1</c> unfindable for a reason that has nothing
    /// to do with enum names, and the <c>DoesNotContain</c> above would pass while proving nothing.
    /// </summary>
    [Fact]
    public void The_default_options_this_contract_replaced_do_emit_the_ordinal_the_probe_expects()
    {
        var json = JsonSerializer.Serialize(SampleSnapshot());

        Assert.Contains("\"Kind\":1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"NeedsInput\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The additive direction, snapshot-side (#619 review, finding 1) — the same contract
    /// <c>FlowEventLogJsonTests</c> protects for flow.jsonl: <c>RespectRequiredConstructorParameters</c>
    /// bites only parameters with no default, so a member added later WITH a default must stay
    /// loadable from snapshots written before it existed. Exercised through a real defaulted
    /// member removed from a real serialization.
    /// </summary>
    [Fact]
    public void A_snapshot_predating_an_added_optional_member_still_loads_with_the_default()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(SampleSnapshot(), SnapshotJson.Options))!.AsObject();
        var pausePoint = node["Steps"]!.AsArray()[0]!.AsObject()["PausePoint"]!.AsObject();

        // Guards the fixture itself: a rename would make Remove return false and this would
        // quietly become a test of a current snapshot, passing while proving nothing.
        Assert.True(pausePoint.Remove("Kind"));

        var deserialized = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(node.ToJsonString(), SnapshotJson.Options);

        Assert.NotNull(deserialized);
        Assert.Equal(PausePointKind.ReadyForReview, Assert.Single(deserialized.Steps).PausePoint!.Kind);
    }

    [Fact]
    public void An_intact_snapshot_round_trips()
    {
        var original = SampleSnapshot();
        var json = JsonSerializer.Serialize(original, SnapshotJson.Options);
        var deserialized = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(json, SnapshotJson.Options);

        Assert.NotNull(deserialized);
        Assert.Equal(
            json, JsonSerializer.Serialize(deserialized, SnapshotJson.Options));
    }

    [Theory]
    [InlineData(0, PausePointKind.ReadyForReview)]
    [InlineData(1, PausePointKind.NeedsInput)]
    public void A_snapshot_written_before_this_change_still_replays_its_ordinal_enums(
        int ordinal, PausePointKind expected)
    {
        var legacy = $$"""
            {
                "WorkflowDefinitionSnapshotId": "snap-1",
                "WorkflowTemplateId": "template-1",
                "WorkflowTemplateVersion": 1,
                "Steps": [
                    {
                        "StepId": "step-1",
                        "Worker": "worker-1",
                        "Inputs": [],
                        "Outputs": [],
                        "DependsOn": [],
                        "RetryPolicy": { "MaxAttempts": 1, "Backoff": "steady" },
                        "PausePoint": { "SupersedeTargets": [], "Kind": {{ordinal}} }
                    }
                ]
            }
            """;

        var deserialized = JsonSerializer.Deserialize<WorkflowDefinitionSnapshot>(legacy, SnapshotJson.Options);

        Assert.NotNull(deserialized);
        var step = Assert.Single(deserialized.Steps);
        Assert.NotNull(step.PausePoint);
        Assert.Equal(expected, step.PausePoint.Kind);
    }

    [Fact]
    public void The_ordinals_legacy_snapshots_carry_still_mean_what_they_meant_when_written()
    {
        Assert.Equal(0, (int)PausePointKind.ReadyForReview);
        Assert.Equal(1, (int)PausePointKind.NeedsInput);

        Assert.Equal(0, (int)JitterMode.None);
        Assert.Equal(1, (int)JitterMode.Half);
    }

    [Fact]
    public void Every_enum_reachable_from_a_snapshot_is_pinned_by_these_tests()
    {
        var pinned = new[] { typeof(PausePointKind), typeof(JitterMode) };

        var reachable = ReachableEnums(typeof(WorkflowDefinitionSnapshot), typeof(WorkflowDefinition));

        Assert.Equal(pinned.OrderBy(t => t.Name), reachable.OrderBy(t => t.Name));
    }

    private sealed record ArrayCarrier(JitterMode[] Modes);

    /// <summary>
    /// Control for the walk itself (#619 review, finding 2): an enum reachable ONLY through an
    /// array element must be seen. A <c>T[]</c> is not <c>IsGenericType</c>, so without a
    /// dedicated array branch the walk yielded the array type itself and silently never visited
    /// its element — the blind spot that would have let a future array-typed member smuggle an
    /// unpinned enum past the test above.
    /// </summary>
    [Fact]
    public void The_reachability_walk_descends_into_array_element_types()
    {
        Assert.Contains(typeof(JitterMode), ReachableEnums(typeof(ArrayCarrier)));
    }

    private static HashSet<Type> ReachableEnums(params Type[] roots)
    {
        var reachable = new HashSet<Type>();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                foreach (var candidate in Unwrap(parameter.ParameterType))
                {
                    if (candidate.IsEnum)
                    {
                        reachable.Add(candidate);
                    }
                    else if (candidate.Namespace?.StartsWith("Baton", StringComparison.Ordinal) == true)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }

            foreach (var prop in type.GetProperties())
            {
                foreach (var candidate in Unwrap(prop.PropertyType))
                {
                    if (candidate.IsEnum)
                    {
                        reachable.Add(candidate);
                    }
                    else if (candidate.Namespace?.StartsWith("Baton", StringComparison.Ordinal) == true)
                    {
                        queue.Enqueue(candidate);
                    }
                }
            }
        }

        return reachable;
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            yield return underlying;
            yield break;
        }

        // A T[] is not IsGenericType, so without this branch the walk yielded the array type
        // itself and never visited its element — the walk's own control test pins this.
        if (type.IsArray)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }

            yield break;
        }

        yield return type;
    }
}
