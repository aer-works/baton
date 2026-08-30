namespace Baton.Domain;

/// <summary>
/// The kind of external decision recorded in response to a paused workflow (spec/baton.md §5). A small,
/// closed set Flow itself acts on differently — not an open string.
/// </summary>
public enum DecisionType
{
    /// <summary>Proceed as if the referenced execution's outcome stands.</summary>
    Resume,

    /// <summary>Treat the referenced step as terminally failed; do not evaluate its dependents.</summary>
    Reject,

    /// <summary>The referenced step, which has not yet succeeded, should attempt again.</summary>
    RetryWithRevision,

    /// <summary>A step that has already succeeded should be re-executed with new information.</summary>
    Supersede,
}
