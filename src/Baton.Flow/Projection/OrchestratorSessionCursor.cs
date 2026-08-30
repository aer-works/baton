using System.Text.Json.Serialization;

namespace Baton.Flow.Projection;

/// <summary>
/// Small engine session metadata record stored at <c>{room}/.baton/orchestrator-session.json</c>.
/// Holds the count of room events already processed by the last completed turn, the wall-clock of that turn,
/// and a content-identity SHA-256 hex hash of the serialized line of the last event counted (#972).
/// Never recorded as a room event (0016 boundary).
/// <para>
/// Cold start (missing or corrupt cursor file) reconstructs state from the room record alone.
/// Conversational nuance since the last recorded state may be lost — that is the DESIGN.
/// </para>
/// <para>
/// <b>Landmine fix (#972):</b> The cursor carries content identity via <see cref="LastEventLineHash"/>, a
/// 64-character lowercase SHA-256 hex string of the serialized line of the event at index
/// <c>ProcessedEventCount - 1</c>. A cursor with <c>ProcessedEventCount == 0</c> carries no hash (<c>null</c>).
/// </para>
/// <para>
/// <b>Why identity rather than reset-on-compaction:</b> A self-validating cursor is fail-loud against EVERY
/// rewriter, including the archival path (#973) and anything later. Reset-on-compaction only works for the
/// rewriter that remembers to call it, which is a rule in prose rather than a structural guarantee.
/// </para>
/// <para>
/// <b>Persisted-shape change rule:</b> An absent or null hash on a cursor with <c>ProcessedEventCount > 0</c>
/// (e.g. legacy cursor file written before #972) is treated as unverifiable and triggers a cold start (fail-closed).
/// </para>
/// </summary>
public sealed record OrchestratorSessionCursor(
    [property: JsonPropertyName("processedEventCount")] int ProcessedEventCount,
    [property: JsonPropertyName("lastCompletedTurnAt")] DateTimeOffset LastCompletedTurnAt,
    [property: JsonPropertyName("lastEventLineHash")] string? LastEventLineHash = null);
