using Aer.Flow.Domain;

namespace Aer.Flow.Store;

/// <summary>
/// Appends room events to <c>room.jsonl</c> (#798).
/// Owner tag is <c>"owner": "room"</c> on the <see cref="LogEntry"/> envelope.
/// </summary>
public interface IRoomEventLogWriter
{
    /// <summary>
    /// Appends <paramref name="roomEvent"/> durably to <c>room.jsonl</c>.
    /// </summary>
    Task AppendAsync(RoomEvent roomEvent, CancellationToken cancellationToken = default);
}
