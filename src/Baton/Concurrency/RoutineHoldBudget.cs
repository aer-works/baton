namespace Baton.Concurrency;

/// <summary>
/// #1646/#1650: how long a sibling <c>baton</c> command waits out a room resource another process
/// is holding <em>routinely</em> — as opposed to anomalously — before refusing.
/// <para>
/// One number, one canonical rationale, because the three call sites that use it are waiting out the
/// same physical event: a live <c>baton run</c> pump's exit tail. That pump appends
/// <c>WorkflowPaused</c> and fsyncs it (so a status reader — or a script driving <c>run --wait</c>
/// then <c>decide</c> — observes Paused immediately), but it still has to loop once more, re-project,
/// find nothing further ready, release <c>flow.lock</c>, and only then dispose its
/// <c>FlowEventLogWriter</c>. That tail is a re-projection with no I/O — sub-millisecond in the
/// ordinary case — yet it is long enough that a <c>decide</c> fired the instant Paused became
/// observable can lose to it, twice within one hour on CI under load (#1646's measured failures).
/// The three points a losing command hits, in the order the pump releases them:
/// </para>
/// <list type="number">
/// <item><c>flow.lock</c>, contended by <c>WorktreeWorkspaces.Walk</c>'s provisioning acquire when a
/// binding actually declares a worktree.</item>
/// <item><c>flow.lock</c> again, contended by <c>MutationInterface.RecordDecisionAsync</c>'s own
/// mutation guard.</item>
/// <item><c>flow.jsonl</c>'s append handle, contended by <c>FlowEventLogWriter</c>'s open — released
/// strictly <em>after</em> the lock, so this is the last of the three to clear and the one that
/// bounds the window.</item>
/// </list>
/// <para>
/// Two seconds is generous for a tail measured in milliseconds and still short enough that a
/// genuinely stuck holder — a second live pump mid-step, not a pump on its way out — surfaces as a
/// refusal rather than being waited on for the length of that step. It is the same size, and for the
/// same "routine overlap" reason, as <see cref="ConcurrencyGuard.AcquireWithin"/>'s own doc describes
/// and as <c>Baton.Mutation.MemoryProposalResolution</c>'s <c>LockContentionBudget</c> already applies
/// to the analogous room-events lock — deliberately <em>not</em> merged with that one, because its
/// number is sized against a different population of holders (see its own remarks for the two it is
/// not sized against) and collapsing two rationales onto one constant is the drift record-once exists
/// to stop, not an instance of it.
/// </para>
/// <para>
/// <b>This is a per-site bound, not a per-command one, and the sites compound.</b> A single
/// <c>baton decide</c> against a binding that declares a worktree can wait at all three in sequence —
/// the provisioning walk, then the writer open, then the mutation guard — so its worst case before
/// <em>succeeding</em> is roughly three times this value, plus <c>FileHolderProbe.DescribeHolders</c>
/// (hundreds of milliseconds) at whichever site ends up giving up. The worktree-free shape, which is
/// the common one, waits at two. Stated because the whole point of naming a budget is to stop a
/// reader having to infer the latency it implies, and inferring it from this number alone is off by
/// up to 3x.
/// </para>
/// <para>
/// Not load-tested at this size: the budget was chosen to swamp the tail above by three orders of
/// magnitude rather than fitted to a distribution. Said here rather than left as an assumption
/// hiding inside a number.
/// </para>
/// </summary>
public static class RoutineHoldBudget
{
    /// <summary>See this type's own remarks for what this is sized against and what it is not.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);
}
