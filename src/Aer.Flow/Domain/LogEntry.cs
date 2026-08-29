using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// The physical line union for the single combined <c>flow.jsonl</c> file (M7 Phase 6's dual-log
/// ownership decision — spec/baton.md §2 defines two logical logs but leaves the storage backend
/// implementation-defined; a later merge into one physical store is explicitly permitted as long
/// as "each log has exactly one writer role" still holds per event type). Wrapping
/// <see cref="FlowEvent"/> and <see cref="CoreEvent"/> in distinct, non-interchangeable
/// <see cref="LogEntry"/> cases is what enforces that ownership rule in the type system rather
/// than by physical file separation: nothing can construct a <see cref="CoreLogEntry"/> around a
/// <see cref="FlowEvent"/> or vice versa.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "owner")]
[JsonDerivedType(typeof(FlowLogEntry), "flow")]
[JsonDerivedType(typeof(CoreLogEntry), "core")]
[JsonDerivedType(typeof(RoomLogEntry), "room")]
public abstract record LogEntry
{
    private LogEntry()
    {
    }

    /// <summary>A line written by Flow's own mutation logic (spec/baton.md §2's <c>flow.jsonl</c> owner).</summary>
    public sealed record FlowLogEntry(FlowEvent Event, DateTime? WriterUtcTimestamp = null) : LogEntry;

    /// <summary>
    /// A line written by the Core Dispatcher on Core's behalf — Flow never originates these, it
    /// only durably records what Core reported.
    /// </summary>
    public sealed record CoreLogEntry(CoreEvent Event, DateTime? WriterUtcTimestamp = null) : LogEntry;

    /// <summary>A line wrapping a room event (spec/baton.md §2's <c>room.jsonl</c> log).</summary>
    public sealed record RoomLogEntry(RoomEvent Event, DateTime? WriterUtcTimestamp = null) : LogEntry;
}

