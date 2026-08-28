namespace Aer.Ui.Core;

/// <summary>
/// The conversation view's read model (M18 Phase 1, #177): one execution's <c>transcript.jsonl</c>
/// projected line by line, in file order — UI spec §10.1 names file order as the order the turns
/// actually happened, so no reordering (by <see cref="TranscriptLine.Turn.Sequence"/> or anything
/// else) is ever applied. Built exclusively from the durable artifact directory
/// (<see cref="TranscriptProjectionLoader"/>), never from worker types: the contract lives in the
/// spec.
/// </summary>
public sealed record TranscriptProjection(IReadOnlyList<TranscriptLine> Lines);

/// <summary>
/// One line of a projected transcript — either a schema-valid turn or an explicit malformed-line
/// marker. The marker exists so a damaged line (a torn final line from a crash mid-append, or any
/// line missing §10.1's required fields) renders honestly in place instead of being silently
/// skipped or failing the whole projection — the same render-the-damage-honestly discipline
/// <see cref="DecisionRecord"/>'s <c>Resolved</c> flag applies to the decision/resume crash window
/// (UI spec §11 determinism, §12 transparency: what is shown is exactly what is durably there).
/// </summary>
public abstract record TranscriptLine
{
    private TranscriptLine()
    {
    }

    /// <summary>
    /// A schema-valid turn per UI spec §10.1's reader contract: at least these five fields, extra
    /// fields ignored. Field meanings are the contract's, not any worker's: <paramref name="Role"/>
    /// is the participant's logical name (never a vendor name), <paramref name="Vendor"/> is who
    /// played it for this turn, <paramref name="Prompt"/> is the exact prompt sent, and
    /// <paramref name="Text"/> is the turn text produced.
    /// </summary>
    public sealed record Turn(int Sequence, string Role, string Vendor, string Prompt, string Text) : TranscriptLine;

    /// <summary>
    /// A line that is not a schema-valid turn: unparseable JSON, a non-object, or an object missing
    /// (or mistyping) a §10.1 required field. Carries only the 1-based line number — the raw bytes
    /// stay on disk for anyone tracing the projection back to its durable origin (§12), and the
    /// view has nothing else it could honestly render.
    /// </summary>
    public sealed record Malformed(int LineNumber) : TranscriptLine;
}
