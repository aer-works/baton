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

    /// <summary>
    /// #1623: attempts to read usage from a live, not-yet-terminal stdout line (e.g. claude's mid-stream
    /// <c>"type":"assistant"</c> <c>message.usage</c>, agy's <c>"step_update"</c> DONE-state
    /// <c>usage</c>) — for a running token budget evaluated as usage arrives, never a replacement for
    /// <see cref="TryParseFinalUsage"/>'s own terminal-line read. Each matching line reports that one
    /// turn's own usage, but the two output fields on <see cref="WorkerUsage"/> are NOT symmetric: the
    /// output side (<c>TokensOut</c>) is additive — a caller sums across calls. The input side
    /// (<c>TokensIn</c> + <c>CacheReadTokens</c> + <c>CacheCreationTokens</c>) is a LEVEL — a vendor's
    /// own <c>input_tokens</c> for a turn already restates the whole context sent that turn, so a
    /// caller replaces its running input total with each new reading rather than adding to it; summing
    /// it the way output is summed double-counts a long conversation's context on every turn.
    /// <see cref="Baton.Mutation.TokenBudgetMonitor"/> is the worked example of both halves together.
    /// Default false/null: a parser that only supports the final-usage read (a test double, a future
    /// vendor) opts out cleanly rather than being forced to implement this.
    /// </summary>
    bool TryParseIncrementalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        return false;
    }

    /// <summary>
    /// #1623: the tool name a live stdout line names, if any (e.g. agy's <c>step_update.tool_name</c>).
    /// Independent of <see cref="TryParseIncrementalUsage"/> — a line can report one, both, or neither.
    /// Default null.
    /// </summary>
    string? TryParseToolName(string rawLine) => null;

    /// <summary>
    /// #1682: how many tool-step events <paramref name="rawLine"/> itself reports — the quantity
    /// <c>Mutation.TokenBudgetMonitor</c>'s tool-step cap accumulates, independently of whether
    /// <see cref="TryParseIncrementalUsage"/> matches anything on the same line (the cap must still
    /// fire on a stream with malformed or entirely absent usage lines). Deliberately NOT
    /// <see cref="TryParseToolName"/> reused as a 0/1 count: that method exists to report ONE display
    /// name per line and, for claude, returns only the first <c>tool_use</c> block of a multi-tool
    /// turn — undercounting exactly the shape this cap exists to catch. A caller sums this across every
    /// line of the stream; each vendor's own doc comment on its implementation states what one line
    /// counts as. Default 0: a parser that reports no tool-step signal (a test double, a future vendor)
    /// opts out cleanly rather than being forced to implement this.
    /// </summary>
    int CountToolSteps(string rawLine) => 0;
}
