using Baton.Flow.Domain;

namespace Baton.Flow.Projection;

/// <summary>
/// The moment a workflow step entered a paused state (#1197), so the room's one chronology can
/// place the decision beside the work that raised it (#1196).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PausedAt"/> is nullable because the journal envelope's writer stamp is: a room whose
/// log predates it still loads, with the moment present and its time unknown — an honest gap rather
/// than a fabricated instant, the same rule 0026 applies to a reset time nobody reported.
/// </para>
/// <para>
/// These moments live only in <c>flow.jsonl</c>, which has no compactor — the repo's only one is
/// <c>RoomJournalCompactor</c>, and it opens <c>room.jsonl</c> exclusively (checked 2026-08-13,
/// #1197's review). Worth re-asking the day <c>flow.jsonl</c> grows one: a compaction that drops
/// these drops a room's decision history out of its own transcript.
/// </para>
/// </remarks>
public sealed record StepPauseMoment(
    ExecutionId ExecutionId,
    StepId StepId,
    DateTimeOffset? PausedAt);
