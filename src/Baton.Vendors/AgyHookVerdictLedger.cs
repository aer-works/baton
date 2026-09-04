using Baton.Dispatch;

namespace Baton.Vendors;

/// <summary>
/// #1680's first-verdict canary, read side. <c>AgyHookCheckCommand</c> (the hook subprocess itself)
/// appends one line to this file every time it reaches a verdict — <see cref="AgyWorkerAdapter"/> has
/// no other signal that the hook fired at all: agy's <c>stream-json</c> output carries tool-call
/// activity, but nothing about whether the <c>PreToolUse</c> hook that gates it ever ran (the stream
/// is the worker's own self-report, and a dead hook would not stop the worker from reporting
/// normally). A dedicated ledger file is therefore the only channel, not a convenience.
/// </summary>
public static class AgyHookVerdictLedger
{
    /// <summary>
    /// The number of verdict lines recorded. #1760: delegates to <see cref="HookVerdictLedger.CountLines"/>
    /// so the live dispatch path here and the crash-recovery replay path (<c>MutationInterface</c>)
    /// share the one reader instead of each deciding blank lines, partial trailing lines, and
    /// encoding separately.
    /// </summary>
    public static int CountVerdicts(string? verdictLedgerPath) => HookVerdictLedger.CountLines(verdictLedgerPath);
}
