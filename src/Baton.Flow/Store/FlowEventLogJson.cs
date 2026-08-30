using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Flow.Store;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> every <c>flow.jsonl</c> line is written and read
/// with (#604). Shared rather than defaulted per call site, because a journal written under one
/// configuration and read under another is not a round trip — it is data loss discovered later.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enums persist by name, not by ordinal.</b> Without <see cref="JsonStringEnumConverter"/>,
/// <c>FailureClassification.Permanent</c> was stored as <c>1</c> and <c>CoreExitReason.Natural</c>
/// as <c>0</c>. That makes every historical line's meaning depend on the declaration order of an
/// enum in this repo: insert a member above <c>TimedOut</c>, or swap two, and every journal already
/// on disk silently reinterprets — a stored <c>Permanent</c> reads back as <c>Retryable</c>, and
/// AER retries an execution a worker explicitly declared not worth retrying. Nothing in the type
/// system, the tests, or the persistence layer prevented that edit, and it would have produced no
/// error at any point. It is the quietest failure available to an event-sourced store.
/// </para>
/// <para>
/// <b>Reading is deliberately more permissive than writing, and only in this one direction.</b>
/// <see cref="JsonStringEnumConverter"/> accepts a number as well as a name, so journals written
/// before #604 — which carry ordinals — keep replaying unchanged. That is what makes this a
/// widening of the reader rather than a breaking change to durable data, and it is why no migration
/// step exists: there is nothing to migrate, because nothing stops being readable.
/// </para>
/// <para>
/// <b>A missing required member fails loudly.</b>
/// <see cref="JsonSerializerOptions.RespectRequiredConstructorParameters"/> makes a constructor
/// parameter with no default value genuinely required. Before this, a line that had lost its
/// <c>ExecutionId</c> — to a truncated write, a partial fsync, or a member renamed in a later
/// version — deserialized happily into an event for execution <c>""</c>, which then took part in
/// projection as though it were real. Silent state corruption is strictly worse than a loud
/// <see cref="JsonException"/> naming the bad line, because the loud one is recoverable;
/// <see cref="FlowEventLogReader"/> already wraps it into a
/// <see cref="FlowEventLogReadException"/> carrying the offending text.
/// </para>
/// <para>
/// <b>What stays permitted, and why the distinction is the whole point.</b> A trailing parameter
/// with a default is <i>optional</i>, so a line predating it still replays and the member defaults —
/// the additive compatibility #597 deliberately relied on when it added <c>Reason</c>. Required
/// means "declared with no default", which is orthogonal to being nullable: <c>ExecutionFailed</c>'s
/// <c>FailureClassification?</c> is a nullable type and a required member, and a line omitting it is
/// now rejected while a line omitting <c>Reason</c> is not. Adding a member is safe; removing or
/// renaming one is not; those were the same code path before #604 and neither threw.
/// </para>
/// <para>
/// <b>Do not set <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> on these options.</b> It
/// is left at <c>Never</c>, and that is the only reason a null required member is written at all —
/// <c>"FailureClassification":null</c> is emitted rather than omitted. Setting
/// <c>WhenWritingNull</c> here would stop the writer emitting it and the reader would immediately
/// reject the lines it had just written: a store that cannot read its own output. On a wire frame
/// absence and null mean the same thing, so <c>WhenWritingNull</c> is the natural choice there and
/// copy-pasting such options here looks harmless; here absence means damage. A test pins this
/// setting so the mistake fails loudly instead of corrupting the journal.
/// </para>
/// </remarks>
public static class FlowEventLogJson
{
    /// <summary>The journal's wire contract. Never construct a second one — see this type's remarks.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
    };
}
