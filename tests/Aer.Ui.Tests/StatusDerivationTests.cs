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

        // Terminal, so the lock reading cannot change the answer — passed explicitly all the same,
        // since #1219 removed the default that let a caller skip the question by accident.
        Assert.Equal("Cancelled", PlainLanguage.ForWorkflow(projection, isFlowLockHeld: false));
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

        // #1219: both lock readings, not just the held one. A second reader found this fact could
        // not have caught the defect it exists for — the headline took a *defaulted* `true` while
        // every other surface probed, so the two derivations agreed here and disagreed on screen.
        // Passing both polarities is what makes "one derivation" mean the whole derivation.
        foreach (var isFlowLockHeld in new[] { true, false })
        {
            Assert.Equal(
                RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission, isFlowLockHeld).StatusText,
                PlainLanguage.ForWorkflow(projection, isFlowLockHeld));
        }
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

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, pendingPermission, isFlowLockHeld: true);

        Assert.Equal(RoomCardStatus.NeedsYou, status);
        Assert.Equal("Permission requested", statusText);
    }

    /// <summary>
    /// #1219's tenth state, and the reason it exists: a room whose process died and a room genuinely
    /// working have the same journal, so these two arms differ in the lock argument and in nothing
    /// else. Before this, both said "Working — step-0" and the switcher turned a spinner over a room
    /// where nothing was happening.
    /// </summary>
    [Fact]
    public void DeriveStatus_RunningWithNoLivePump_IsStoppedNotWorking()
    {
        var projection = ProjectionWith(WorkflowStatus.Running, StepStatus.Running);

        var (workingText, workingStatus) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: true);
        Assert.Equal(RoomCardStatus.Running, workingStatus);
        Assert.Equal("Working — step-0", workingText);

        var (stoppedText, stoppedStatus) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: false);
        Assert.Equal(RoomCardStatus.Stopped, stoppedStatus);
        Assert.Equal("Stopped", stoppedText);
    }

    /// <summary>
    /// The Stopped arm sits ahead of every other Running arm, and each of those orderings is a claim
    /// about what a person is told. Without the lock these all read as something still in progress.
    /// </summary>
    [Fact]
    public void DeriveStatus_StoppedBeatsTheOtherRunningArms()
    {
        // An orphaned permission ask on a dead room: no worker is left to be released by an answer,
        // so the room's true state wins over the gate. A LIVE gate is the arm below and still wins.
        var withOrphanedAsk = ProjectionWith(WorkflowStatus.Running, StepStatus.Running);
        var orphanedAsk = new Aer.Flow.Projection.PendingPermission(
            "req-dead", "worker-alpha", "claude", "Bash",
            """{"command":"ls"}""", "Bash", DateTimeOffset.UtcNow);

        Assert.Equal(RoomCardStatus.Stopped, RoomCardViewModel.DeriveStatus(withOrphanedAsk, orphanedAsk, isFlowLockHeld: false).Status);
        Assert.Equal(RoomCardStatus.NeedsYou, RoomCardViewModel.DeriveStatus(withOrphanedAsk, orphanedAsk, isFlowLockHeld: true).Status);

        // An out-of-plan room whose process then died must not keep promising a resume time nothing
        // is left to honour — the misleading-optimistic instant 0026 §5 rules out.
        var exhausted = ProjectionWith(WorkflowStatus.Running, StepStatus.Failed) with { };
        var outOfPlan = new RoomProjection(
            exhausted.Snapshot,
            new FlowState(
                exhausted.Snapshot.WorkflowDefinitionSnapshotId,
                [new StepState(
                    new StepId("step-0"), StepStatus.Failed, LatestExecutionId: new ExecutionId("e-1"),
                    UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
                    LatestFailureClassification: FailureClassification.ExhaustedUntil,
                    RetryNotBefore: DateTimeOffset.UtcNow.AddHours(3))],
                WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        Assert.Equal(RoomCardStatus.OutOfPlan, RoomCardViewModel.DeriveStatus(outOfPlan, null, isFlowLockHeld: true).Status);
        Assert.Equal(RoomCardStatus.Stopped, RoomCardViewModel.DeriveStatus(outOfPlan, null, isFlowLockHeld: false).Status);
    }

    /// <summary>
    /// A room waiting on a decision is never Stopped, lock or no lock — and the scan is a step scan,
    /// so a crashed room with one branch still Running and another Paused keeps the person's action
    /// rather than being labelled Stopped beside a gate they can answer.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeriveStatus_ARoomAwaitingADecisionIsNeverStopped(bool isFlowLockHeld)
    {
        var paused = ProjectionWith(WorkflowStatus.Paused, StepStatus.Succeeded, StepStatus.Paused);
        Assert.Equal(RoomCardStatus.NeedsYou, RoomCardViewModel.DeriveStatus(paused, null, isFlowLockHeld).Status);

        var mixed = ProjectionWith(WorkflowStatus.Running, StepStatus.Running, StepStatus.Paused);
        Assert.NotEqual(RoomCardStatus.Stopped, RoomCardViewModel.DeriveStatus(mixed, null, isFlowLockHeld).Status);
    }

    [Fact]
    public void DeriveStatus_AnsweredPermission_RestoresRunningStatus()
    {
        var projection = ProjectionWith(WorkflowStatus.Running, StepStatus.Running);

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: true);

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

        var (statusText, _) = RoomCardViewModel.DeriveStatus(projection, orphanedAsk, isFlowLockHeld: true);

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
        // #1116 review must-fix — why a genuinely failed sibling must not hide behind the
        // exhausted keep-alive is the mixed-room arm comment in RoomCardViewModel.DeriveStatus.
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

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: true);

        Assert.Equal(RoomCardStatus.Failed, status);
        Assert.Equal("Failed", statusText);

        // #1219, found by a second reader: this shape can reach the Stopped arm with a free lock, and
        // that arm's own comment in DeriveStatus explains why a recorded verdict has to win. Pinned
        // here because nothing else combines the two step kinds under a lock nobody holds.
        var (deadText, deadStatus) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: false);

        Assert.Equal(RoomCardStatus.Failed, deadStatus);
        Assert.Equal("Failed", deadText);
    }

    /// <summary>
    /// The other side of the arm above, and the reason its guard is not simply "no failed steps":
    /// exhaustion alone is a <em>wait</em>, not a verdict, so a room blocked only on quota whose
    /// process then died is Stopped rather than going on promising a reset instant nothing is left to
    /// honour. Both polarities, since one alone would pass under a guard that always answered the same.
    /// </summary>
    [Fact]
    public void DeriveStatus_ARoomBlockedOnlyOnQuotaIsStoppedOnceNothingIsServingTheWait()
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("status-derivation-quota"),
            1,
            [new WorkflowStepDefinition(new StepId("step-0"), "worker", ["in"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var exhausted = new StepState(
            new StepId("step-0"), StepStatus.Failed,
            LatestExecutionId: new ExecutionId("exec-1"),
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            LatestFailureClassification: FailureClassification.ExhaustedUntil,
            RetryNotBefore: new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero));

        var projection = new RoomProjection(
            snapshot,
            new FlowState(snapshot.WorkflowDefinitionSnapshotId, [exhausted], WorkflowStatus.Running),
            new ExecutionHistory(new Dictionary<StepId, IReadOnlyList<ExecutionAttempt>>(), [], []),
            new ArtifactLineage([]));

        Assert.Equal(RoomCardStatus.OutOfPlan, RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: true).Status);
        Assert.Equal(RoomCardStatus.Stopped, RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: false).Status);
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
            ProjectionOf(Exhausted("step-0", "e-1", earlier), Exhausted("step-1", "e-2", later)), null, isFlowLockHeld: true);
        Assert.Equal(RoomCardStatus.OutOfPlan, bothKnownStatus);
        Assert.Equal($"Out of plan — resumes {expectedLocalTime}", bothKnownText);

        // One unknown instant makes the room's answer unknown — a known sibling must not
        // fabricate a full-resume time the vendor never gave for the other step.
        var (oneUnknownText, oneUnknownStatus) = RoomCardViewModel.DeriveStatus(
            ProjectionOf(Exhausted("step-0", "e-1", earlier), Exhausted("step-1", "e-2", null)), null, isFlowLockHeld: true);
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

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: true);

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

        var (statusText, status) = RoomCardViewModel.DeriveStatus(projection, null, isFlowLockHeld: true);

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

        var (permText, permStatus) = RoomCardViewModel.DeriveStatus(projectionExhaustedWithPermission, pendingPermission, isFlowLockHeld: true);
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

        var (failedText, failedStatus) = RoomCardViewModel.DeriveStatus(projectionOrdinaryFailed, null, isFlowLockHeld: true);
        Assert.Equal(RoomCardStatus.Failed, failedStatus);
        Assert.Equal("Failed", failedText);
    }
}
