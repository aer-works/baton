using Baton.Domain;
using Baton.Projection;

namespace Baton.Tests.Projection;

/// <summary>
/// #1157: what "the terminal instant" means, pinned. The claim under test is narrow and specific —
/// the instant is the LAST transition into <see cref="WorkflowStatus.Terminal"/>, which is what
/// distinguishes it from the two cheaper answers it replaces (<c>flow.jsonl</c>'s mtime, and the last
/// journal line's own stamp). Both of those move when something is appended after a run ended; this
/// must not. Every absence arm also asserts its <see cref="TerminalInstantAbsence"/>, because a caller
/// acts on which one it got.
/// </summary>
public class TerminalInstantResolverTests
{
    private static readonly StepId StepA = new("stepA");
    private static readonly DateTime Ended = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);

    private static WorkflowDefinitionSnapshot SingleStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("single-step"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(StepA, "worker", [], [], DependsOn: [], RetryPolicy: new RetryPolicy(1)),
        ]);

    private static WorkflowDefinitionSnapshot ZeroStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-empty"),
        new WorkflowTemplateId("zero-step"),
        WorkflowTemplateVersion: 1,
        Steps: []);

    private static ExecutionRequest Request(ExecutionId executionId) => new(
        executionId,
        new WorkflowId("wf-1"),
        StepA,
        "worker",
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromMinutes(1),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    private static LogEntry Flow(FlowEvent flowEvent, DateTime? stamp) =>
        new LogEntry.FlowLogEntry(flowEvent, stamp);

    [Fact]
    public void Resolve_TerminalRun_ReportsTheTransitionEntrysWriterStamp()
    {
        var exec = new ExecutionId("exec-1");

        var resolved = TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), Ended.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(exec), Ended),
            ],
            SingleStepSnapshot());

        Assert.Equal(Ended, resolved.AtUtc);
        Assert.Equal(TerminalInstantAbsence.None, resolved.Absence);
    }

    /// <summary>
    /// The whole point of the change. A diagnostic appended long after the run ended
    /// (<see cref="FlowEvent.ZeroOutputsDespiteSubstantialWork"/> is deliberately
    /// consequence-free in <see cref="StateProjector"/>) moves both the file's mtime and the last
    /// line's stamp — and must move neither the answer here.
    /// </summary>
    [Fact]
    public void Resolve_LateAppendAfterTerminal_DoesNotMoveTheInstant()
    {
        var exec = new ExecutionId("exec-1");
        var entries = new List<LogEntry>
        {
            Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), Ended.AddMinutes(-5)),
            Flow(new FlowEvent.ExecutionSucceeded(exec), Ended),
        };

        // Control arm read first: without the late append the answer is Ended, so a passing assertion
        // below is about the append being ignored and not about the fixture never having been terminal.
        Assert.Equal(Ended, TerminalInstantResolver.Resolve(entries, SingleStepSnapshot()).AtUtc);

        entries.Add(Flow(new FlowEvent.ZeroOutputsDespiteSubstantialWork(exec, "diagnostic"), Later));

        Assert.Equal(Ended, TerminalInstantResolver.Resolve(entries, SingleStepSnapshot()).AtUtc);
    }

    /// <summary>
    /// Polarity in both directions for the same fixture shape: a non-Flow line landing after the run
    /// (Core's own half of the combined journal) is not even a projection input, so it cannot be a
    /// transition — and the mtime it moves is precisely what the old proxy read.
    /// </summary>
    [Fact]
    public void Resolve_LateCoreLine_IsNotATransitionCandidate()
    {
        var exec = new ExecutionId("exec-1");

        var resolved = TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), Ended.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(exec), Ended),
                new LogEntry.CoreLogEntry(new CoreEvent.ExecutionStarted(exec, Pid: 4321), Later),
            ],
            SingleStepSnapshot());

        Assert.Equal(Ended, resolved.AtUtc);
    }

    [Fact]
    public void Resolve_RunThatIsNotTerminal_IsAbsentAsNotTerminal()
    {
        var exec = new ExecutionId("exec-1");

        var resolved = TerminalInstantResolver.Resolve(
            [Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), Ended)],
            SingleStepSnapshot());

        Assert.Null(resolved.AtUtc);
        Assert.Equal(TerminalInstantAbsence.NotTerminal, resolved.Absence);
    }

    /// <summary>
    /// The crash-window rule, stated in spec/baton.md §3 "The terminal instant" and pinned here:
    /// nothing synthesises an instant from the events that DID land.
    /// </summary>
    [Fact]
    public void Resolve_JournalTruncatedBeforeItsTerminalEvent_InventsNothing()
    {
        var exec = new ExecutionId("exec-1");
        var snapshot = SingleStepSnapshot();

        // Control arm: with the terminal line present this fixture resolves. The assertion below is
        // therefore about the missing line, not about the shape being unresolvable to begin with.
        Assert.NotNull(TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), Ended.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(exec), Ended),
            ],
            snapshot).AtUtc);

        var truncated = TerminalInstantResolver.Resolve(
            [Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), Ended.AddMinutes(-5))],
            snapshot);

        Assert.Null(truncated.AtUtc);
        Assert.Equal(TerminalInstantAbsence.NotTerminal, truncated.Absence);
    }

    /// <summary>
    /// A pre-#745 journal carries no writer stamps at all. Absent, never fabricated, and reported as
    /// the one absence spec/baton.md §3 lets the retention sweep name to an operator.
    /// </summary>
    [Fact]
    public void Resolve_TerminalRunOnAJournalWithNoWriterStamps_IsAbsentAsUnstamped()
    {
        var exec = new ExecutionId("exec-1");

        var resolved = TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), stamp: null),
                Flow(new FlowEvent.ExecutionSucceeded(exec), stamp: null),
            ],
            SingleStepSnapshot());

        Assert.Null(resolved.AtUtc);
        Assert.Equal(TerminalInstantAbsence.TransitionEntryUnstamped, resolved.Absence);
    }

    /// <summary>
    /// The <c>transitionIndex == 0</c> guard, which is reachable rather than defensive: a zero-step
    /// snapshot projects terminal off its EMPTY prefix, so no line ever transitioned it. Without the
    /// guard this indexes <c>stamps[-1]</c>, and the throw would surface as the retention sweep's
    /// per-room catch — pruning silently ceasing for that room forever rather than crashing. Reported
    /// as its own absence, never as the legacy-journal one.
    /// </summary>
    [Fact]
    public void Resolve_ZeroStepSnapshot_IsAbsentAsNoTransitionEntry_AndDoesNotThrow()
    {
        var resolved = TerminalInstantResolver.Resolve([], ZeroStepSnapshot());

        Assert.Null(resolved.AtUtc);
        Assert.Equal(TerminalInstantAbsence.NoTransitionEntry, resolved.Absence);
    }

    /// <summary>
    /// The same guard with lines actually in the journal: a step-less execution against a zero-step
    /// snapshot leaves every prefix terminal, including the empty one, so the backwards walk runs all
    /// the way down rather than stopping at the first entry.
    /// </summary>
    [Fact]
    public void Resolve_ZeroStepSnapshotWithJournalLines_StillHasNoTransitionEntry()
    {
        var exec = new ExecutionId("exec-stepless");
        var steplessRequest = new ExecutionRequest(
            exec, new WorkflowId("wf-1"), StepId: null, "human",
            Inputs: [], Outputs: [], Timeout: null, Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var resolved = TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(steplessRequest), Ended.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(exec), Ended),
            ],
            ZeroStepSnapshot());

        Assert.Null(resolved.AtUtc);
        Assert.Equal(TerminalInstantAbsence.NoTransitionEntry, resolved.Absence);
    }

    /// <summary>
    /// Terminality is not monotone — a fresh <see cref="FlowEvent.ExecutionRequestAccepted"/> reopens
    /// the step — so on a re-driven room "first transition" and "last transition" are two different
    /// answers, and only one of them is this run's ending.
    /// </summary>
    [Fact]
    public void Resolve_RoomReopenedAndReTerminalised_ReportsTheSecondEnding()
    {
        var first = new ExecutionId("exec-1");
        var second = new ExecutionId("exec-2");

        var resolved = TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(first)), Ended.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(first), Ended),
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(second)), Later.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(second), Later),
            ],
            SingleStepSnapshot());

        Assert.Equal(Later, resolved.AtUtc);
    }

    /// <summary>
    /// A hand-built entry can carry <see cref="DateTimeKind.Unspecified"/> where the writer's own
    /// <c>DateTime.UtcNow</c> would not: read as UTC, with the ticks untouched.
    /// <para>
    /// <b>Scope, stated because it is easy to over-read.</b> This asserts the ticks are not shifted and
    /// the Kind is Utc. It does NOT discriminate a <c>ToUniversalTime()</c> regression on a UTC host —
    /// where Local and UTC coincide, that mutation is a no-op — and CI is UTC, so no test could. The
    /// resolver's answer is instead structural: <c>SpecifyKind</c> is the only operation on the value,
    /// and there is no Local arm for a mutation to hide in.
    /// </para>
    /// </summary>
    [Fact]
    public void Resolve_UnspecifiedKindStamp_KeepsItsTicksAndReadsAsUtc()
    {
        var exec = new ExecutionId("exec-1");
        var unspecified = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified);

        var resolved = TerminalInstantResolver.Resolve(
            [
                Flow(new FlowEvent.ExecutionRequestAccepted(Request(exec)), unspecified.AddMinutes(-5)),
                Flow(new FlowEvent.ExecutionSucceeded(exec), unspecified),
            ],
            SingleStepSnapshot());

        Assert.Equal(unspecified.Ticks, resolved.AtUtc!.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, resolved.AtUtc!.Value.Kind);
    }
}
