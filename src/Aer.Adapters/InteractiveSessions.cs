using System.Collections.Concurrent;
using System.Text.Json;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;

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

public sealed record StartSessionRequest(
    string? Adapter = null,
    string? Model = null,
    string? RoomName = null,
    string? DirectoryPath = null,
    string? WorkingDirectory = null,
    string? InitialMessage = null,
    int? SafetyCeiling = null,
    PermissionGrant? PermissionGrant = null);

public sealed record SendSessionMessageRequest(
    string? SessionId = null,
    string? DirectoryPath = null,
    string Message = "",
    string? Adapter = null,
    string? Model = null,
    // 0054 §4/#1307: the sticky-tag chip's addressee. Null (the default, and every caller before
    // this field existed) is an untagged send -- "posted to the room" -- which /api/sessions/send
    // resolves daemon-side to the current orchestrator (never stamped back onto the recorded turn;
    // see SessionTurn.TargetParticipantId's own remarks). A non-null value must name an existing
    // participant or the endpoint refuses with 400.
    WorkerId? TargetParticipantId = null);

public static class InteractiveSessionMaterializer
{
    public const int DefaultSafetyCeiling = 100;
    public const string DefaultStepId = "chat";
    public const string DefaultWorkerName = "chat-worker";
    public const string DefaultOutputFileName = "response.md";

    // M24 Phase 5.2 (#285): a downstream anchor step exists purely to give a repeated-turn
    // `Supersede` (spec §17.5) a legal target. `Supersede`'s target must be a distinct transitive
    // ancestor (§17.1) -- a single "chat" step targeting itself is spec-illegal three ways (self-
    // target, no ancestor, no supplementary artifact possible) and was silently no-oping every turn
    // after the first (see #285's investigation notes). "chat" itself now declares no PausePoint at
    // all, so a successful turn flows straight through to the anchor without stopping -- Anchor's own
    // PausePoint (targeting "chat") is what actually pauses the workflow, ready for the next turn's
    // Supersede. This also means "chat" has no pause-driven retry path of its own: a first-ever turn
    // that fails outright leaves the workflow terminally failed with nothing to Decide against, which
    // is why Aer.Daemon's turn-execution code detects "anchor has never succeeded" and re-materializes
    // Flow's own state fresh for that one narrow case, rather than issuing a decision.
    public const string AnchorStepId = "turn-anchor";
    public const string AnchorWorkerName = "turn-anchor-worker";
    public const string AnchorOutputFileName = "turn.marker";

    /// <summary>
    /// Asks for the answer as a file without requiring it (#650). It lives in the prompt rather than
    /// in the contract's <c>ProducedOutputs</c> because those are two different statements that were
    /// being made with one: what the worker is asked to do, and what AER treats as the turn having
    /// succeeded. A chat turn's answer arrives either as this file or in the vendor's own structured
    /// result, and the daemon reads whichever it gets — so requiring the file classified a completed
    /// turn as <c>Failed</c> whenever the session's grant could not write one, which is every
    /// directory-less and every plan-mode session.
    /// </summary>
    /// <remarks>
    /// Still worth asking for: the structured-result channel only carries an answer when the turn ran
    /// with streaming output captured, so on a non-streaming turn the file is the only channel there is.
    /// </remarks>
    /// <summary>
    /// The prompt a chat turn is actually dispatched with. Every turn's prompt is rebuilt by the
    /// daemon from the user's message (or a synthesized handoff summary) and overwrites the
    /// materialized <c>PromptTemplate</c>, so the ask has to be appended here, on the per-turn path,
    /// rather than once at materialization — appending it to the materialized template only was
    /// measured to reach no vendor at all.
    /// </summary>
    public static string BuildTurnPrompt(string message) => message + ResponseFileInstruction;

    /// <summary>The chat worker's contract. AER owns it; it is never operator-authored.</summary>
    public static WorkerContract ChatWorkerContract => new(
        WorkerName: DefaultWorkerName,
        RequiredInputs: [],
        // See ResponseFileInstruction (#650).
        ProducedOutputs: [],
        OptionalMetadata: []);

    public static string ResponseFileInstruction =>
        $"\n\nWrite your answer to {WorkerEnvironmentReference.For("AER_OUTPUT_DIR")}" +
        $"{Path.DirectorySeparatorChar}{DefaultOutputFileName} if you are able to write files. " +
        "If you cannot, just answer normally — the answer is read either way.";

    /// <summary>
    /// The default <see cref="PermissionGrant"/> for an interactive session that supplied no explicit
    /// grant. A working directory is a project ceiling (decision 0004); with none, the effective grant
    /// floors to the intersection and MUST fail closed -- no filesystem, shell, or network -- so a
    /// directory-less "plain chat" cannot inherit the daemon/app cwd with write access nobody scoped
    /// (#321). With a directory, the conservative codebase default applies (read + write, no shell or
    /// network). This is the single home for that policy; every materialize path routes through it.
    /// </summary>
    public static PermissionGrant DefaultGrantForWorkingDirectory(string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? new PermissionGrant(ReadFiles: false, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false)
            : new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

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
            // never match and every grant read as `custom`. Caught by
            // DaemonIntegrationTests.SetSessionMode_ThenGetSessionMode_ReflectsTheChange.
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
    /// The directory a session's vendor process runs in (its cwd). When the session is attached to a
    /// codebase that working directory is used; when it is directory-less the process runs in the
    /// session's own directory (its room dir under <c>~/.aer/rooms/</c>) rather than inheriting the
    /// daemon/app cwd (#407). Defense in depth alongside <see cref="DefaultGrantForWorkingDirectory"/>:
    /// a directory-less session is already fail-closed (#321), so it cannot act on any cwd today, but
    /// starting it in a neutral, session-owned dir means a future tool or vector that reads the cwd
    /// independent of the file-tool grant still finds nothing nobody chose. The grant is deliberately
    /// still derived from the (absent) working directory, never from this run directory — running in
    /// its own dir must never widen what a directory-less session is allowed to do.
    /// </summary>
    public static string ResolveRunDirectory(string? workingDirectory, string sessionDirectoryPath) =>
        string.IsNullOrWhiteSpace(workingDirectory) ? sessionDirectoryPath : workingDirectory;

    public static (WorkflowDefinition Definition, IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings, SessionMetadata Metadata) Materialize(
        string sessionId,
        string roomDirectoryPath,
        string adapter,
        string? model = null,
        string? workingDirectory = null,
        string? initialMessage = null,
        int safetyCeiling = DefaultSafetyCeiling,
        PermissionGrant? grant = null)
    {
        var normalizedAdapter = string.IsNullOrWhiteSpace(adapter) ? "claude" : adapter.Trim().ToLowerInvariant();
        var defaultGrant = grant ?? DefaultGrantForWorkingDirectory(workingDirectory);

        // #645 at the last choke point before a caller-supplied grant is written to bindings.json.
        // The UI surfaces check this too, and they must -- an operator needs to hear it while
        // authoring, not as a rejected request. This is here for the callers that are not a UI:
        // `POST /api/sessions/start` takes a grant straight from the request body, and the per-turn
        // rewrite reads whatever that wrote back out and re-persists it, so an incoherent grant
        // accepted once would fail every turn of that session until someone changed the mode.
        //
        // Throws rather than silently repairing: which category the operator actually meant to grant
        // is not knowable here, and quietly widening a permission is the wrong direction to guess in.
        // Why the rule lives on PermissionGrant is recorded once, on CategoriesDefeatedByTheShell.
        if (defaultGrant.CategoriesDefeatedByTheShell is { Count: > 0 } defeated)
        {
            throw new ArgumentException(
                $"This session's permission grant cannot be stored: the shell is granted while "
                + $"{string.Join(", ", defeated)} {(defeated.Count == 1 ? "is" : "are")} withheld, and a "
                + "shell command reaches them anyway. The engine refuses this at bind time, so a "
                + "session created with it could never run a turn.",
                nameof(grant));
        }

        var definition = new WorkflowDefinition(
            WorkflowTemplateId: new WorkflowTemplateId("interactive-session-template"),
            // 3: the chat step no longer declares response.md (#650). Spec §4 is unambiguous that a
            // declared output which does not appear is a failure, and it is right — the defect was
            // declaring one AER does not actually require. A chat turn's answer has two channels, the
            // artifact and the vendor's own structured result, and the daemon accepts either.
            WorkflowTemplateVersion: 3,
            Steps:
            [
                new WorkflowStepDefinition(
                    StepId: new StepId(DefaultStepId),
                    Worker: DefaultWorkerName,
                    Inputs: [],
                    Outputs: [],
                    DependsOn: [],
                    // SessionTurnStubAdapter.ExhaustionSentinel's two-call classifier design (formerly
                    // tests/Aer.Ui.Tests/TestSupport, deleted #1412) assumed exactly one pump attempt
                    // per chat turn -- raising this MaxAttempts would have changed which consultation
                    // was "call #2" there (#1180 review); no surviving test enforces that constraint,
                    // so a future MaxAttempts change here has nothing pinning it.
                    RetryPolicy: new RetryPolicy(1)),
                new WorkflowStepDefinition(
                    StepId: new StepId(AnchorStepId),
                    Worker: AnchorWorkerName,
                    // Nothing upstream declares response.md any more, so the anchor cannot require it
                    // as an input. DependsOn is what orders the two steps; this only ever wired an
                    // artifact the anchor does not read (it is a no-op bookkeeping step).
                    Inputs: [],
                    Outputs: [AnchorOutputFileName],
                    DependsOn: [new StepId(DefaultStepId)],
                    RetryPolicy: new RetryPolicy(1),
                    // NeedsInput, not the default ReadyForReview: a settled chat turn is "awaiting your
                    // next message," never "approve finished work" (#334). This is the one declaration
                    // site that opts out of the approval-gate default; every authored review gate keeps it.
                    PausePoint: new PausePoint([new StepId(DefaultStepId)], PausePointKind.NeedsInput))
            ]);

        var promptTemplate = BuildTurnPrompt(string.IsNullOrWhiteSpace(initialMessage)
            ? "You are an AI assistant in an interactive session. Answer user questions and perform requested tasks."
            : initialMessage);

        var vendorSessionId = string.Equals(normalizedAdapter, "claude", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString()
            : null;

        var bindings = new Dictionary<string, WorkerBindingConfigEntry>
        {
            [DefaultWorkerName] = new WorkerBindingConfigEntry(
                Adapter: normalizedAdapter,
                Contract: ChatWorkerContract,
                PromptTemplate: promptTemplate,
                Timeout: TimeSpan.FromMinutes(10),
                PermissionGrant: defaultGrant,
                Model: model,
                WorkingDirectory: workingDirectory),
            [AnchorWorkerName] = new WorkerBindingConfigEntry(
                Adapter: NoOpWorkerAdapter.AdapterName,
                Contract: new WorkerContract(
                    WorkerName: AnchorWorkerName,
                    RequiredInputs: [],
                    ProducedOutputs: [new ProducedOutput(AnchorOutputFileName)],
                    OptionalMetadata: []),
                PromptTemplate: "(no-op bookkeeping step; ignored)",
                Timeout: TimeSpan.FromSeconds(30),
                // No PermissionGrant, because NoOpWorkerAdapter never reads one (#651): it is not a
                // vendor CLI, and AER builds its dispatch itself. The all-false grant that used to sit
                // here constrained nothing while reading as a sandbox — including to the bind-time rule
                // #629 proposes, which took it at face value and would have refused every interactive
                // session. It also read as a builder-mode grant to the bindings editor, which flagged a
                // session's bindings dirty on load and would have rewritten this entry on save.
                // WorkerAdapterRegistryTests is what keeps "reads a grant" and
                // "declares IPermissionGrantTranslator" the same set.
                PermissionGrant: null)
        };

        // 0054 §1/§6, #1305: the room's first (and, for now, only) worker is a participant with its
        // own identity -- auto-named after its vendor -- and the room's implicit first orchestrator
        // (0054 §6: no gesture, no one else to choose). ParticipantNaming.NextName against an empty
        // set always returns the bare vendor name here, since nothing else has joined yet.
        var firstParticipant = new Participant(
            Id: new WorkerId(DefaultWorkerName),
            Name: ParticipantNaming.NextName(normalizedAdapter, existingNames: []),
            Vendor: normalizedAdapter,
            Model: model,
            Effort: null,
            IsOrchestrator: true);

        var metadata = new SessionMetadata(
            SessionId: sessionId,
            RoomDirectoryPath: roomDirectoryPath,
            CurrentAdapter: normalizedAdapter,
            CurrentVendorSessionId: vendorSessionId,
            Model: model,
            WorkingDirectory: workingDirectory,
            TurnCount: 0,
            SafetyCeiling: safetyCeiling > 0 ? safetyCeiling : DefaultSafetyCeiling,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Turns: [],
            Participants: [firstParticipant]);

        return (definition, bindings, metadata);
    }

    /// <summary>
    /// Computes a session's directory path the same way for every caller -- the daemon's
    /// POST /api/sessions/start handler and the desktop's in-process fallback both need this, and
    /// disagreeing between them is exactly the bug that made a session creatable but unreachable by
    /// id (fixed in Aer.Daemon.Program's session lookups). A caller-supplied <paramref name="roomName"/>
    /// produces a differently-named folder than the "session-{id}" fallback used when it is omitted.
    /// </summary>
    public static string ResolveRoomDirectoryPath(string sessionId, string? roomName, string? directoryPathOverride)
    {
        if (directoryPathOverride != null && Path.IsPathRooted(directoryPathOverride))
        {
            return directoryPathOverride;
        }

        var baseRoomsDir = AerPaths.Rooms;
        var folderName = string.IsNullOrWhiteSpace(roomName) ? $"session-{sessionId}" : roomName.Trim();
        return Path.GetFullPath(Path.Combine(baseRoomsDir, folderName));
    }

    public static async Task<SessionMetadata> MaterializeToDirectoryAsync(
        string sessionId,
        string roomDirectoryPath,
        string adapter,
        string? model = null,
        string? workingDirectory = null,
        string? initialMessage = null,
        int safetyCeiling = DefaultSafetyCeiling,
        PermissionGrant? grant = null,
        CancellationToken cancellationToken = default)
    {
        var workflowFilePath = Path.Combine(roomDirectoryPath, "workflow.json");
        if (File.Exists(workflowFilePath))
        {
            throw new RoomDirectoryAlreadyExistsException(
                RoomLifecycle.IsArchived(roomDirectoryPath)
                    ? $"A room already exists at '{roomDirectoryPath}' and is archived. Unarchive or delete it before reusing this name."
                    : $"A room already exists at '{roomDirectoryPath}'. Choose a different room/session name.");
        }

        Directory.CreateDirectory(roomDirectoryPath);
        var (definition, bindings, metadata) = Materialize(
            sessionId, roomDirectoryPath, adapter, model, workingDirectory, initialMessage, safetyCeiling, grant);

        var bindingsFilePath = AerPaths.RoomBindingsFile(roomDirectoryPath);
        var metadataFilePath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);

        await WorkflowDefinitionWriter.SaveToFileAsync(definition, workflowFilePath, cancellationToken).ConfigureAwait(false);
        await WorkerBindingConfigWriter.SaveToFileAsync(bindings, bindingsFilePath, cancellationToken).ConfigureAwait(false);

        var aerDir = Path.Combine(roomDirectoryPath, ".aer");
        Directory.CreateDirectory(aerDir);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowFilePath, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false);

        await SaveMetadataAsync(metadata, metadataFilePath, cancellationToken).ConfigureAwait(false);

        // 0054 §1/§6, #1305: journal the first participant's join and its implicit orchestrator
        // assignment to room.jsonl, the same durable event log grants and escalations already use
        // for this room. After the room.json write above, matching that file's own "the metadata is
        // the source of truth callers read back; the journal is the derived history" ordering.
        //
        // An IOException here must not fail room creation (second-reader finding): by this point
        // workflow.json already exists, so a thrown journal error would tell the caller creation
        // failed while the existence check at the top of this method permanently blocks retrying the
        // same room name -- an orphaned room nobody can use or recreate. The participant lives
        // durably in room.json above; a missed journal line costs only this event's presence in the
        // derived history, so warn and proceed.
        var firstParticipant = metadata.Participants?.FirstOrDefault();
        if (firstParticipant != null)
        {
            try
            {
                var roomLogPath = Path.Combine(roomDirectoryPath, "room.jsonl");
                await using var writer = new RoomEventLogWriter(roomLogPath);
                var joinedAt = DateTimeOffset.UtcNow;
                await writer.AppendAsync(
                    new RoomEvent.WorkerJoined(
                        firstParticipant.Id, firstParticipant.Name, firstParticipant.Vendor,
                        firstParticipant.Model, firstParticipant.Effort, joinedAt),
                    cancellationToken).ConfigureAwait(false);
                await writer.AppendAsync(
                    new RoomEvent.OrchestratorAssigned(firstParticipant.Id, joinedAt),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine(
                    $"Could not journal the first participant's join for '{roomDirectoryPath}': {ex.Message}");
            }
        }

        return metadata;
    }

    /// <summary>
    /// How many times <see cref="SaveMetadataAsync"/> and <see cref="LoadMetadataAsync"/> retry a
    /// sharing-violation before giving up, and how long they wait between attempts. Small on
    /// purpose: the contended window is a single file replace, so anything that outlasts a handful
    /// of these is a real fault rather than contention, and should surface as one.
    /// </summary>
    private const int MetadataIoAttempts = 12;
    private static readonly TimeSpan MetadataIoRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// #1319: per-room mutex guarding every <see cref="SessionMetadata"/> read-modify-write --
    /// <c>room.json</c> had no lock of its own before this; its safety rested entirely on
    /// <c>Aer.Daemon.Program</c>'s <c>SessionTurnLockFor</c> serializing every chat-turn endpoint's
    /// whole turn. Keyed via <see cref="AerPaths.RecordKey"/>/<see cref="AerPaths.RecordKeyComparer"/>,
    /// the same normalisation <c>SessionTurnLockFor</c> itself uses, so two spellings of one directory
    /// can never each acquire their own semaphore. Wherever a caller already holds the turn lock, this
    /// one must nest INSIDE it, never the reverse -- see <see cref="UpdateMetadataAsync"/> for why it
    /// is safe to acquire on its own where no turn lock is held at all.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MetadataMutexes =
        new(AerPaths.RecordKeyComparer);

    private static SemaphoreSlim MetadataMutexFor(string roomDirectoryPath) =>
        MetadataMutexes.GetOrAdd(AerPaths.RecordKey(roomDirectoryPath), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The one write path production code has to change an existing room's <c>room.json</c>: loads
    /// the current file under this room's metadata mutex, applies <paramref name="mutate"/> to it, and
    /// saves the result before releasing -- so two concurrent load-mutate-saves against the same room
    /// can no longer interleave and silently drop one caller's change (#1319, PR A of #1306's
    /// three-way split). <see cref="SaveMetadataAsync"/> is <c>internal</c> precisely so this is the
    /// only write path reachable from <c>Aer.Daemon</c> endpoint code; the raw primitive stays
    /// reachable in-assembly for <see cref="MaterializeToDirectoryAsync"/> (a brand-new file, not a
    /// read-modify-write) and for test fixtures that need to seed an exact starting state.
    /// <para>
    /// Held only around the load-mutate-save, never across a vendor dispatch -- a turn-completion
    /// caller reads metadata once at dispatch start (outside this lock; that read feeds decisions the
    /// whole turn needs, long before there is anything to save) and folds its final write through here
    /// once the vendor call returns, so this mutex's held duration is a single file replace, not the
    /// length of a CLI invocation.
    /// </para>
    /// <para>
    /// <paramref name="fallback"/> exists for the one legitimate case a fresh load returns null: a
    /// caller that already holds its own pre-lock snapshot, mirroring the "current ?? metadata" idiom
    /// every call site used before this helper existed. Neither the fresh load nor the fallback having
    /// anything throws <see cref="InvalidOperationException"/> -- callers are expected to have already
    /// confirmed the room exists before reaching this helper.
    /// </para>
    /// </summary>
    public static async Task<SessionMetadata> UpdateMetadataAsync(
        string roomDirectoryPath,
        Func<SessionMetadata, SessionMetadata> mutate,
        SessionMetadata? fallback = null,
        CancellationToken cancellationToken = default)
    {
        var metadataFilePath = Path.Combine(roomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var mutex = MetadataMutexFor(roomDirectoryPath);
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadMetadataAsync(metadataFilePath, cancellationToken).ConfigureAwait(false)
                ?? fallback
                ?? throw new InvalidOperationException(
                    $"'{roomDirectoryPath}' is not an interactive room directory (no room.json).");

            var updated = mutate(current);
            await SaveMetadataAsync(updated, metadataFilePath, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>
    /// Writes an interactive room's <c>room.json</c> so that a concurrent reader can neither fail the write nor observe
    /// a half-written file.
    /// <para>
    /// Issue #341: this used a plain <c>File.WriteAllTextAsync</c> against the live path while
    /// <see cref="LoadMetadataAsync"/> used a plain <c>File.ReadAllTextAsync</c>. <c>ReadAllText</c>
    /// opens with <c>FileShare.Read</c>, which denies writers -- so on Windows any client polling
    /// <c>GET /api/sessions/{id}</c> while a turn finished made the turn's own metadata write throw
    /// <c>IOException</c>. That throw happened *after* the Supersede decision had already been
    /// recorded, so the workflow was healthy and only <c>TurnCount</c> never persisted: the chat
    /// stalled forever with an intact event log, and the exception died in a fire-and-forget task.
    /// POSIX permits the concurrent open, which is why this only ever reproduced on Windows.
    /// </para>
    /// <para>
    /// The fix is on the reader: once <see cref="LoadMetadataAsync"/> stops denying write access,
    /// this ordinary write succeeds. A brief retry stays for the genuinely concurrent case (two
    /// turns finishing at once), since the writer's own <c>FileShare.Read</c> excludes a second
    /// writer. Replace-via-temp was tried first and is worse here: Windows'
    /// <c>MOVEFILE_REPLACE_EXISTING</c> needs delete rights on the target and throws
    /// <see cref="UnauthorizedAccessException"/> against a live reader, trading one race for another.
    /// </para>
    /// <para>
    /// #1319: <c>internal</c> on purpose -- this is the raw, RMW-unsafe primitive. Production code
    /// outside this assembly must go through <see cref="UpdateMetadataAsync"/>, which is the only
    /// public path that guards the load this write depends on. Kept reachable in-assembly for
    /// <see cref="MaterializeToDirectoryAsync"/> (writing a brand-new file, not a read-modify-write)
    /// and, via <c>InternalsVisibleTo</c>, for test fixtures seeding an exact starting state.
    /// </para>
    /// </summary>
    internal static async Task SaveMetadataAsync(SessionMetadata metadata, string filePath, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(metadata, options);

        await RetryOnSharingViolationAsync(
            () => File.WriteAllTextAsync(filePath, json, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an interactive room's <c>room.json</c> without denying a concurrent writer -- see
    /// <see cref="SaveMetadataAsync"/> for why that matters. What permits that writer's open is the
    /// <c>Write</c> bit; it writes the live path directly rather than renaming onto it.
    /// <para>
    /// #1267: this said <c>FileShare.Delete</c> "permits the replace this file's writer performs",
    /// which credited the wrong flag for a rename that does not happen here -- and would tell whoever
    /// next converts this writer to stage-and-move that the reader already tolerates it. It does not:
    /// a delete-sharing handle blocks a rename exactly as a default-share one does (0057's
    /// "Rests on"). The flag is kept because it costs nothing and is correct for a reader to offer.
    /// </para>
    /// </summary>
    public static async Task<SessionMetadata?> LoadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        SessionMetadata? result = null;
        await RetryOnSharingViolationAsync(
            async () =>
            {
                using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                // Permitting a concurrent writer is what makes the write above succeed, and the
                // cost is that this read can land mid-rewrite and see a truncated document. That
                // state is always transient -- the writer completes -- so a torn parse is retried
                // rather than surfaced. Deserialize inside the retry so both failures share it.
                result = JsonSerializer.Deserialize<SessionMetadata>(json, options);
            },
            cancellationToken).ConfigureAwait(false);

        // A workflow room's marker is a minimal { "Kind": "Workflow" } with no SessionId; it
        // deserializes to a SessionMetadata whose required fields defaulted to null. That is not
        // interactive-session metadata, so report it as absent -- identical to the pre-0013 world
        // where a workflow room simply had no session.json. Every caller that gated on a non-null
        // load (the /api/sessions endpoints, the by-id scan, the broadcast SessionId probe) then
        // behaves exactly as before, without a per-site kind check.
        if (result is not null && string.IsNullOrEmpty(result.SessionId))
        {
            return null;
        }

        return result;
    }

    /// <summary>
    /// Reads a room's <see cref="RoomKind"/> from its <c>.aer/room.json</c> marker without denying a
    /// concurrent writer — opening with <c>FileShare.ReadWrite | FileShare.Delete</c> exactly as
    /// <see cref="LoadMetadataAsync"/> does, because a plain <c>File.ReadAllText</c> reintroduces
    /// #341's Windows write-denial. An absent marker is a workflow room; a present-but-unparseable
    /// one is treated as interactive (its presence has always meant that) rather than crashing a
    /// caller. This is the single seam the daemon, adapters, and UI route their old
    /// <c>File.Exists(session.json)</c> kind-checks through.
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
    /// at materialization, using the same write discipline as <see cref="SaveMetadataAsync"/>. The
    /// marker is defensive rather than load-bearing — an absent one already reads as a workflow room
    /// — but it makes the room self-describing on disk instead of implied by a missing file.
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
