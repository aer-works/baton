using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Vendors;

/// <summary>
/// One <c>baton room delete</c>/<c>baton rooms prune</c> removal, recorded so the deliverables inbox
/// can catch up with it later (see <see cref="DeletedRoomsTombstoneStore"/>'s own remarks for why this
/// exists at all).
/// </summary>
public sealed record DeletedRoomTombstone(
    [property: JsonPropertyName("roomPath")] string RoomPath,
    [property: JsonPropertyName("deletedAt")] DateTime DeletedAt);

/// <summary>
/// Appends to <see cref="Baton.Status.BatonPaths.DeletedRoomsFile"/> (#1659).
/// </summary>
/// <remarks>
/// <b>Why this exists instead of the CLI just deleting the deliverable.</b> A room's deliverables live
/// in the Cloudflare Worker's KV inbox (<c>tools/fleet-glass/worker.js</c>'s <c>/deliver</c> route,
/// populated by <c>pusher.py</c> scanning terminal rooms) — <c>baton</c> is a local process with no
/// network call into that Worker and no delete route to call even if it did (<c>handleDeliver</c> only
/// ever upserts <c>inbox:item:&lt;id&gt;</c> entries; nothing there accepts a removal). So the CLI does
/// the one thing it actually can: record the deletion locally, in the same append-only-JSONL, one-line-
/// per-fact shape <see cref="Baton.Vendors.RoomRegistryStore"/> already uses for
/// <c>room-registry.jsonl</c>. Reading this file and forwarding each entry to the Worker as a
/// <c>/deliver</c> removal is unbuilt — the Worker's <c>/deliver</c> route has no removal verb to
/// forward TO yet either — so a deleted room's deliverables remain visible in the inbox until that pair
/// lands; see report-1659.md and the PR body for what "delete" does not yet reach.
/// <para>
/// <b>Best-effort, never gates the delete.</b> Mirrors <see cref="RoomRegistryStore"/>'s fail-open
/// contract: a write failure here must not stop <c>baton room delete</c> from removing the room
/// directory and registry lines it already succeeded at — it is logged to stderr and swallowed.
/// </para>
/// <para>
/// <b>Not behind the same named <see cref="Mutex"/> as <see cref="RoomRegistryStore"/>.</b> Deletes are
/// operator-driven, one-at-a-time-per-process commands, not a fleet of concurrent dispatchers racing to
/// append — the concurrency pressure that justifies <c>RoomRegistryStore</c>'s cross-process mutex does
/// not apply here. A per-process exclusive <see cref="FileStream"/> open (the same <c>FileShare.Read</c>
/// choice <see cref="RoomRegistryStore.AppendAsync"/> makes) is enough to avoid a torn write within one
/// invocation; a lost line from two genuinely concurrent <c>baton room delete</c> processes racing the
/// same room is an accepted, narrow gap given the fail-open contract above.
/// </para>
/// </remarks>
public static class DeletedRoomsTombstoneStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// Appends one tombstone line for <paramref name="roomPath"/> to <paramref name="tombstoneFilePath"/>,
    /// creating the file and its parent directory if neither exists yet. Never throws: an
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> is reported on stderr and
    /// swallowed, returning <c>false</c>, matching <see cref="RoomRegistryStore"/>'s fail-open contract.
    /// </summary>
    public static async Task<bool> AppendAsync(
        string roomPath, string tombstoneFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomPath);
        ArgumentException.ThrowIfNullOrEmpty(tombstoneFilePath);

        var entry = new DeletedRoomTombstone(Baton.Status.BatonPaths.RecordKey(roomPath), DateTime.UtcNow);
        var line = JsonSerializer.Serialize(entry, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        try
        {
            var directory = Path.GetDirectoryName(tombstoneFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(
                tombstoneFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not record the deleted-room tombstone for '{roomPath}': {ex.Message}");
            return false;
        }
    }
}
