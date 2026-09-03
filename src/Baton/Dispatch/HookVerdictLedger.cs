namespace Baton.Dispatch;

/// <summary>
/// #1680's first-verdict canary, read side — the one reader shared by the live dispatch path
/// (<see cref="CoreDispatchTarget.CountHookVerdicts"/>, via <c>AgyWorkerAdapter</c>) and the
/// crash-recovery replay path (<c>MutationInterface</c>'s counting block, #1741/#1753). Both used to
/// read the same on-disk ledger with their own copy of this logic — #1760 collapses that to one
/// primitive so blank-line handling, partial trailing lines, and encoding are decided once.
/// </summary>
public static class HookVerdictLedger
{
    /// <summary>
    /// The number of verdict lines recorded, or 0 when the path is null/blank or the file does not
    /// exist (the hook never ran, or no grant asked for the ledger). Counts every non-whitespace
    /// line, including a torn <b>partial</b> one (#1732 review): a mid-append crash cannot produce a
    /// line whose content exists before the verdict that produced it was reached, so counting a torn
    /// line still counts a real verdict — correct, not merely harmless. The actual undercount risk is
    /// a different mechanism: two hook subprocesses appending to the same per-execution file
    /// concurrently can each lose a whole line to a sharing violation, which <see cref="File.ReadLines(string)"/>
    /// and the <c>catch (IOException)</c> below both swallow as "count 0 for that read" rather than
    /// surface. Either way the failure direction stays fail-closed — an undercount can only push a run
    /// toward Indeterminate, never away from it.
    /// </summary>
    public static int CountLines(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 0;
        }

        try
        {
            return File.ReadLines(path).Count(line => line.Trim().Length > 0);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
