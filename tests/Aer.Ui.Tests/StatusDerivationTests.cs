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

    public static TheoryData<WorkflowStatus, StepStatus, string> OrphanedAskCases() => new()
    {
        // #1112 review finding: an ask can outlive its turn (a lost turn-end revoke race, a young
        // ask re-raised by startup reconcile — #1113). The gate is only genuinely answerable while
        // a turn is executing, so every non-Running state keeps its own truthful word rather than
        // masking it with "Permission requested" for a worker nothing is left to release.
        { WorkflowStatus.Paused, StepStatus.Paused, "Waiting for your review" },
        { WorkflowStatus.Terminal, StepStatus.Failed, "Failed" },
        { WorkflowStatus.Terminal, StepStatus.Cancelled, "Cancelled" },
        { WorkflowStatus.Terminal, StepStatus.Succeeded, "Finished" },
    };

    [Theory]
    [MemberData(nameof(OrphanedAskCases))]
    public void DeriveStatus_OrphanedAskBesideNonRunningState_KeepsTheTrueStatus(
        WorkflowStatus status, StepStatus stepStatus, string expectedText)
    {
        var orphanedAsk = new Aer.Flow.Projection.PendingPermission(
            "req-orphan",
            "worker-alpha",
            "claude",
            "Bash",
            """{"command":"ls"}""",
            "Bash",
            DateTimeOffset.UtcNow);

        var projection = ProjectionWith(status, stepStatus);

        var (statusText, _) = RoomCardViewModel.DeriveStatus(projection, orphanedAsk);

        Assert.Equal(expectedText, statusText);
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

    [Fact]
    public void PlainLanguage_ForStep_ExhaustedUntil_KnownInstant_RendersOutofPlanResumesLocalTime()
    {
        var resetInstant = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var expectedLocalTime = resetInstant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var result = PlainLanguage.ForStep(StepStatus.Failed, FailureClassification.ExhaustedUntil, resetInstant);

        Assert.Equal($"Out of plan — resumes {expectedLocalTime}", result);
    }

    [Fact]
    public void PlainLanguage_ForStep_ExhaustedUntil_UnknownInstant_RendersOutofPlanResetUnknown()
    {
        var result = PlainLanguage.ForStep(StepStatus.Failed, FailureClassification.ExhaustedUntil, null);

        Assert.Equal("Out of plan — reset unknown", result);
    }

    [Fact]
    public void PlainLanguage_ForStep_Polarity_OrdinaryFailedAndRetryableWordingUnchanged()
    {
        Assert.Equal("Failed", PlainLanguage.ForStep(StepStatus.Failed, FailureClassification.Retryable));
        Assert.Equal("Failed", PlainLanguage.ForStep(StepStatus.Failed, FailureClassification.Permanent));
        Assert.Equal("Failed", PlainLanguage.ForStep(StepStatus.Failed, null));
    }

    [Fact]
    public void DeriveStatus_MixedExhaustedAndGenuinelyFailed_IsFailed_NotWorking()
    {
        // #1116 review must-fix: an unresolved ExhaustedUntil step keeps WorkflowStatus.Running
        // alive FOREVER (RetryEngine.MayRetry bypasses attempts for it), so a genuinely failed
        // sibling would hide behind "Working" indefinitely — Terminal's "Failed" arm never comes.
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-mixed"),
            1,
            [new WorkflowStepDefinition(new StepId("step-0"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var exhausted = new StepState(
            new StepId("step-0"), StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-1"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero));
        var genuinelyFailed = new StepState(
            new StepId("step-1"), StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-2"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.Permanent);

        var projection = new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, [exhausted, genuinelyFailed], WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null);

        Assert.Equal(RoomCardStatus.Failed, status);
        Assert.Equal("Failed", statusText);
    }

    [Fact]
    public void DeriveStatus_TwoExhaustedSteps_ShowsTheLatestResetInstant_AndAnyUnknownMakesItUnknown()
    {
        // #1116 review should-fix: the room cannot fully resume before EVERY exhausted step
        // clears — the honest instant is the max, never declaration order's arbitrary first.
        var earlier = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 8, 13, 6, 0, 0, TimeSpan.Zero);
        var expectedLocalTime = later.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-two-exhausted"),
            1,
            [new WorkflowStepDefinition(new StepId("step-0"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        StepState Exhausted(string stepId, string execId, DateTimeOffset? instant) => new(
            new StepId(stepId), StepStatus.Failed,
            LatestExecutionId: new ExecutionId(execId),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: instant);

        RoomProjection ProjectionOf(params StepState[] steps) => new(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, steps, WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        // Earlier instant declared FIRST — declaration order must not win.
        var (bothKnownText, bothKnownStatus) = RoomCardViewModel.DeriveStatus(
            ProjectionOf(Exhausted("step-0", "e-1", earlier), Exhausted("step-1", "e-2", later)), null);
        Assert.Equal(RoomCardStatus.OutOfPlan, bothKnownStatus);
        Assert.Equal($"Out of plan — resumes {expectedLocalTime}", bothKnownText);

        // One unknown instant makes the room's answer unknown — a known sibling must not
        // fabricate a full-resume time the vendor never gave for the other step.
        var (oneUnknownText, oneUnknownStatus) = RoomCardViewModel.DeriveStatus(
            ProjectionOf(Exhausted("step-0", "e-1", earlier), Exhausted("step-1", "e-2", null)), null);
        Assert.Equal(RoomCardStatus.OutOfPlan, oneUnknownStatus);
        Assert.Equal("Out of plan — reset unknown", oneUnknownText);
    }

    [Fact]
    public void DeriveStatus_RoomWithExhaustedStep_IsNotNeedsYou_Carries0026Sentence()
    {
        var resetInstant = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var expectedLocalTime = resetInstant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-exhausted"),
            1,
            [new WorkflowStepDefinition(new StepId("step-0"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var stepState = new StepState(
            new StepId("step-0"),
            StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-1"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: resetInstant);

        var projection = new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, [stepState], WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null);

        Assert.NotEqual(RoomCardStatus.NeedsYou, status);
        Assert.NotEqual(RoomCardStatus.Failed, status);
        Assert.Equal(RoomCardStatus.OutOfPlan, status);
        Assert.Equal($"Out of plan — resumes {expectedLocalTime}", statusText);
    }

    [Fact]
    public void DeriveStatus_RoomWithExhaustedStep_UnknownInstant_CarriesResetUnknownSentence()
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-exhausted-unknown"),
            1,
            [new WorkflowStepDefinition(new StepId("step-0"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var stepState = new StepState(
            new StepId("step-0"),
            StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-1"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: null);

        var projection = new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, [stepState], WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null);

        Assert.NotEqual(RoomCardStatus.NeedsYou, status);
        Assert.NotEqual(RoomCardStatus.Failed, status);
        Assert.Equal(RoomCardStatus.OutOfPlan, status);
        Assert.Equal("Out of plan — reset unknown", statusText);
    }

    [Fact]
    public void DeriveStatus_Polarity_PendingPermissionAndFailedArmPassUnchanged()
    {
        var pendingPermission = new Aer.Flow.Projection.PendingPermission(
            "req-101",
            "worker-alpha",
            "claude",
            "WriteFiles",
            """{"path":"test.txt"}""",
            "WriteFiles",
            DateTimeOffset.UtcNow);

        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-polarity"),
            1,
            [new WorkflowStepDefinition(new StepId("step-0"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var exhaustedStep = new StepState(
            new StepId("step-0"),
            StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-1"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.ExhaustedUntil);

        var projectionExhaustedWithPermission = new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, [exhaustedStep], WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        var (permText, permStatus) = RoomCardViewModel.DeriveStatus(projectionExhaustedWithPermission, pendingPermission);
        Assert.Equal(RoomCardStatus.NeedsYou, permStatus);
        Assert.Equal("Permission requested", permText);

        var ordinaryFailedStep = new StepState(
            new StepId("step-0"),
            StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-1"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.Retryable);

        var projectionOrdinaryFailed = new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, [ordinaryFailedStep], WorkflowStatus.Terminal),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        var (failedText, failedStatus) = RoomCardViewModel.DeriveStatus(projectionOrdinaryFailed, null);
        Assert.Equal(RoomCardStatus.Failed, failedStatus);
        Assert.Equal("Failed", failedText);
    }
}
