using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// #616 check 2: no status derivation falls through to a default. The worked example (0020) is a
/// cancelled run rendering as "Finished" because cancellation has no <see cref="WorkflowStatus"/>
/// of its own and a derivation's discard arm absorbed it. #461 fixed that on Home's cards;
/// #976 found the same defect alive in the task headline's separate copy. These pin every
/// derivation's full mapping; the structural half of the check is that every derivation's
/// discard arm now throws instead of answering (the generated AerStatusPresentation posture),
/// so a new enum member turns the golden-map iteration below red instead of shipping a wrong
/// word. (CS8524 under -warnaserror rules out arm-free switches: unnamed enum values exist.)
/// </summary>
public class StatusDerivationTests
{
    private static RoomProjection ProjectionWith(WorkflowStatus status, params StepStatus[] stepStatuses)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-fixture"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(new StepId("only"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var steps = stepStatuses
            .Select((s, i) => new StepState(new StepId($"step-{i}"), s, LatestExecutionId: null, UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>()))
            .ToList();

        return new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, steps, status),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));
    }

    [Fact]
    public void The_task_headline_reads_cancelled_not_finished_for_a_cancelled_run()
    {
        // #976: the red-first arm for this file. Before the fix, ForWorkflow had no Cancelled
        // arm and its discard arm answered "Finished" — the exact 0020 worked example, live.
        var projection = ProjectionWith(WorkflowStatus.Terminal, StepStatus.Cancelled);

        Assert.Equal("Cancelled", PlainLanguage.ForWorkflow(projection));
    }

    [Fact]
    public void Every_surface_derives_a_rooms_name_from_the_one_canonical_helper()
    {
        // The naming counterpart of this file's status-derivation guards: Home cards
        // (RoomCardViewModel.TitleFor) and the switcher/chat header (RoomProjectionLoader.FriendlyNameFor)
        // must resolve a room to the SAME name — one derivation, never a re-implemented copy (#461/#976).
        // A trailing separator discriminates the canonical trim from a naive Path.GetFileName (which
        // would return ""), so a future divergence that drops the trim turns this red.
        const string withTrailingSeparator = "/tmp/aer-flow/";

        Assert.Equal("aer-flow", RoomProjectionLoader.FriendlyNameFor(withTrailingSeparator));
        Assert.Equal(
            RoomProjectionLoader.FriendlyNameFor(withTrailingSeparator),
            RoomCardViewModel.TitleFor(withTrailingSeparator));
    }

    public static TheoryData<WorkflowStatus, StepStatus[]> HeadlineCases() => new()
    {
        { WorkflowStatus.Paused, new[] { StepStatus.Paused } },
        { WorkflowStatus.Running, new[] { StepStatus.Running } },
        { WorkflowStatus.Running, new[] { StepStatus.Pending } },
        { WorkflowStatus.Terminal, new[] { StepStatus.Failed } },
        { WorkflowStatus.Terminal, new[] { StepStatus.Rejected } },
        { WorkflowStatus.Terminal, new[] { StepStatus.Cancelled } },
        { WorkflowStatus.Terminal, new[] { StepStatus.Failed, StepStatus.Cancelled } },
        { WorkflowStatus.Terminal, new[] { StepStatus.Succeeded } },
    };

    [Theory]
    [MemberData(nameof(HeadlineCases))]
    public void The_task_headline_and_the_home_card_are_one_derivation(WorkflowStatus status, StepStatus[] stepStatuses)
    {
        // The headline's doc comment has always claimed it shares Home's mapping. #976 measured
        // that claim false; after the fix it is true by construction (delegation, not a copy),
        // and this arm is what keeps a second copy from ever growing back.
        var projection = ProjectionWith(status, stepStatuses);

        Assert.Equal(
            RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission).StatusText,
            PlainLanguage.ForWorkflow(projection));
    }

    [Fact]
    public void DeriveStatus_PendingPermission_IsNeedsYou_PermissionRequested()
    {
        var pendingPermission = new Aer.Flow.Projection.PendingPermission(
            "req-101",
            "worker-alpha",
            "claude",
            "WriteFiles",
            """{"path":"test.txt"}""",
            "WriteFiles",
            DateTimeOffset.UtcNow);

        var projection = ProjectionWith(WorkflowStatus.Running, StepStatus.Running);

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, pendingPermission);

        Assert.Equal(RoomCardStatus.NeedsYou, status);
        Assert.Equal("Permission requested", statusText);
    }

    [Fact]
    public void DeriveStatus_AnsweredPermission_RestoresRunningStatus()
    {
        var projection = ProjectionWith(WorkflowStatus.Running, StepStatus.Running);

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null);

        Assert.Equal(RoomCardStatus.Running, status);
        Assert.Equal("Working — step-0", statusText);
    }

    [Fact]
    public void Every_step_status_has_a_pinned_plain_word()
    {
        var expected = new Dictionary<StepStatus, string>
        {
            [StepStatus.Pending] = "Not started yet",
            [StepStatus.Running] = "Working",
            [StepStatus.Succeeded] = "Done",
            [StepStatus.Failed] = "Failed",
            [StepStatus.Cancelled] = "Cancelled",
            [StepStatus.Paused] = "Waiting for your review",
            [StepStatus.Rejected] = "Rejected",
        };

        foreach (var status in Enum.GetValues<StepStatus>())
        {
            Assert.True(expected.ContainsKey(status), $"No pinned word for {status} — pin it here and map it explicitly.");
            Assert.Equal(expected[status], PlainLanguage.ForStep(status));
        }
    }

    [Fact]
    public void Every_decision_type_has_a_pinned_plain_word()
    {
        var expected = new Dictionary<DecisionType, string>
        {
            [DecisionType.Resume] = "Approved",
            [DecisionType.Reject] = "Rejected",
            [DecisionType.RetryWithRevision] = "Retry requested",
            [DecisionType.Supersede] = "Sent back",
        };

        foreach (var decisionType in Enum.GetValues<DecisionType>())
        {
            Assert.True(expected.ContainsKey(decisionType), $"No pinned word for {decisionType} — pin it here and map it explicitly.");
            Assert.Equal(expected[decisionType], PlainLanguage.ForDecision(decisionType));
        }
    }
}
