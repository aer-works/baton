using System.Text.Json;
using System.Text.Json.Nodes;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Ui.Core;

namespace Aer.Daemon.Tests;

public static class WireFixtureGenerator
{
    public const string RelativeFixturesPath = "src/Aer.Mobile/test/fixtures/wire";

    public static Dictionary<string, string> GenerateAll()
    {
        var fixtures = new Dictionary<string, string>();

        var projection = BuildRepresentativeProjection();

        // 1. RoomProjection REST (camelCase)
        var restNode = JsonSerializer.SerializeToNode(projection, DaemonSerializerOptions.Rest)!.AsObject();
        restNode["directoryPath"] = "C:/tasks/foo";
        restNode["sessionId"] = "session-123";
        var workerAdaptersRest = new JsonObject { ["critic"] = "agy" };
        restNode["workerAdapters"] = workerAdaptersRest;
        fixtures[Path.Combine(RelativeFixturesPath, "room_projection.rest.json")] = FormatJson(restNode, DaemonSerializerOptions.Rest);

        // 2. RoomProjection WS (PascalCase envelope)
        var wsNode = JsonSerializer.SerializeToNode(projection, DaemonSerializerOptions.WebSocket)!.AsObject();
        wsNode["DirectoryPath"] = "C:/tasks/foo";
        wsNode["SessionId"] = "session-123";
        var workerAdaptersWs = new JsonObject { ["critic"] = "agy" };
        wsNode["WorkerAdapters"] = workerAdaptersWs;
        fixtures[Path.Combine(RelativeFixturesPath, "room_projection.ws.json")] = FormatJson(wsNode, DaemonSerializerOptions.WebSocket);

        // 3. RoomFleetItem REST (camelCase). A needs-you row: Status pins the RoomCardStatus enum's
        // wire name ("NeedsYou"), which the mobile switcher's waiting-on-you-first sort keys on
        // literally (#1049) — a rename of the enum member reddens this fixture and the Dart parse test
        // rather than silently breaking the sort. StatusText and Status are independent wire fields.
        var fleetItem = new RoomFleetItem(
            "C:/Users/pbree/.aer/tasks/foo",
            "foo",
            "solo-run-template",
            "Waiting for your review",
            2,
            false,
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 3, 15, 0, 0, TimeSpan.Zero),
            false,
            new DateTimeOffset(2026, 8, 3, 15, 0, 0, TimeSpan.Zero),
            Status: RoomCardStatus.NeedsYou);
        fixtures[Path.Combine(RelativeFixturesPath, "fleet_item.rest.json")] = JsonSerializer.Serialize(fleetItem, IndentedOptions(DaemonSerializerOptions.Rest));

        // 4. RoomFleetItem WS (PascalCase)
        fixtures[Path.Combine(RelativeFixturesPath, "fleet_item.ws.json")] = JsonSerializer.Serialize(fleetItem, IndentedOptions(DaemonSerializerOptions.WebSocket));

        return fixtures;
    }

    public static RoomProjection BuildRepresentativeProjection()
    {
        var snapId = new WorkflowDefinitionSnapshotId("snap-953");
        var templateId = new WorkflowTemplateId("golden-wire-contract");

        var stepPlanner = new WorkflowStepDefinition(
            new StepId("planner"),
            "agy",
            [],
            ["plan.md"],
            [],
            new RetryPolicy(3));

        var stepCritic = new WorkflowStepDefinition(
            new StepId("critic"),
            "agy",
            ["plan.md"],
            ["review.md"],
            [new StepId("planner")],
            new RetryPolicy(3),
            new PausePoint([new StepId("architect")], PausePointKind.ReadyForReview));

        var stepCoder = new WorkflowStepDefinition(
            new StepId("coder"),
            "agy",
            ["review.md"],
            ["code.py"],
            [new StepId("critic")],
            new RetryPolicy(3));

        var snapshot = new WorkflowDefinitionSnapshot(
            snapId,
            templateId,
            1,
            [stepPlanner, stepCritic, stepCoder]);

        var stateSteps = new List<StepState>
        {
            new(
                StepId: new StepId("planner"),
                Status: StepStatus.Running,
                LatestExecutionId: new ExecutionId("exec-1"),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()),
            new(
                StepId: new StepId("critic"),
                Status: StepStatus.Paused,
                LatestExecutionId: new ExecutionId("exec-2"),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId> { { new StepId("planner"), new ExecutionId("exec-1") } },
                PauseRecordedForLatestExecution: true),
            new(
                StepId: new StepId("coder"),
                Status: StepStatus.Failed,
                LatestExecutionId: new ExecutionId("exec-3"),
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId> { { new StepId("critic"), new ExecutionId("exec-2") } },
                ConsecutiveFailureCount: 1,
                LatestFailureClassification: FailureClassification.Permanent,
                LatestFailureReason: "Syntax error on line 42")
        };

        var state = new FlowState(
            WorkflowDefinitionSnapshotId: snapId,
            Steps: stateSteps,
            Status: WorkflowStatus.Paused);

        var attempts = new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>
        {
            { new StepId("planner"), [new ExecutionAttempt(new ExecutionId("exec-1"), "agy", StepStatus.Running, null, false)] },
            { new StepId("critic"), [new ExecutionAttempt(new ExecutionId("exec-2"), "agy", StepStatus.Paused, null, false)] },
            { new StepId("coder"), [new ExecutionAttempt(new ExecutionId("exec-3"), "agy", StepStatus.Failed, FailureClassification.Permanent, false, "Syntax error on line 42")] }
        };

        var history = new ExecutionHistory(attempts, [], []);

        var executions = new List<ExecutionArtifacts>
        {
            new(new ExecutionId("exec-1"), new StepId("planner"), "agy", ["plan.md"], []),
            new(new ExecutionId("exec-2"), new StepId("critic"), "agy", ["review.md"], [new ArtifactInputLink("plan.md", new StepId("planner"), new ExecutionId("exec-1"))]),
            new(new ExecutionId("exec-3"), new StepId("coder"), "agy", ["code.py"], [new ArtifactInputLink("review.md", new StepId("critic"), new ExecutionId("exec-2"))])
        };

        var lineage = new ArtifactLineage(executions);

        // #1142: one answered and one expired entry, so the fixture (and the Dart parse test that
        // reads it) exercises both PermissionAnswer shapes rather than an always-empty list.
        var permissionAnswers = new List<Aer.Flow.Projection.PermissionAnswer>
        {
            new("perm-1", "Bash", "run_command", "AllowOnce", null, "operator",
                new DateTimeOffset(2026, 8, 3, 13, 0, 0, TimeSpan.Zero), WasRevoked: false),
            new("perm-2", "Bash", "run_command", "", "turn_ended", "",
                new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero), WasRevoked: true),
        };

        // #1178: one entered and one cleared transition, so wire fixtures carry real DormancyTransition shapes.
        var dormancyTransitions = new List<Aer.Flow.Domain.DormancyTransition>
        {
            new(IsEntered: true, ConsecutiveFailures: 3, Detail: "The last three turns tried to fix build", ClearedBy: null,
                Timestamp: new DateTimeOffset(2026, 8, 3, 14, 30, 0, TimeSpan.Zero)),
            new(IsEntered: false, ConsecutiveFailures: 0, Detail: null, ClearedBy: "operator",
                Timestamp: new DateTimeOffset(2026, 8, 3, 15, 0, 0, TimeSpan.Zero)),
        };

        return new RoomProjection(snapshot, state, history, lineage, PermissionAnswers: permissionAnswers, DormancyTransitions: dormancyTransitions);
    }

    private static JsonSerializerOptions IndentedOptions(JsonSerializerOptions baseOptions) =>
        new(baseOptions) { WriteIndented = true };

    private static string FormatJson(JsonNode node, JsonSerializerOptions baseOptions) =>
        node.ToJsonString(IndentedOptions(baseOptions));
}
