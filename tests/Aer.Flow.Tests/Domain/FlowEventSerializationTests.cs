using System.Text.Json;
using Aer.Flow.Domain;

using Aer.Flow.Store;

namespace Aer.Flow.Tests.Domain;

public class FlowEventSerializationTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly StepId StepId = new("build");

    public static IEnumerable<object[]> AllEventVariants()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "claude",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/plan.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment:
            [
                new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", "/artifacts/execution_2"),
                new EnvironmentVariable.PassThrough("ANTHROPIC_API_KEY"),
            ],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId> { [new StepId("architect")] = new ExecutionId("exec-0") });

        // A step-less supplementary execution: StepId and Timeout are both null.
        var stepLessRequest = new ExecutionRequest(
            new ExecutionId("exec-supplement"),
            new WorkflowId("wf-1"),
            StepId: null,
            "human",
            Inputs: [],
            Outputs: ["revision.md"],
            Timeout: null,
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var auditedRequest = new ExecutionRequest(
            new ExecutionId("exec-audited"),
            new WorkflowId("wf-1"),
            StepId,
            "gemini",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(5),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: GrantAuditMode.AuditedNotEnforced);


        yield return [new FlowEvent.ExecutionRequestAccepted(request)];
        yield return [new FlowEvent.ExecutionRequestAccepted(stepLessRequest)];
        yield return [new FlowEvent.ExecutionRequestAccepted(auditedRequest)];
        yield return [new FlowEvent.ExecutionRequestRejected(ExecutionId, "concurrency cap reached")];

        yield return [new FlowEvent.ExecutionSucceeded(ExecutionId)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification: null)];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, "Worker process exited with code 1")];
        yield return [new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification: null, "Missing required output file 'plan.md'")];
        yield return [new FlowEvent.ExecutionCancelled(ExecutionId)];
        yield return [new FlowEvent.CancellationRequested(ExecutionId)];
        yield return [new FlowEvent.WorkflowPaused(ExecutionId, StepId)];
        yield return
        [
            new FlowEvent.ExternalDecisionRecorded(
                new DecisionId("decision-1"),
                ExecutionId,
                DecisionType.Supersede,
                new StepId("architect"),
                new ExecutionId("exec-9"))
        ];
        yield return [new FlowEvent.WorkflowResumed(new DecisionId("decision-1"))];
    }

    [Theory]
    [MemberData(nameof(AllEventVariants))]
    public void RoundTrips_through_the_FlowEvent_base_type_without_data_loss(FlowEvent original)
    {
        var json = JsonSerializer.Serialize(original, typeof(FlowEvent), FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options);
        Assert.NotNull(deserialized);

        var reserialized = JsonSerializer.Serialize(deserialized, typeof(FlowEvent), FlowEventLogJson.Options);
        Assert.Equal(json, reserialized);
        Assert.Equal(original.GetType(), deserialized.GetType());
    }

    [Fact]
    public void Deserializing_an_unknown_event_type_discriminator_throws()
    {
        const string json = """{"eventType":"somethingElse"}""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FlowEvent>(json, FlowEventLogJson.Options));
    }

    /// <summary>
    /// Produces the <c>flow.jsonl</c> line an AER build from before #597 would have written, by
    /// serializing a current event and deleting the <c>Reason</c> property from the wire form.
    /// </summary>
    /// <remarks>
    /// Deriving it beats hand-typing it. The first version of these two tests hand-wrote
    /// <c>{"eventType":"executionFailed","executionId":"exec-1",…}</c> in camelCase; the real
    /// serializer emits members in PascalCase and only the discriminator in camelCase, so every
    /// property silently missed and deserialized to its default. The tests failed for a reason that
    /// had nothing to do with what they were written to check. Derived this way, the fixture tracks
    /// the wire format automatically rather than being hand-typed.
    /// <para>
    /// It derives from the <i>default</i> serializer deliberately, not from
    /// <see cref="Aer.Flow.Store.FlowEventLogJson.Options"/>: the default emits the ordinal enum shape
    /// (<c>"FailureClassification":0</c>) a genuinely historical line carries, which is precisely what
    /// this fixture exists to reproduce. The read side uses the journal's real options, so the test
    /// still drives production's reader against a pre-#604 line. The earlier claim here — that the
    /// fixture "cannot drift away from the wire format again" — stopped being true when the two
    /// diverged in #604, and is not restated.
    /// </para>
    /// </remarks>
    private static string LegacyExecutionFailedJson(FailureClassification? classification)
    {
        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, classification, "some reason"),
            typeof(FlowEvent));

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();

        // Guards the derivation itself: if the property were ever renamed, Remove would return
        // false and this fixture would quietly become a *current* line, making the legacy test
        // pass while proving nothing.
        Assert.True(node.Remove(nameof(FlowEvent.ExecutionFailed.Reason)));

        return node.ToJsonString();
    }

    [Theory]
    [InlineData(FailureClassification.Retryable)]
    [InlineData(FailureClassification.Permanent)]
    [InlineData(null)]
    public void Deserializing_legacy_ExecutionFailed_without_Reason_property_deserializes_with_null_Reason(
        FailureClassification? classification)
    {
        // #597 added Reason as a trailing defaulted member specifically so lines already on disk
        // stay readable. A journal that stopped deserializing after an upgrade is unrecoverable
        // state, which is why this is asserted rather than assumed.
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionFailedJson(classification), FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(ExecutionId, failed.ExecutionId);
        Assert.Equal(classification, failed.FailureClassification);
        Assert.Null(failed.Reason);
    }

    [Fact]
    public void Deserializing_current_ExecutionFailed_with_Reason_property_sets_Reason()
    {
        // The polarity control for the test above: same event shape, Reason present rather than
        // stripped. Without this arm, an implementation that never read Reason at all would pass
        // the legacy test — null is what it asserts.
        const string reason = "Missing required output file 'plan'";

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionFailed(ExecutionId, FailureClassification.Retryable, reason),
            typeof(FlowEvent));

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var failed = Assert.IsType<FlowEvent.ExecutionFailed>(deserialized);
        Assert.Equal(reason, failed.Reason);
    }

    private static string LegacyExecutionRequestAcceptedJson()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "gemini",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: GrantAuditMode.AuditedNotEnforced);

        var current = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!.AsObject();
        var requestNode = node["Request"]!.AsObject();

        Assert.True(requestNode.Remove(nameof(ExecutionRequest.GrantAuditMode)));

        return node.ToJsonString();
    }

    [Fact]
    public void Deserializing_legacy_ExecutionRequestAccepted_without_GrantAuditMode_deserializes_with_null_mode()
    {
        var deserialized = JsonSerializer.Deserialize<FlowEvent>(
            LegacyExecutionRequestAcceptedJson(), FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(ExecutionId, accepted.Request.ExecutionId);
        Assert.Null(accepted.Request.GrantAuditMode);
    }

    [Fact]
    public void Deserializing_current_ExecutionRequestAccepted_with_GrantAuditMode_sets_mode()
    {
        var request = new ExecutionRequest(
            ExecutionId,
            new WorkflowId("wf-1"),
            StepId,
            "gemini",
            Inputs: ["/artifacts/execution_1/goal.md"],
            Outputs: ["/artifacts/execution_2/report.md"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: GrantAuditMode.AuditedNotEnforced);

        var currentJson = JsonSerializer.Serialize(
            (FlowEvent)new FlowEvent.ExecutionRequestAccepted(request),
            typeof(FlowEvent),
            FlowEventLogJson.Options);

        var deserialized = JsonSerializer.Deserialize<FlowEvent>(currentJson, FlowEventLogJson.Options);

        var accepted = Assert.IsType<FlowEvent.ExecutionRequestAccepted>(deserialized);
        Assert.Equal(GrantAuditMode.AuditedNotEnforced, accepted.Request.GrantAuditMode);
    }
}


