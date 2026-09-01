namespace Baton.Outcomes;

/// <summary>
/// An optional capability provided by worker adapters (#1594) to recover a worker's own final
/// answer from its terminal stream envelope. Exists for exactly one consumer,
/// <see cref="OutputMaterializer"/>: a worker that did the work but never wrote its declared output
/// file still said something on its way out, and that response is the only recovery an engine that
/// never reads conversation content (CLAUDE.md's Architecture Rule 1) is allowed to reach for.
/// </summary>
public interface IWorkerResponseParser
{
    /// <summary>
    /// Attempts to interpret one raw stdout line — the last non-blank line of a completed execution's
    /// captured stream — as this vendor's terminal response text. False (and a null
    /// <paramref name="response"/>) for a line this vendor doesn't recognize, a non-terminal line, an
    /// error turn, or a terminal line whose response text is empty — an adapter that has nothing to
    /// report must not fabricate one.
    /// </summary>
    bool TryParseFinalResponse(string rawLine, out string? response)
    {
        response = null;
        return false;
    }
}
