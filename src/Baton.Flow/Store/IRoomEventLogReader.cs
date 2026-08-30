using Baton.Flow.Domain;

namespace Baton.Flow.Store;

/// <summary>
/// Reads room events from <c>room.jsonl</c> back into memory.
/// </summary>
public interface IRoomEventLogReader
{
    /// <summary>
    /// Returns every complete <see cref="RoomEvent"/> currently in <c>room.jsonl</c>.
    /// </summary>
    Task<IReadOnlyList<RoomEvent>> ReadAllRoomEventsAsync(CancellationToken cancellationToken = default);
}
