namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton room delete</c> (#1659).
/// </summary>
/// <param name="RoomDirectoryPath">
/// record-once-ok: #443 src/Baton.Cli/CancelOptions.cs
/// An already-started room's durable state directory — this verb never binds a fresh snapshot.
/// </param>
/// <param name="KeepDeliverables">
/// <c>--keep-deliverables</c>: skip writing the room's entry to the deleted-rooms tombstone
/// (<see cref="Baton.Vendors.DeletedRoomsTombstoneStore"/>), so its already-pushed deliverables are
/// left standing in the inbox rather than queued for removal there.
/// </param>
/// <param name="Force">
/// <c>--force</c>: delete even when the room has not reached a terminal state — see
/// <see cref="RoomDeleteCommand"/>'s refusal for why this is refused by default.
/// </param>
public sealed record RoomDeleteOptions(string RoomDirectoryPath, bool KeepDeliverables, bool Force);
