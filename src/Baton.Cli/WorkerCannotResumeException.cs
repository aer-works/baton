using Baton;

namespace Baton.Cli;

/// <summary>
/// <c>baton resume</c> (issue #1359) refuses loudly rather than silently starting cold when the
/// worker it was asked to continue cannot actually be resumed — the design ruling every worker
/// implementing this issue is told to hold: "REFUSE loudly with a Try: line — never silently start
/// cold." Today's one trigger is a bindings entry with no <c>SessionId</c> recorded: an ordinary
/// <c>baton dispatch</c> mints none (see below for why), so resuming one requires the operator to have
/// already recorded one on the worker's bindings entry — captured from a prior invocation's own
/// transcript/logs, or from a chain of <c>baton dispatch --continue</c> dispatches (#1381), each of
/// which records the session id it resumed onto its OWN room's bindings entry in turn.
/// <para>
/// Why an ordinary dispatch mints no session id automatically: <c>WorkerInvocation</c>/the resolved
/// <c>CoreDispatchTarget</c> argv is built once per binding and reused verbatim across every retry of
/// that binding (#1373); minting a client-side <c>--session-id</c> at bind time would bake it into that
/// frozen argv, and Claude's own <c>--session-id</c> reuse is existence-guarded (sequential reuse
/// refused — docs/vendor-doc-audit.md, sentinel <c>durability.session-id-guard-is-not-a-lock</c>), so a
/// second attempt after a timeout would fail outright rather than merely restart cold. #1381 scoped
/// around this by minting nothing on the ordinary path and only ever carrying an id FORWARD through an
/// explicit <c>--continue</c> chain (which dispatches with <c>--resume</c>, not <c>--session-id</c> —
/// not existence-guarded the same way). Automatic capture on every dispatch is unshipped follow-up work.
/// </para>
/// </summary>
public sealed class WorkerCannotResumeException : BatonFlowException
{
    public WorkerCannotResumeException(string message, string tryInvocation)
        : base(message)
    {
        TryInvocation = tryInvocation;
    }
}
