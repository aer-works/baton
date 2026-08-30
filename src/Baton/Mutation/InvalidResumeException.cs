namespace Baton.Mutation;

/// <summary>
/// Raised when <c>baton resume</c> (issue #1359) is asked to continue a worker that cannot be
/// continued right now: no step is bound to the named worker role, more than one step names it (an
/// ambiguous target this verb refuses rather than guesses), the target step has no execution yet
/// (<see cref="Domain.StepStatus.Pending"/>), the target step's latest attempt is still
/// <see cref="Domain.StepStatus.Running"/> under a live engine (mid-flight steering is #1359's
/// explicitly excluded scope — only a terminal or stalled worker is a resume target; a
/// <see cref="Domain.StepStatus.Running"/> step whose engine is provably dead is STALLED instead, and
/// is allowed rather than refused, per F3), the resolved binding is not a
/// <see cref="WorkerBinding.Process"/> (a non-process worker has no vendor session to resume), or the
/// worker's worktree workspace no longer exists on disk (F1 — <c>Baton.Vendors.WorktreeWorkspaces.ReuseForResume</c>,
/// thrown before any dispatch is even attempted), or the bindings file's recorded session id
/// disagrees with the one the execution being resumed already recorded (F6). Nothing is appended to
/// the log when this is thrown, EXCEPT the STALLED takeover branch above, which appends the
/// abandoned prior execution's own outcome before throwing on some other, later condition.
/// </summary>
public sealed class InvalidResumeException : BatonFlowException
{
    public InvalidResumeException(string message)
        : base(message)
    {
    }
}
