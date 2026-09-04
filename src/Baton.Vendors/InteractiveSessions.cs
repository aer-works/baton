using System.Text.Json;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// What a room contains, recorded in its <c>.baton/room.json</c> marker so no caller has to infer it
/// from a file's mere presence (decision 0013 makes the room the single record noun; a room holds
/// either an interactive session or a workflow execution). An absent marker is read as
/// <see cref="Workflow"/> — a workflow room needs none, an interactive one always writes its session
/// metadata here — which preserves the pre-0013 rule that <c>.baton/session.json</c>'s presence meant
/// "interactive" exactly, now keyed on an explicit field rather than a filename.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum RoomKind
{
    Interactive,
    Workflow,
}

public sealed record SessionTurn(
    int TurnIndex,
    string Vendor,
    string HumanMessage,
    string? AssistantResponse,
    DateTimeOffset ExecutedAt,
    bool NativeSessionResumed,
    bool VendorHandoffSynthesized,
    string? ErrorMessage = null,
    // #1179: a turn the PRODUCT answered because the room was dormant when the message arrived --
    // no vendor process ran, which is why AssistantResponse stays null on this turn. Trailing
    // optional, like ErrorMessage above, so metadata files written before this field existed still
    // load unchanged (defaults to false).
    bool IsDormancyAnswer = false,
    // 0026 §4/#1180: a failed turn the resolved adapter's IFailureClassifier keyed to
    // FailureClassification.ExhaustedUntil -- a STATE with a reset time, not a failure. Renderers
    // MUST key on IsExhausted FIRST, before ErrorMessage: an exhausted turn must never reach the
    // failure-card arm, even though ErrorMessage stays populated (below) for exactly that turn.
    // Trailing optional, same idiom as ErrorMessage/IsDormancyAnswer, so old metadata loads
    // unchanged (defaults to false/null).
    bool IsExhausted = false,
    // The reset instant the vendor reported, frozen at classification time (0026 §2's no-wall-
    // clock-on-replay rule). Null is an honest "reset unknown" (0026 §5), not "not exhausted" --
    // that distinction is IsExhausted's job. ErrorMessage is deliberately left populated (the raw
    // vendor text) even when IsExhausted is true: it still feeds Copy and the disclosure path, just
    // never the fix-ask affordance an exhausted quota can't answer.
    DateTimeOffset? ExhaustedUntil = null,
    // 0054 §4/#1307 ruling 3: the tag the sender actually chose, durable as the turn's own fact.
    // Null means "posted to the room" and STAYS null even though the daemon resolves an untagged
    // send to the current orchestrator to answer it (Baton.Daemon.Program's ResolveOrchestrator) --
    // that resolution is deliberately never stamped back here, because "untagged, answered by
    // whoever held the role" is the truthful transcript fact and stamping the resolution would
    // erase the distinction 0054 §4 draws between a tagged turn and a room turn. Trailing optional,
    // same idiom as ExhaustedUntil above, so metadata written before this field existed still loads.
    WorkerId? TargetParticipantId = null);

public sealed record SessionMetadata(
    string SessionId,
    string RoomDirectoryPath,
    string CurrentAdapter,
    string? CurrentVendorSessionId,
    string? Model,
    string? WorkingDirectory,
    int TurnCount,
    int SafetyCeiling,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<SessionTurn> Turns,
    // False until a turn actually completes against CurrentVendorSessionId (M24 Phase 5.1, #285):
    // the id is minted client-side at materialization/handoff time, before the vendor CLI has ever
    // heard of it, so its mere presence can't tell a caller whether `--resume` is safe yet. Absent
    // in room.json files written before this field existed -- System.Text.Json defaults it to
    // false on load, which is the safe direction (worst case: one redundant `--session-id` retry
    // instead of a guaranteed-failing `--resume`).
    bool VendorSessionEstablished = false,
    // The room-kind marker (0013). Always Interactive for a serialized SessionMetadata -- this file
    // *is* an interactive room's marker; a workflow room writes a minimal marker instead. Defaulted
    // so room.json files written before this field existed still load as the interactive rooms they
    // were, and so ReadRoomKind can key on it without a second file.
    RoomKind Kind = RoomKind.Interactive,
    // 0054 §1, #1305: the room's workers as participant identities, not derived vendor labels --
    // null on any room.json written before this field existed (the pre-#1305 shape: exactly one
    // worker, identified only by CurrentAdapter/Model above). Callers that need "the" participant on
    // an old room fall back to CurrentAdapter/Model directly; this stays null rather than being
    // synthesized on load, since a synthesized participant would have no corresponding WorkerJoined
    // journal entry.
    List<Participant>? Participants = null);

public static class InteractiveSessionMaterializer
{
    /// <summary>
    /// How many times <see cref="ReadRoomKind"/>'s retry loop attempts a sharing-violation before
    /// giving up, and how long it waits between attempts. Small on purpose: the contended window is a
    /// single file replace, so anything that outlasts a handful of these is a real fault rather than
    /// contention, and should surface as one.
    /// </summary>
    private const int MetadataIoAttempts = 12;
    private static readonly TimeSpan MetadataIoRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Reads a room's <see cref="RoomKind"/> from its <c>.baton/room.json</c> marker without denying a
    /// concurrent writer — opening with <c>FileShare.ReadWrite | FileShare.Delete</c>, because a plain
    /// <c>File.ReadAllText</c> reintroduces #341's Windows write-denial. An absent marker is a
    /// workflow room; a present-but-unparseable one is treated as interactive (its presence has
    /// always meant that) rather than crashing a caller. This is the single seam adapters route their
    /// old <c>File.Exists(session.json)</c> kind-checks through.
    /// </summary>
    public static async Task<RoomKind> ReadRoomKindAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".baton", BatonPaths.RoomMetadataFileName);
        if (!File.Exists(markerPath)) return RoomKind.Workflow;

        var kind = RoomKind.Interactive;
        try
        {
            await RetryOnSharingViolationAsync(
                async () =>
                {
                    using var stream = new FileStream(
                        markerPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
                    using var reader = new StreamReader(stream);
                    kind = ParseRoomKind(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // RetryOnSharingViolationAsync already rode out the transient torn-read window, so a
            // marker that still will not parse is corrupt rather than mid-write.
            kind = RoomKind.Interactive;
        }

        return kind;
    }

    /// <summary>
    /// Redispatch lineage (#1441) read back off the same marker <see cref="ReadRoomKindAsync"/>
    /// reads its kind from -- <c>null</c>/<c>null</c>/<c>null</c> for an ordinary <c>baton dispatch</c>
    /// room, which has no parent.
    /// </summary>
    /// <param name="ContinuedSessionId">
    /// The prior room's own vendor session id this room's worker resumed (issue #1381,
    /// <c>baton dispatch &lt;role&gt; --continue &lt;room&gt;</c>). Why this is a third field rather
    /// than folded into the two above, and what it lets a reader tell apart: spec/baton.md §3's
    /// dispatch entry.
    /// </param>
    public sealed record RoomLineage(
        string? ParentRoomDirectoryPath = null, string? ParentExecutionId = null, string? ContinuedSessionId = null)
    {
        public static readonly RoomLineage None = new();
    }

    /// <summary>
    /// Reads a room's redispatch lineage (#1441, issue #1620) off its <c>.baton/room.json</c> marker --
    /// the same file and read strategy <see cref="ReadRoomKindAsync"/> uses, opened with
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> so a concurrent writer is never denied. Display
    /// metadata for <c>fleet_status</c> (spec/baton.md §6 schema), so this fails open all the way:
    /// an absent marker, a marker with no lineage fields (an ordinary dispatch room), or one that
    /// will not parse after <see cref="RetryOnSharingViolationAsync"/> rides out the torn-read
    /// window all read as <see cref="RoomLineage.None"/> rather than throwing.
    /// </summary>
    public static async Task<RoomLineage> ReadLineageAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".baton", BatonPaths.RoomMetadataFileName);
        if (!File.Exists(markerPath)) return RoomLineage.None;

        var lineage = RoomLineage.None;
        try
        {
            await RetryOnSharingViolationAsync(
                async () =>
                {
                    using var stream = new FileStream(
                        markerPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
                    using var reader = new StreamReader(stream);
                    lineage = ParseLineage(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            lineage = RoomLineage.None;
        }

        return lineage;
    }

    private static RoomLineage ParseLineage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var parentRoomDirectoryPath = doc.RootElement.TryGetProperty("ParentRoomDirectoryPath", out var parentPathEl)
                                       && parentPathEl.ValueKind == JsonValueKind.String
            ? parentPathEl.GetString()
            : null;
        var parentExecutionId = doc.RootElement.TryGetProperty("ParentExecutionId", out var parentExecEl)
                                 && parentExecEl.ValueKind == JsonValueKind.String
            ? parentExecEl.GetString()
            : null;
        var continuedSessionId = doc.RootElement.TryGetProperty("ContinuedSessionId", out var sessionEl)
                                  && sessionEl.ValueKind == JsonValueKind.String
            ? sessionEl.GetString()
            : null;
        return new RoomLineage(parentRoomDirectoryPath, parentExecutionId, continuedSessionId);
    }

    /// <summary>
    /// Synchronous <see cref="ReadRoomKindAsync"/>, for the one caller that decides a kind while
    /// building a process's environment (the agy HOME redirect) and is not on an async path.
    /// </summary>
    public static RoomKind ReadRoomKind(string roomDirectoryPath)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".baton", BatonPaths.RoomMetadataFileName);
        if (!File.Exists(markerPath)) return RoomKind.Workflow;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return ParseRoomKind(reader.ReadToEnd());
            }
            catch (Exception ex) when (attempt < MetadataIoAttempts
                                       && ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Thread.Sleep(MetadataIoRetryDelay);
            }
            catch (JsonException)
            {
                // Final attempt saw a corrupt (not merely mid-write) marker -- see ReadRoomKindAsync.
                return RoomKind.Interactive;
            }
        }
    }

    private static RoomKind ParseRoomKind(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("Kind", out var kindEl)
               && kindEl.ValueKind == JsonValueKind.String
               && Enum.TryParse<RoomKind>(kindEl.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            // A present marker with no parseable Kind is a pre-0013 interactive session file, whose
            // presence alone denoted interactive.
            : RoomKind.Interactive;
    }

    /// <summary>
    /// Writes a workflow room's minimal <c>.baton/room.json</c> marker (<c>{ "Kind": "Workflow" }</c>)
    /// at materialization, retrying through <see cref="RetryOnSharingViolationAsync"/> the same way
    /// every other write into this file does. The marker is defensive rather than load-bearing — an
    /// absent one already reads as a workflow room — but it makes the room self-describing on disk
    /// instead of implied by a missing file.
    /// </summary>
    /// <param name="parentRoomDirectoryPath">
    /// <c>baton redispatch</c>'s lineage (#1441, spec/baton.md §2): the terminal room this one was
    /// redispatched from. Null for an ordinary <c>baton dispatch</c>, which has no parent.
    /// </param>
    /// <param name="parentExecutionId">The parent room's own execution id, when cheaply known (#1441). Null otherwise.</param>
    /// <param name="continuedSessionId">
    /// #1381: the veteran's own vendor session id, set only by <c>baton dispatch --continue</c> --
    /// see <see cref="RoomLineage.ContinuedSessionId"/>'s own doc for why this is the one field that
    /// tells the two lineage-writing verbs apart. Null for an ordinary dispatch and for a redispatch.
    /// </param>
    public static async Task WriteWorkflowRoomMarkerAsync(
        string roomDirectoryPath,
        string? parentRoomDirectoryPath = null,
        string? parentExecutionId = null,
        string? continuedSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".baton", BatonPaths.RoomMetadataFileName);
        var dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(
            new WorkflowRoomMarker(RoomKind.Workflow, parentRoomDirectoryPath, parentExecutionId, continuedSessionId),
            new JsonSerializerOptions { WriteIndented = true });

        await RetryOnSharingViolationAsync(
            () => File.WriteAllTextAsync(markerPath, json, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record WorkflowRoomMarker(
        RoomKind Kind, string? ParentRoomDirectoryPath = null, string? ParentExecutionId = null, string? ContinuedSessionId = null);

    /// <summary>
    /// Retries <paramref name="action"/> through the transient states of a concurrently
    /// read-and-rewritten file: a sharing violation, a denied replace, or a document parsed
    /// mid-write. The final attempt rethrows, so a genuine fault (a corrupt file that never settles,
    /// a real permissions problem) still surfaces rather than being retried into silence -- silence
    /// is what made #341 cost a day.
    /// </summary>
    private static async Task RetryOnSharingViolationAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MetadataIoAttempts
                                       && ex is IOException or UnauthorizedAccessException or JsonException)
            {
                await Task.Delay(MetadataIoRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string SynthesizeContextSummary(IReadOnlyList<SessionTurn> turns, string newMessage)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Previous conversation transcript summary for context:");
        foreach (var turn in turns)
        {
            sb.AppendLine($"User: {turn.HumanMessage}");
            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                sb.AppendLine($"Assistant: {turn.AssistantResponse}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Now continue with the following user request:");
        sb.AppendLine(newMessage);
        return sb.ToString();
    }
}
