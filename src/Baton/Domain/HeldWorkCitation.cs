namespace Baton.Domain;

/// <summary>
/// Cites the thing a held-work resolution was decided on, without copying its content: for a workflow
/// resolution, the workflow's terminal <c>flow.jsonl</c> event; for a resolution with no workflow (a memory
/// proposal), the held-work ref itself. <see cref="Subject"/> is a free string, deliberately NOT an
/// <see cref="ExecutionId"/> — a citation records what was decided on, it is not the join key into
/// Core-owned events that <see cref="ExecutionId"/> exists to be (#855). <see cref="LineIndex"/> is
/// the line in a workflow journal when there is one, and null otherwise.
/// </summary>
public sealed record HeldWorkCitation(
    string Subject,
    string EventType,
    int? LineIndex = null);
