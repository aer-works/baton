using Baton;

namespace Baton.Cli;

/// <summary>
/// <c>baton resume</c> (issue #1359) refuses loudly rather than silently starting cold when the
/// worker it was asked to continue cannot actually be resumed — the design ruling every worker
/// implementing this issue is told to hold: "REFUSE loudly with a Try: line — never silently start
/// cold." Today's one trigger is a bindings entry with no <c>SessionId</c> recorded: adapters do not
/// yet capture a vendor session id into the room ledger on their own (that capture is baton-works/baton#1381's
/// separate ask), so resuming a real dispatch requires the operator to have already recorded one —
/// captured from a prior invocation's own transcript/logs — on the worker's bindings entry.
/// </summary>
public sealed class WorkerCannotResumeException : BatonFlowException
{
    public WorkerCannotResumeException(string message, string tryInvocation)
        : base(message)
    {
        TryInvocation = tryInvocation;
    }
}
