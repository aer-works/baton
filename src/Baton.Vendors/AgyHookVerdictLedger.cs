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
    /// or no grant asked for the ledger). Counts every non-whitespace line, including a torn
    /// <b>partial</b> one (#1732 review): a mid-append crash cannot produce a line whose content
    /// exists before the verdict that produced it was reached, so counting a torn line still counts a
    /// real verdict — correct, not merely harmless. The actual undercount risk is a different
    /// mechanism: two hook subprocesses appending to the same per-execution file concurrently can each
    /// lose a whole line to a sharing violation, which <see cref="File.ReadLines"/> and the
    /// <c>catch (IOException)</c> below both swallow as "count 0 for that read" rather than surface.
    /// Either way the failure direction stays fail-closed — an undercount can only push a run toward
    /// Indeterminate, never away from it.
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
