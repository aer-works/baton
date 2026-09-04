using Baton;

namespace Baton.Cli;

/// <summary>
/// <c>baton resume</c> (issue #1359) refuses loudly rather than silently starting cold when the
/// worker it was asked to continue cannot actually be resumed — the design ruling every worker
/// implementing this issue is told to hold: "REFUSE loudly with a Try: line — never silently start
/// cold." Today's one trigger is a bindings entry with no <c>SessionId</c> recorded: an ordinary
/// <c>baton dispatch</c> mints none automatically, so resuming one requires the operator to have
/// already recorded one on the worker's bindings entry — captured from a prior invocation's own
/// transcript/logs, or from a chain of <c>baton dispatch --continue</c> dispatches (#1381), each of
/// which records the session id it resumed onto its OWN room's bindings entry in turn. Why an
/// ordinary dispatch mints nothing automatically, and why that is deliberate: spec/baton.md §3's
/// dispatch entry.
/// </summary>
public sealed class WorkerCannotResumeException : BatonFlowException
{
    public WorkerCannotResumeException(string message, string tryInvocation)
        : base(message)
    {
        TryInvocation = tryInvocation;
    }
}
