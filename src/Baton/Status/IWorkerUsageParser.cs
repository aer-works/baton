using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Parser interface for extracting terminal worker usage from captured stdout.
/// </summary>
public interface IWorkerUsageParser
{
    /// <summary>
    /// Attempts to interpret one raw stdout line — the last non-blank line of a completed execution's
    /// captured stream — as this vendor's terminal usage report (issue #1360).
    /// </summary>
    bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        return false;
    }
}
