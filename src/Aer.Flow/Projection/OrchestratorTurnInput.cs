using Aer.Flow.Domain;
using Aer.Flow.Store;

namespace Aer.Flow.Projection;

/// <summary>
/// Assembles what one orchestrator turn reads:
/// <list type="bullet">
///   <item><description>The projected <see cref="RoomState"/> (carrying ActiveGrants + OpenEscalations).</description></item>
///   <item><description>The event delta: room events appended since the last completed turn cursor.</description></item>
///   <item><description>The current wake set passed in from the bridge.</description></item>
///   <item><description>The <see cref="RoomMemoryDocument"/>.</description></item>
/// </list>
/// <para>
/// <b>Re-schedulable Turns:</b> Advancing the cursor is a separate explicit call
/// (<see cref="CommitTurn"/>), and it takes THIS input — never a caller-computed count — so the
/// only committable value is what the turn actually read (#778 review: a bare int invited
/// committing events no turn ever saw, silently dropping them from every future delta).
/// A crashed turn must NOT advance the cursor so that the next wake replays the same event delta.
/// </para>
/// <para>
/// The journal read and the memory-document read are two reads with no shared lock: a guarded
/// mutation can land between them, so the memory document may be one step ahead of (or behind)
/// <see cref="EventDelta"/>. Benign by design: a turn input is a best-effort snapshot, every
/// action a turn takes goes through the guarded mutation surfaces that re-project under the room
/// lock, and the next wake re-assembles fresh.
/// </para>
/// </summary>
public sealed record OrchestratorTurnInput(
    RoomState RoomState,
    IReadOnlyList<RoomEvent> EventDelta,
    IReadOnlyList<RoomWake> Wakes,
    RoomMemoryDocument MemoryDocument,
    OrchestratorSessionCursor? InitialCursor,
    bool IsColdStart,
    int TotalEventCount,
    string? RoomDirectoryPath = null,
    string? LastEventLineHash = null)
{
    private const string RoomLogFileName = "room.jsonl";

    /// <summary>
    /// Assembles an <see cref="OrchestratorTurnInput"/> from <paramref name="roomDirectoryPath"/> and the passed-in <paramref name="wakes"/>.
    /// Reads the room event journal ONCE for both state projection and delta extraction.
    /// </summary>
    public static async Task<OrchestratorTurnInput> AssembleAsync(
        string roomDirectoryPath,
        IReadOnlyList<RoomWake> wakes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(wakes);

        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        var reader = new RoomEventLogReader(roomLogPath);
        var allEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var roomState = RoomProjector.Project(allEvents);
        var memoryDoc = await RoomMemoryDocument.LoadAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);

        var cursor = OrchestratorSessionStore.Load(roomDirectoryPath);

        bool isColdStart;
        IReadOnlyList<RoomEvent> eventDelta;

        if (cursor is null)
        {
            isColdStart = true;
            eventDelta = allEvents;
        }
        else if (cursor.ProcessedEventCount > allEvents.Count)
        {
            Console.Error.WriteLine(
                $"[OrchestratorTurnInput] Fallback to cold start LOUDLY: Cursor processed count ({cursor.ProcessedEventCount}) exceeds journal length ({allEvents.Count}).");
            isColdStart = true;
            eventDelta = allEvents;
        }
        else
        {
            isColdStart = false;
            eventDelta = allEvents.Skip(cursor.ProcessedEventCount).ToList().AsReadOnly();
        }

        // Hashed HERE, not at commit: the same rule the class remarks give for the count applies to
        // its identity, and a turn can run for minutes between the two. Commit-time re-reading would
        // hash whatever now sits at that index, which a rewriter (compaction, #1025) can make a
        // different event -- and a cursor that then validates against the file it was just written
        // from validates tautologically, which is the landmine wearing the fix's clothes.
        string? lastEventLineHash = null;
        if (allEvents.Count > 0)
        {
            var lines = OrchestratorSessionStore.ReadRoomLogLines(roomDirectoryPath);
            if (lines.Length >= allEvents.Count)
            {
                lastEventLineHash = OrchestratorSessionStore.ComputeLineHash(lines[allEvents.Count - 1]);
            }
        }

        return new OrchestratorTurnInput(
            RoomState: roomState,
            EventDelta: eventDelta,
            Wakes: wakes,
            MemoryDocument: memoryDoc,
            InitialCursor: cursor,
            IsColdStart: isColdStart,
            TotalEventCount: allEvents.Count,
            RoomDirectoryPath: roomDirectoryPath,
            LastEventLineHash: lastEventLineHash);
    }

    /// <summary>
    /// Explicitly advances the session cursor past everything <paramref name="input"/> read.
    /// Takes the assembled input rather than a count so a caller cannot commit a value divorced
    /// from what the turn actually saw — see the class remarks for the bug that shape invites.
    /// </summary>
    public static void CommitTurn(
        string roomDirectoryPath,
        OrchestratorTurnInput input,
        DateTimeOffset? turnTimestamp = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(input);

        var newCursor = new OrchestratorSessionCursor(
            ProcessedEventCount: input.TotalEventCount,
            LastCompletedTurnAt: turnTimestamp ?? DateTimeOffset.UtcNow,
            LastEventLineHash: input.LastEventLineHash);

        OrchestratorSessionStore.Save(roomDirectoryPath, newCursor);
    }
}
