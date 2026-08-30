using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Adapters;

/// <summary>
/// What a room contains, recorded in its <c>.aer/room.json</c> marker so no caller has to infer it
/// from a file's mere presence (decision 0013 makes the room the single record noun; a room holds
/// either an interactive session or a workflow execution). An absent marker is read as
/// <see cref="Workflow"/> — a workflow room needs none, an interactive one always writes its session
/// metadata here — which preserves the pre-0013 rule that <c>.aer/session.json</c>'s presence meant
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
    // send to the current orchestrator to answer it (Aer.Daemon.Program's ResolveOrchestrator) --
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
    /// The session-mode vocabulary <c>POST /api/sessions/{id}/mode</c> accepts, and the only place it
    /// is written down. #645.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Enumerable on purpose.</b> The mapping was an inline <c>switch</c> literal inside the
    /// endpoint's lambda, which meant nothing could assert a property across it — and the property
    /// that matters is that every mode produces a grant <see cref="WorkerBindingResolver.Resolve"/>
    /// accepts. All three were coherent, so nothing was broken; a fourth mode written straight into a
    /// lambda is how that stops being true silently. A test over <see cref="KnownModes"/> covers a new
    /// mode the day it is added here, which an inline literal could never offer.
    /// </para>
    /// <para>
    /// Reverse-mapped by the endpoint's <c>GET</c> counterpart back to one of these names, or
    /// <c>custom</c> for a grant matching none — see #286. <c>custom</c> is GET-only and is
    /// deliberately not a member here: it is an OBSERVATION about a grant, never an instruction.
    /// </para>
    /// <para>
    /// <b>Do not read this list next to a vendor's own mode names and assume the shared words
    /// agree.</b> Both vendors have a mode vocabulary and both include an accept-edits mode — agy's
    /// <c>--mode</c> takes <c>default</c>/<c>accept-edits</c>/<c>plan</c>, claude's
    /// <c>--permission-mode</c> takes <c>default</c>/<c>acceptEdits</c>/<c>plan</c>/
    /// <c>bypassPermissions</c>. The overlap with the names here is a coincidence of English, not a
    /// mapping: <see cref="AgyWorkerAdapter.TryTranslatePermissionGrant"/> resolves AER's
    /// <c>default</c> (write, no shell) to agy's <b><c>accept-edits</c></b>, not to agy's
    /// <c>default</c>. Only <c>plan</c> happens to line up.
    /// </para>
    /// <para>
    /// And the two adapters do not even use the same MECHANISM, which is the part most likely to
    /// mislead: <c>ClaudeWorkerAdapter</c> never emits <c>--permission-mode</c> at all — it expresses
    /// a grant as <c>--allowedTools</c>/<c>--disallowedTools</c> plus the <c>PreToolUse</c> hook, so
    /// claude's mode vocabulary is unused rather than translated. This is Adapter Isolation working
    /// as intended; it is written down because "both vendors have accept-edits" invites the
    /// conclusion that AER maps onto it on both sides, and it does not.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>One dictionary, not a list beside a switch.</b> The first version of this had
    /// <see cref="KnownModes"/> as a literal and the mapping as a <c>switch</c>, which could
    /// disagree: the endpoint rejects on <see cref="GrantForMode"/> returning null rather than on
    /// list membership, so a case added to the switch alone would have been accepted while the 400
    /// message listed it as invalid. Deriving both from one dictionary makes that divergence
    /// unrepresentable rather than merely tested for.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, PermissionGrant> ModeGrants =
        new Dictionary<string, PermissionGrant>(StringComparer.Ordinal)
        {
            ["auto"] = new(ReadFiles: true, WriteFiles: true, RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: true),
            ["default"] = new(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false),
            ["plan"] = new(ReadFiles: true, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false),
        };

    public static readonly IReadOnlyList<string> KnownModes = [.. ModeGrants.Keys];

    /// <summary>
    /// The <see cref="PermissionGrant"/> a mode name means, or <see langword="null"/> when the name is
    /// not one of <see cref="KnownModes"/>. Case- and whitespace-insensitive, matching what the
    /// endpoint accepted before this moved.
    /// </summary>
    public static PermissionGrant? GrantForMode(string? mode) =>
        mode is not null && ModeGrants.TryGetValue(mode.Trim().ToLowerInvariant(), out var grant)
            ? grant
            : null;

    /// <summary>
    /// The mode name a grant corresponds to, or <c>custom</c> for one matching none — what
    /// <c>GET /api/sessions/{id}/mode</c> reports.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="ModeGrants"/> rather than restating the three grants inline, which is
    /// what the GET endpoint did. That inline copy was a second home for the vocabulary: adding a
    /// fourth mode would have left GET reporting <c>custom</c> for it while POST accepted it, which
    /// is the same silent drift moving the mapping out of the lambda was meant to end — on the other
    /// half of the endpoint pair.
    /// <para>
    /// <b>One deliberate behaviour change.</b> The inline version matched on the four booleans and
    /// ignored <see cref="PermissionGrant.ShellCommandPatterns"/>, so a grant giving a <i>scoped</i>
    /// shell reported as <c>auto</c> — which reads to an operator as unrestricted. A grant carrying
    /// patterns is none of these modes, and now says so.
    /// </para>
    /// </remarks>
    public static string ModeForGrant(PermissionGrant? grant)
    {
        if (grant is null)
        {
            return CustomMode;
        }

        foreach (var (name, known) in ModeGrants)
        {
            // Field by field, NOT record equality. PermissionGrant is a record whose
            // ShellCommandPatterns is a collection, and the compiler-generated Equals uses the
            // member's own equality -- which for a list is REFERENCE equality, so two empty lists
            // never match and every grant read as `custom`. Originally caught by a session-mode
            // round-trip test that died with the Ui archive (#1412) -- #1416 restores that
            // coverage class post-narrowing; until then this comment is the record of the trap.
            if (known.ReadFiles == grant.ReadFiles
                && known.WriteFiles == grant.WriteFiles
                && known.RunShellCommands == grant.RunShellCommands
                && known.NetworkAccess == grant.NetworkAccess
                // Null and empty both mean "no scoping", and the property is nullable.
                && (grant.ShellCommandPatterns?.Count ?? 0) == 0)
            {
                return name;
            }
        }

        return CustomMode;
    }

    /// <summary>
    /// What <see cref="ModeForGrant"/> reports for a grant matching no mode. GET-only, and
    /// deliberately not a member of <see cref="KnownModes"/>: it is an OBSERVATION about a grant,
    /// never an instruction.
    /// </summary>
    public const string CustomMode = "custom";

    /// <summary>
    /// The interactive chat worker's binding key. No production path materializes a chat-worker
    /// binding any more (the daemon HTTP surface that started interactive sessions was deleted in
    /// #1420, and the last materializer for one was deleted alongside it, #1440) — this and
    /// <see cref="ChatWorkerContract"/> survive only as the realistic "chat worker" fixture
    /// WorkerBindingResolverTests' session-mode coherence tests bind against.
    /// </summary>
    public const string DefaultWorkerName = "chat-worker";

    /// <summary>The chat worker's contract. See <see cref="DefaultWorkerName"/> for why this still exists.</summary>
    public static WorkerContract ChatWorkerContract => new(
        WorkerName: DefaultWorkerName,
        RequiredInputs: [],
        ProducedOutputs: [],
        OptionalMetadata: []);

    /// <summary>
    /// How many times <see cref="ReadRoomKind"/>'s retry loop attempts a sharing-violation before
    /// giving up, and how long it waits between attempts. Small on purpose: the contended window is a
    /// single file replace, so anything that outlasts a handful of these is a real fault rather than
    /// contention, and should surface as one.
    /// </summary>
    private const int MetadataIoAttempts = 12;
    private static readonly TimeSpan MetadataIoRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Reads a room's <see cref="RoomKind"/> from its <c>.aer/room.json</c> marker without denying a
    /// concurrent writer — opening with <c>FileShare.ReadWrite | FileShare.Delete</c>, because a plain
    /// <c>File.ReadAllText</c> reintroduces #341's Windows write-denial. An absent marker is a
    /// workflow room; a present-but-unparseable one is treated as interactive (its presence has
    /// always meant that) rather than crashing a caller. This is the single seam adapters route their
    /// old <c>File.Exists(session.json)</c> kind-checks through.
    /// </summary>
    public static async Task<RoomKind> ReadRoomKindAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
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
    /// Synchronous <see cref="ReadRoomKindAsync"/>, for the one caller that decides a kind while
    /// building a process's environment (the agy HOME redirect) and is not on an async path.
    /// </summary>
    public static RoomKind ReadRoomKind(string roomDirectoryPath)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
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
    /// Writes a workflow room's minimal <c>.aer/room.json</c> marker (<c>{ "Kind": "Workflow" }</c>)
    /// at materialization, retrying through <see cref="RetryOnSharingViolationAsync"/> the same way
    /// every other write into this file does. The marker is defensive rather than load-bearing — an
    /// absent one already reads as a workflow room — but it makes the room self-describing on disk
    /// instead of implied by a missing file.
    /// </summary>
    public static async Task WriteWorkflowRoomMarkerAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(
            new WorkflowRoomMarker(RoomKind.Workflow),
            new JsonSerializerOptions { WriteIndented = true });

        await RetryOnSharingViolationAsync(
            () => File.WriteAllTextAsync(markerPath, json, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record WorkflowRoomMarker(RoomKind Kind);

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
