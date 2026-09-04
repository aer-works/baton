using Baton;

namespace Baton.Cli;

/// <summary>
/// <c>baton resume</c> (issue #1359) refuses loudly rather than silently starting cold when the
/// worker it was asked to continue cannot actually be resumed — the design ruling every worker
/// implementing this issue is told to hold: "REFUSE loudly with a Try: line — never silently start
/// cold." Today's one trigger is a bindings entry with no <c>SessionId</c> recorded. An ordinary
/// Claude <c>baton dispatch</c> records the session id the worker reports in its stream (#1841),
/// while an unmeasured adapter or a stream that reports no id leaves the field absent. A chain of
/// <c>baton dispatch --continue</c> dispatches (#1381) also carries the resumed id onto each new
/// room's own binding. Why Baton captures rather than mints the id: spec/baton.md §3's dispatch entry.
/// </summary>
public sealed class WorkerCannotResumeException : BatonFlowException
{
    public WorkerCannotResumeException(string message, string tryInvocation)
        : base(message)
    {
        TryInvocation = tryInvocation;
    }
}
