using System.Linq;

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
    /// The number of verdict lines recorded, or 0 when the file does not exist (the hook never ran,
    /// or no grant asked for the ledger). Read rather than trusted: a concurrent writer mid-append
    /// could leave a final partial line, so this counts complete, non-empty lines only — an
    /// undercount on a torn write, never an overcount, which keeps this canary's failure direction
    /// fail-closed (a torn-line undercount can only push a run toward Indeterminate, not away from it).
    /// </summary>
    public static int CountVerdicts(string? verdictLedgerPath)
    {
        if (string.IsNullOrWhiteSpace(verdictLedgerPath) || !File.Exists(verdictLedgerPath))
        {
            return 0;
        }

        try
        {
            return File.ReadLines(verdictLedgerPath).Count(line => line.Trim().Length > 0);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
