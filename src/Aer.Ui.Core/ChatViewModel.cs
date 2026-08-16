using System.Collections.ObjectModel;
using System.Linq;
using Aer.Adapters;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>One rendered row in <see cref="ChatViewModel.Messages"/> — a human turn or an assistant response, never both (M24 Phase 1 desktop chat UI, issue #262).</summary>
public sealed record ChatMessageViewModel(
    string SenderLabel,
    string Text,
    DateTimeOffset Timestamp,
    bool IsFromUser,
    bool IsSystem = false,
    bool IsFailure = false,
    Action? PrepareFixPrompt = null,
    bool IsDormancy = false,
    Action? Wake = null,
    // 0026 §4/#1180: the out-of-plan card -- same pattern as IsFailure, but never carries
    // PrepareFixPrompt (an offer to spend against the very quota that is out is the confusion 0026
    // exists to remove) and never reads Status.Failed.
    bool IsOutOfPlan = false,
    // What Copy puts on the clipboard. Null means "copy Text" (every other card's behaviour,
    // including IsFailure's). The out-of-plan card sets this to the turn's raw ErrorMessage: Text
    // is the plain-language 0026 sentence ("Out of plan — resumes …"), which is what should render,
    // but the vendor's own words are what's useful to paste elsewhere -- the same raw text
    // InteractiveSessions.SessionTurn.ErrorMessage stays populated for.
    string? CopyText = null)
{
    public IRelayCommand? PrepareFixPromptCommand { get; } = PrepareFixPrompt != null ? new RelayCommand(PrepareFixPrompt) : null;
    public IRelayCommand? WakeCommand { get; } = Wake != null ? new RelayCommand(Wake) : null;
}

/// <summary>
/// One row in the chat capability picker (M24 Phase 2 follow-up). <paramref name="IsInvokable"/> is
/// carried from <see cref="Aer.Adapters.WorkerCapabilityItem.IsInvokable"/> — which kinds are
/// actionable is vendor-kind semantics the adapter layer states (#615); the picker only routes
/// invokable rows into the selectable section and the rest into the informational one.
/// </summary>
public sealed record ChatCapabilityItemViewModel(string Name, string Kind, string Description, bool IsRecentlyUsed, bool IsInvokable);

/// <summary>
/// The dedicated Chat view's state (M24 Phase 1 desktop wiring, issue #262) — a chat/codebase
/// session renders here instead of <see cref="MainWindowViewModel.RoomSteps"/>'s generic DAG
/// drill-in, since a single repeatedly-superseded "chat" step has no dependency graph worth
/// showing and the real content (<see cref="SessionMetadata.Turns"/>) lives outside
/// <c>RoomProjection</c> entirely.
/// <para>
/// <c>POST /api/sessions/send</c> only confirms a turn was dispatched — the daemon runs it on a
/// fire-and-forget background task and the response carries no updated metadata at all
/// (<c>Aer.Daemon.Program</c>'s handler). Completion is observed the same way every other live
/// room state already is in this app: <c>MainWindow</c>'s existing 2-second poll
/// (<c>_liveRefreshTimer</c>) reloads <see cref="SessionMetadata"/> and calls
/// <see cref="LoadFromMetadata"/> again, whose <see cref="Aer.Adapters.SessionTurn"/> count moving
/// past <see cref="_turnsCountAtSendTime"/> is what flips <see cref="IsSending"/> back off — no
/// second polling loop or completion push needed.
/// </para>
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private SessionMetadata? _lastMetadata;
    private IReadOnlyList<PermissionAnswer> _permissionAnswers = [];
    private IReadOnlyList<DormancyTransition> _dormancyTransitions = [];
    private IReadOnlyList<RecordedDecisionMoment> _recordedDecisionMoments = [];
    private bool _isDormant = false;
    private Action? _wakeAction = null;

    /// <summary>
    /// Room-journal entries (permission answers, and since #1178 dormancy transitions) at or before
    /// this watermark are hidden from the transcript. Set by
    /// <see cref="MarkTranscriptCleared"/> when /clear wipes the vendor turns: room.jsonl's
    /// history survives the wipe, so without this every old entry would re-render as an
    /// orphan bubble above an empty transcript. Derived from the entries' own timestamps rather than
    /// the UI host's clock (the daemon stamped AnsweredAt; the two clocks can disagree). In-memory
    /// only — after an app restart the old answers reappear alongside the room's other durable
    /// history, which is the disclosed limitation (#1142 review) until clears are themselves room
    /// events.
    /// </summary>
    private DateTimeOffset? _answersClearedThrough;
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    /// <summary>Invokable skills/commands/agents (M24 Phase 2 follow-up chat capability picker) — recently-used first, per <see cref="LoadCommands"/>.</summary>
    public ObservableCollection<ChatCapabilityItemViewModel> InvokableCommands { get; } = [];

    /// <summary>Informational-only entries (Gemini's modes/plugins) — shown, never selectable.</summary>
    public ObservableCollection<ChatCapabilityItemViewModel> InfoCommands { get; } = [];

    /// <summary>
    /// Messages typed while a turn was in flight (#1074, slice of #462): the composer never blocks, so
    /// a send during <see cref="IsSending"/> joins this FIFO instead of posting a concurrent turn. The
    /// queue is visible (rendered as pending "You" bubbles) and each entry is removable before it
    /// sends; <c>MainWindow</c>'s live-refresh poll drains the head one turn at a time on completion.
    /// </summary>
    public ObservableCollection<QueuedChatMessageViewModel> QueuedMessages { get; } = [];

    /// <summary>True while at least one message is waiting to send — drives the composer's "queued" caption and the pending-bubble list.</summary>
    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    [ObservableProperty]
    private bool isCommandMenuOpen;

    [ObservableProperty]
    private string inputText = string.Empty;

    [ObservableProperty]
    private bool isSending;

    /// <summary>The raw in-turn stream (<see cref="WorkerProgressEvent.Text"/> concatenated as it arrives) — reset at the start of every send.</summary>
    [ObservableProperty]
    private string liveProgressText = string.Empty;

    [ObservableProperty]
    private string headlineText = "No room open.";

    /// <summary>
    /// The room's single worker, shown as a chip beside the room name in the header — the daily-driver
    /// mockup's "aer-flow  claude" (02-screens.md). The vendor (<see cref="CurrentAdapter"/>); null
    /// until a room is open. "+ Add worker" and multi-worker chips are M27.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorker))]
    private string? workerChipText;

    public bool HasWorker => !string.IsNullOrEmpty(WorkerChipText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string statusText = string.Empty;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    /// <summary>The active session mode ("auto"/"default"/"plan"/"custom"), or null until <see cref="RoomClient.GetSessionModeAsync"/> has resolved it (#286) — persistently shown in the chat header, not just reflected transiently after a click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentMode))]
    private string? currentMode;

    public bool HasCurrentMode => !string.IsNullOrEmpty(CurrentMode);

    public string? SessionId { get; private set; }
    public string? RoomDirectoryPath { get; private set; }
    public string? CurrentAdapter { get; private set; }

    /// <summary>
    /// True once a session is open and the ordinary message-send flow applies (#290). Chat page
    /// entry: no session started -> no dependency on the template picker to change that; the
    /// message box was previously the *only* Chat page control, silently doing nothing on Send when
    /// this was false.
    /// </summary>
    public bool IsSessionOpen => RoomDirectoryPath != null;

    /// <summary>
    /// True while the room open in this rendering is a workflow room rather than a chat session
    /// (#1196 slice 3). Both open here now — that is the whole point of the slice, a decision
    /// answered where it was raised — but only one of them can be talked to, so the composer and
    /// the "start new chat" entry point key on this rather than on <see cref="IsSessionOpen"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposerVisible))]
    [NotifyPropertyChangedFor(nameof(IsNewChatVisible))]
    [NotifyPropertyChangedFor(nameof(IsWorkflowSwitchVisible))]
    [NotifyPropertyChangedFor(nameof(IsWorkflowActive))]
    [NotifyPropertyChangedFor(nameof(IsShapeToggleVisible))]
    private bool isPipelineRoom;

    /// <summary>
    /// Whether this room's workflow is switched on (#1216) — projected from <c>room.jsonl</c>, so it
    /// survives a restart, and defaulting true because absence means on
    /// (<see cref="Aer.Flow.Domain.RoomEvent.WorkflowSwitched"/>).
    /// </summary>
    /// <remarks>
    /// Rendering only. Nothing here decides the value or refuses a change: the durable fact is the
    /// journal's, and the refusal rule is <c>RoomMutationInterface.SetWorkflowSwitchAsync</c>'s, which
    /// is what keeps a phone and a desktop from disagreeing about when the switch is available (0020).
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkflowActive))]
    [NotifyPropertyChangedFor(nameof(IsShapeToggleVisible))]
    [NotifyPropertyChangedFor(nameof(WorkflowSwitchLabel))]
    private bool isWorkflowOn = true;

    /// <summary>The switch's own text, spelled out rather than left to the toggle's pressed look — the corpus draws the state in words ("Workflow [● ON ]").</summary>
    public string WorkflowSwitchLabel => IsWorkflowOn ? "Workflow ON" : "Workflow OFF";

    /// <summary>The switch itself: a chat session has no workflow to switch, so it is offered only in a workflow room.</summary>
    public bool IsWorkflowSwitchVisible => IsPipelineRoom;

    /// <summary>
    /// Whether this room currently has a workflow to act on at all — the one canonical answer every
    /// workflow-shaped affordance keys on, so they cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Why a workflow-off room offers no run at all — including the enforcement that does not yet
    /// exist behind it — is settled in <c>docs/design/02-screens.md</c>'s #1216 amendment. This is
    /// the single flag that decision is spent through, so the header, the Shape toggle and the run
    /// offer cannot disagree about it.
    /// </remarks>
    public bool IsWorkflowActive => IsPipelineRoom && IsWorkflowOn;

    /// <summary>
    /// Whether the Shape toggle is offered. A room whose workflow is off has no shape to show — the
    /// corpus's "toggling a room's workflow off hides the shape panel" — so the toggle goes with it
    /// rather than being left on screen opening an empty panel.
    /// </summary>
    public bool IsShapeToggleVisible => IsWorkflowActive;

    partial void OnIsWorkflowOnChanged(bool value)
    {
        // Switching off closes the panel as well as retiring its toggle. Without this a panel that
        // was already open stays open, showing the shape of a workflow the header now says is off —
        // the toggle disappearing is not the same as the panel closing.
        if (!value)
        {
            IsShapePanelOpen = false;
        }
    }

    /// <summary>
    /// Whether the composer is on screen at all — true in a workflow room, where it is disabled
    /// rather than removed. The choice and its reasoning are the dated 2026-08-14 amendment in
    /// <c>docs/design/02-screens.md</c>, "Desktop · the daily driver".
    /// </summary>
    public bool IsComposerVisible => IsSessionOpen || IsPipelineRoom;

    /// <summary>Whether it can be typed into — a workflow room's workers are not conversational yet.</summary>
    public bool IsComposerEnabled => IsSessionOpen;

    /// <summary>The "start new chat" entry point (#290) belongs to neither of the two open states.</summary>
    public bool IsNewChatVisible => !IsSessionOpen && !IsPipelineRoom;

    /// <summary>
    /// Whether the room's shape — its steps, evidence, lineage and diff — is showing beside the
    /// transcript. Pure presentation state, remembered for the session but never projected.
    /// </summary>
    [ObservableProperty]
    private bool isShapePanelOpen;

    /// <summary>Adapters offered by the "start new chat" picker (#290) — populated from <see cref="Aer.Adapters.VendorCliPresence.Probe"/>, same source and same all-unavailable fallback ["claude","agy"] the existing template picker already uses, so the two entry points never disagree about what's offered.</summary>
    public ObservableCollection<string> AvailableAdapters { get; } = [];

    [ObservableProperty]
    private string newChatAdapter = "claude";

    [ObservableProperty]
    private string newChatWorkingDirectory = string.Empty;

    [ObservableProperty]
    private bool isStartingNewChat;

    private int _turnsCountAtSendTime;
    private string? _pendingUserMessage;
    // #323/#1290: tracks whether the last append was a streaming text delta, so a same-stream
    // continuation concatenates raw (it's one sentence arriving token by token) while a genuinely
    // new event (a new "status"/"tool" label, or "text" starting over) gets a separator first.
    private bool _lastProgressWasPartialText;

    /// <summary>
    /// The runtime permission gate rendered inline in the conversation (0022, #390), or null when the
    /// worker is not waiting on a permission. Set by <see cref="SurfacePendingPermission"/> from the
    /// projection's <c>PendingPermission</c> on every render — a projected fact, so it clears itself the
    /// moment the projection stops carrying one (a permission dies with its turn, 0022 §5).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPermission))]
    private PendingPermissionViewModel? pendingPermission;

    /// <summary>True while the worker is blocked on a permission — drives the inline gate's visibility.</summary>
    public bool HasPendingPermission => PendingPermission != null;

    /// <summary>
    /// The open workflow decisions rendered inline in the transcript as live cards (#1196/#1199), empty
    /// when nothing is paused. Reconciled by <see cref="SurfacePendingPermission"/> from the projection's
    /// paused steps on every render — a projected fact, so a card leaves the moment the projection stops
    /// carrying its step.
    /// </summary>
    /// <remarks>
    /// A collection, not one card: a room can hold several steps paused at once, which the product
    /// already models everywhere else it counts them (<c>RoomsViewModel.PausedStepCount</c>,
    /// <c>RoomProjectionLoader</c>'s own count). Showing the first and dropping the rest would be a
    /// transcript claiming to be the room's whole chronology while hiding part of it.
    /// </remarks>
    public ObservableCollection<PausedStepViewModel> PendingDecisions { get; } = [];

    /// <summary>True while at least one step is paused waiting for a decision — drives the inline decision cards' visibility.</summary>
    public bool HasPendingDecision => PendingDecisions.Count > 0;

    /// <summary>
    /// Reconciles the inline gate with the projection's current <paramref name="pending"/>: builds a
    /// fresh <see cref="PendingPermissionViewModel"/> when a new permission appears, keeps the existing
    /// instance while the same one is still open (so an in-flight <see cref="PendingPermissionViewModel.IsEnabled"/>
    /// toggle survives a poll that changed nothing else), and clears it when the projection no longer
    /// carries a permission. Idempotent — called from the same render path as
    /// <see cref="LoadFromMetadata"/> on every load/refresh.
    /// </summary>
    public void SurfacePendingPermission(Aer.Flow.Projection.PendingPermission? pending, AnswerPermissionDelegate answer)
        => SurfacePendingPermission(pending, null, answer, null, false, null, null, null);

    public void SurfacePendingPermission(
        Aer.Flow.Projection.PendingPermission? pending,
        IReadOnlyList<PermissionAnswer>? permissionAnswers,
        AnswerPermissionDelegate answer)
        => SurfacePendingPermission(pending, permissionAnswers, answer, null, false, null, null, null);

    public void SurfacePendingPermission(
        Aer.Flow.Projection.PendingPermission? pending,
        IReadOnlyList<PermissionAnswer>? permissionAnswers,
        AnswerPermissionDelegate answer,
        IReadOnlyList<DormancyTransition>? dormancyTransitions,
        bool isDormant = false,
        Action? wake = null)
        => SurfacePendingPermission(pending, permissionAnswers, answer, dormancyTransitions, isDormant, wake, null, null);

    public void SurfacePendingPermission(
        Aer.Flow.Projection.PendingPermission? pending,
        IReadOnlyList<PermissionAnswer>? permissionAnswers,
        AnswerPermissionDelegate answer,
        IReadOnlyList<DormancyTransition>? dormancyTransitions,
        bool isDormant = false,
        Action? wake = null,
        IReadOnlyList<RecordedDecisionMoment>? recordedDecisionMoments = null,
        IReadOnlyList<PausedStepViewModel>? pendingDecisions = null)
    {
        if (dormancyTransitions != null)
        {
            _dormancyTransitions = dormancyTransitions;
        }

        if (recordedDecisionMoments != null)
        {
            _recordedDecisionMoments = recordedDecisionMoments;
        }

        _isDormant = isDormant;
        _wakeAction = wake;

        if (permissionAnswers != null || dormancyTransitions != null || recordedDecisionMoments != null)
        {
            if (permissionAnswers != null)
            {
                _permissionAnswers = permissionAnswers;
            }

            RebuildMessages();
        }

        if (pending is null)
        {
            PendingPermission = null;
        }
        else if (PendingPermission?.PermissionRequestId != pending.PermissionRequestId)
        {
            PendingPermission = new PendingPermissionViewModel(pending, answer);
        }

        ReconcilePendingDecisions(pendingDecisions ?? []);
    }

    /// <summary>
    /// Brings <see cref="PendingDecisions"/> into line with the projection's paused steps, keyed on
    /// (step, execution): a card whose key is still open keeps its existing instance, so an in-flight
    /// <see cref="PausedStepViewModel.IsEnabled"/> toggle survives a poll that changed nothing else —
    /// the same discipline <see cref="PendingPermission"/> has kept since #1145. A key that is gone
    /// leaves; a key that is new arrives.
    /// </summary>
    /// <summary>
    /// Re-points the transcript's cards at the room's current paused steps. Called whenever the room
    /// rebuilds them — since #350, an unchanged pause reuses its existing instance, so most calls are
    /// now a no-op replace; a genuinely new or departed pause point is what still needs syncing.
    /// </summary>
    public void SyncPendingDecisions(IReadOnlyList<PausedStepViewModel> pausedSteps)
    {
        if (!IsPipelineRoom)
        {
            return;
        }

        ReconcilePendingDecisions(pausedSteps, adoptRebuiltInstances: true);
    }

    private void ReconcilePendingDecisions(IReadOnlyList<PausedStepViewModel> pausedSteps, bool adoptRebuiltInstances = false)
    {
        var before = PendingDecisions.Count;

        for (var index = PendingDecisions.Count - 1; index >= 0; index--)
        {
            var existing = PendingDecisions[index];
            if (!pausedSteps.Any(step => step.StepId == existing.StepId && step.ExecutionId == existing.ExecutionId))
            {
                PendingDecisions.RemoveAt(index);
            }
        }

        foreach (var step in pausedSteps)
        {
            var existingIndex = -1;
            for (var index = 0; index < PendingDecisions.Count; index++)
            {
                if (PendingDecisions[index].StepId == step.StepId && PendingDecisions[index].ExecutionId == step.ExecutionId)
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                PendingDecisions.Add(step);
            }
            else if (adoptRebuiltInstances && !ReferenceEquals(PendingDecisions[existingIndex], step))
            {
                // Only the room's own rebuild is authoritative about identity. A poll passing a
                // freshly-projected card for the same key keeps the live one instead, so an in-flight
                // IsEnabled toggle survives — the #1145 discipline the caller default preserves.
                PendingDecisions[existingIndex] = step;
            }
        }

        if (PendingDecisions.Count != before)
        {
            OnPropertyChanged(nameof(HasPendingDecision));
        }
    }

    /// <summary>
    /// The durable turn count as of the last <see cref="LoadFromMetadata"/> — the send baseline
    /// (#1074). Completion keys on <see cref="Aer.Adapters.SessionMetadata"/>'s <c>Turns.Count</c>
    /// growing past the baseline (see line ~<see cref="IsSending"/> handling below), so the baseline
    /// must be that same durable count, never one derived from <see cref="Messages"/> — which carries
    /// the optimistic pending echo and so is not stable across polls.
    /// </summary>
    public int LastKnownTurnsCount { get; private set; }

    /// <summary>
    /// True after a send's dispatch failed, until the operator's next send or enqueue (#1074). The
    /// poll pauses draining while it is set, so one failed queued send neither cascades the rest of
    /// the queue into repeated failures nor busy-retries every tick. Deliberately separate from
    /// <see cref="StatusText"/>, which also carries *success* notices (mode changes, "context
    /// cleared") that must never gate the queue.
    /// </summary>
    public bool LastSendFailed { get; private set; }

    /// <summary>
    /// True when the drain tick may post the queued head (#1167). All three clauses are load-bearing:
    /// a turn in flight (<see cref="IsSending"/>), a queue paused by a failed dispatch
    /// (<see cref="LastSendFailed"/>), and an OPEN PERMISSION GATE each hold the drain. The gate
    /// clause is not redundant with <see cref="IsSending"/>: a turn another client started (the
    /// phone) keeps the gate open while THIS client's IsSending is false — before the clause was
    /// explicit, the hold was an accident of IsSending's lifecycle and a resumed backlog could
    /// drain into a blocked worker.
    /// </summary>
    public bool CanDrainQueue => !IsSending && !LastSendFailed && !HasPendingPermission;

    /// <summary>
    /// True when a typed send must JOIN the queue instead of posting a turn directly (#1074's
    /// never-block composer): a turn in flight, a non-empty queue (FIFO), or an open permission
    /// gate. The gate clause is #1167's second call site — the same cross-client shape
    /// <see cref="CanDrainQueue"/>'s doc explains, found by that PR's second reader: without it a
    /// FRESH typed send (unlike a queued one) posted straight past a phone-raised gate and sat
    /// rendered "in flight" behind another client's unanswered ask. Deliberately NOT
    /// <c>!CanDrainQueue</c>: <see cref="LastSendFailed"/> must not reroute a typed retry into the
    /// queue — the operator's next direct send after a failure stays a direct send (#1074).
    /// </summary>
    public bool SendJoinsQueue => IsSending || HasQueuedMessages || HasPendingPermission;

    /// <summary>Rebuilds <see cref="Messages"/> from a freshly loaded/polled <see cref="SessionMetadata"/> — the chat view's counterpart of <see cref="MainWindowViewModel.RebuildRoomSteps"/>.</summary>
    public void LoadFromMetadata(SessionMetadata metadata, string roomDirectoryPath)
    {
        _lastMetadata = metadata;
        // A chat session has no workflow to switch, so it resets to the default rather than inheriting
        // the last workflow room's OFF (#1216). Deliberately NOT in Clear(): the shell calls Clear()
        // AFTER the projection has already rendered, so resetting there wiped the value the journal
        // had just supplied — and the render's fingerprint then short-circuited every retry, leaving
        // a switched-off room reading ON for as long as it stayed open.
        IsWorkflowOn = true;
        SessionId = metadata.SessionId;
        RoomDirectoryPath = roomDirectoryPath;
        CurrentAdapter = metadata.CurrentAdapter;
        LastKnownTurnsCount = metadata.Turns.Count;
        // Daily-driver header (02-screens.md): the room name + a worker chip, not "vendor — turn N".
        // The name is the SAME canonical derivation the switcher renders — never a second one (#461/#976).
        HeadlineText = RoomProjectionLoader.FriendlyNameFor(roomDirectoryPath);
        WorkerChipText = metadata.CurrentAdapter;
        RaiseOpenStateChanged();

        RebuildMessages();
    }

    /// <summary>
    /// Opens a workflow room in this rendering (#1196 slice 3): same transcript, same decision
    /// cards, no session behind it. There is no <see cref="Aer.Adapters.SessionMetadata"/> for such a
    /// room and there never will be — its content is the room's own event streams, which
    /// <see cref="SurfacePendingPermission"/> already supplies.
    /// </summary>
    /// <param name="pausedSteps">
    /// The room's open decisions as the render that just ran projected them. Passed in rather than
    /// left to the next render because the caller's order is load-then-open: the decisions are
    /// already built by the time this runs, and <see cref="Clear"/> — which the caller rightly calls
    /// first, to drop any previous room's turns — takes them with it. Driving the built app is what
    /// showed that: routing, composer and toggle were all correct on screen and the card simply was
    /// not there, with every test green.
    /// </param>
    public void OpenPipelineRoom(string roomDirectoryPath, IReadOnlyList<PausedStepViewModel>? pausedSteps = null)
    {
        IsPipelineRoom = true;
        ReconcilePendingDecisions(pausedSteps ?? []);
        OnPropertyChanged(nameof(HasPendingDecision));
        // The SAME canonical name derivation the switcher and the session path use, never a second
        // one (#461/#976).
        HeadlineText = RoomProjectionLoader.FriendlyNameFor(roomDirectoryPath);
        RaiseOpenStateChanged();

        RebuildMessages();
    }

    /// <summary>
    /// <see cref="IsSessionOpen"/> is derived from a plain field rather than an observable property,
    /// so every place that changes what "open" means has to say so — and the three states that read
    /// from it move together or the composer contradicts the transcript.
    /// </summary>
    private void RaiseOpenStateChanged()
    {
        OnPropertyChanged(nameof(IsSessionOpen));
        OnPropertyChanged(nameof(IsComposerVisible));
        OnPropertyChanged(nameof(IsComposerEnabled));
        OnPropertyChanged(nameof(IsNewChatVisible));
    }

    private void RebuildMessages()
    {
        Messages.Clear();

        // A workflow room has no session metadata and never will (#1196 slice 3), so the early
        // return that used to guard this method would have left its transcript permanently empty —
        // every decision row and pause dropped on the floor, in the surface built to show them.
        // Its streams come from the projection instead, by way of SurfacePendingPermission.
        if (_lastMetadata is null && !IsPipelineRoom)
        {
            return;
        }

        var turns = _lastMetadata?.Turns ?? [];
        var answers = _answersClearedThrough is { } clearedThroughAnswers
            ? _permissionAnswers.Where(a => a.AnsweredAt > clearedThroughAnswers).ToList()
            : _permissionAnswers;
        var transitions = _answersClearedThrough is { } clearedThroughTransitions
            ? _dormancyTransitions.Where(t => t.Timestamp > clearedThroughTransitions).ToList()
            : _dormancyTransitions;
        // A decision with no recorded time is treated as older than any clear, so it goes with the
        // transcript the person cleared. That is the same reading that puts it before the first
        // stamped row in the merge below — unknown means "from before what I can place" in both
        // places — and it is the conservative half of the choice: a clear is a request to stop
        // seeing what came before, and a moment that cannot be placed cannot be shown to be after it.
        // The sibling lists never face this; PermissionAnswer.AnsweredAt and DormancyTransition.Timestamp
        // are both non-nullable.
        //
        // Sorted here rather than assumed: the merge below only ever peeks the head of each stream,
        // so an out-of-order list renders out of order silently. RoomProjectionLoader appends these
        // in journal read order, which is ascending today only because a null timestamp can come
        // only from a journal written before #1197 — the oldest lines in the file. That is a fact
        // about history, not a guarantee the type carries (LogEntry's writer timestamp is an
        // ordinary nullable), so the ordering the merge needs is established here instead of hoped for.
        var decisions = (_answersClearedThrough is { } clearedThroughDecisions
                ? _recordedDecisionMoments.Where(d => (d.RecordedAt ?? DateTimeOffset.MinValue) > clearedThroughDecisions)
                : _recordedDecisionMoments)
            .OrderBy(d => d.RecordedAt ?? DateTimeOffset.MinValue)
            .ToList();

        var latestEnteredTransition = _isDormant ? transitions.LastOrDefault(t => t.IsEntered) : null;

        int turnIdx = 0;
        int ansIdx = 0;
        int transIdx = 0;
        int decIdx = 0;

        while (turnIdx < turns.Count || ansIdx < answers.Count || transIdx < transitions.Count || decIdx < decisions.Count)
        {
            var turnTs = turnIdx < turns.Count ? turns[turnIdx].ExecutedAt : DateTimeOffset.MaxValue;
            var ansTs = ansIdx < answers.Count ? answers[ansIdx].AnsweredAt : DateTimeOffset.MaxValue;
            var transTs = transIdx < transitions.Count ? transitions[transIdx].Timestamp : DateTimeOffset.MaxValue;
            var decTs = decIdx < decisions.Count ? (decisions[decIdx].RecordedAt ?? DateTimeOffset.MinValue) : DateTimeOffset.MaxValue;

            // The decision arm goes LAST, and every arm above it now also outranks decTs. On an exact
            // tie this repo already had a precedence — turn, then answer, then transition — and
            // putting decisions first would have quietly reversed it against turns, a new rule
            // nothing had asked for. Appending instead leaves every pre-existing pair rendering
            // exactly as before (with no decisions in play decTs is MaxValue, so the added clauses
            // are vacuously true) and pins the new one: a decision recorded at the same instant as a
            // turn renders after it. Pinned by
            // ChatViewModelTests.SurfacePendingPermission_DecisionAndTurnAtTheSameInstant_TurnRendersFirst.
            if (turnTs <= ansTs && turnTs <= transTs && turnTs <= decTs)
            {
                AddTurnMessages(turns[turnIdx]);
                turnIdx++;
            }
            else if (ansTs <= transTs && ansTs <= decTs)
            {
                AddAnswerMessage(answers[ansIdx]);
                ansIdx++;
            }
            else if (transTs <= decTs)
            {
                AddDormancyMessage(transitions[transIdx], isLatestEntered: _isDormant && transitions[transIdx] == latestEnteredTransition);
                transIdx++;
            }
            else
            {
                AddRecordedDecisionMessage(decisions[decIdx]);
                decIdx++;
            }
        }

        if (IsSending && turns.Count > _turnsCountAtSendTime)
        {
            IsSending = false;
            LiveProgressText = string.Empty;
            _lastProgressWasPartialText = false;
            _pendingUserMessage = null;
        }
        else if (IsSending && _pendingUserMessage is { } pending)
        {
            // The turn hasn't landed in Turns yet (still running, or the send hasn't reached the
            // daemon's background task) -- show the user's own message immediately rather than
            // leaving the box looking like Send did nothing until the response completes.
            Messages.Add(new ChatMessageViewModel("You", pending, DateTimeOffset.UtcNow, IsFromUser: true));
        }
    }

    private void AddTurnMessages(SessionTurn turn)
    {
        Messages.Add(new ChatMessageViewModel("You", turn.HumanMessage, turn.ExecutedAt, IsFromUser: true));

        if (turn.IsDormancyAnswer)
        {
            // #1179: the room was dormant when this message arrived -- the PRODUCT answered with the
            // dormancy state instead of dispatching a worker turn, so neither AssistantResponse nor
            // ErrorMessage below is ever populated on this turn. Wake gates on _isDormant alone here,
            // not #1178's per-transition "latest entered" rule: every dormancy-answer turn while the
            // room is still dormant is an equally valid place to offer the same Wake action, so the
            // cross-collection "newest dormancy-shaped entry" bookkeeping buys nothing (#1179).
            Messages.Add(new ChatMessageViewModel(
                "System",
                "Still dormant — waking is yours to choose.",
                turn.ExecutedAt,
                IsFromUser: false,
                IsDormancy: true,
                Wake: _isDormant ? _wakeAction : null));
            return;
        }

        // #1180 (the SessionTurn.IsExhausted doc carries the 0026 framing): checked
        // BEFORE the ErrorMessage/IsFailure arm below so the failure card is unreachable for it, even
        // though ErrorMessage is still populated on this turn (it feeds the out-of-plan card's Copy).
        // A partial response can coexist with exhaustion (the vendor said something before refusing),
        // so it still renders first.
        if (turn.IsExhausted)
        {
            if (turn.AssistantResponse is { } partialResponse)
            {
                Messages.Add(new ChatMessageViewModel(turn.Vendor, partialResponse, turn.ExecutedAt, IsFromUser: false));
            }

            Messages.Add(new ChatMessageViewModel(
                turn.Vendor,
                PlainLanguage.ForExhaustion(turn.ExhaustedUntil),
                turn.ExecutedAt,
                IsFromUser: false,
                IsOutOfPlan: true,
                CopyText: turn.ErrorMessage));
            return;
        }

        if (turn.AssistantResponse is { } response)
        {
            Messages.Add(new ChatMessageViewModel(turn.Vendor, response, turn.ExecutedAt, IsFromUser: false));
        }

        if (!string.IsNullOrEmpty(turn.ErrorMessage))
        {
            var error = turn.ErrorMessage;
            Messages.Add(new ChatMessageViewModel(
                turn.Vendor,
                error,
                turn.ExecutedAt,
                IsFromUser: false,
                IsFailure: true,
                PrepareFixPrompt: () => InputText = $"The last turn failed with:\n> {error}\nPlease diagnose and fix it."));
        }
    }

    private void AddAnswerMessage(PermissionAnswer answer)
    {
        var text = FormatPermissionAnswerWording(answer);
        Messages.Add(new ChatMessageViewModel("System", text, answer.AnsweredAt, IsFromUser: false, IsSystem: true));
    }

    private void AddRecordedDecisionMessage(RecordedDecisionMoment moment)
    {
        var text = PlainLanguage.ForRecordedDecision(moment);
        var timestamp = moment.RecordedAt ?? DateTimeOffset.MinValue;
        Messages.Add(new ChatMessageViewModel("System", text, timestamp, IsFromUser: false, IsSystem: true));
    }

    private void AddDormancyMessage(DormancyTransition transition, bool isLatestEntered)
    {
        if (transition.IsEntered)
        {
            var text = $"Dormant — stopped after {transition.ConsecutiveFailures} machine turns without progress.";
            if (!string.IsNullOrEmpty(transition.Detail))
            {
                text += $"\n{transition.Detail}";
            }

            Action? wake = isLatestEntered ? _wakeAction : null;
            Messages.Add(new ChatMessageViewModel(
                "System",
                text,
                transition.Timestamp,
                IsFromUser: false,
                IsSystem: false,
                IsDormancy: true,
                Wake: wake));
        }
        else
        {
            var text = $"Woken by {transition.ClearedBy}.";
            Messages.Add(new ChatMessageViewModel(
                "System",
                text,
                transition.Timestamp,
                IsFromUser: false,
                IsSystem: true));
        }
    }

    public static string FormatPermissionAnswerWording(PermissionAnswer answer)
    {
        if (answer.WasRevoked)
        {
            var reasonText = answer.Reason switch
            {
                "turn_ended" => "turn ended",
                "timeout" => "timed out",
                _ => answer.Reason ?? string.Empty
            };
            return $"Expired unanswered — {reasonText}";
        }

        if (answer.DecisionKind.StartsWith("Allow", StringComparison.Ordinal))
        {
            var scope = answer.DecisionKind switch
            {
                PermissionDecisionKind.AllowRoom => "for this room",
                PermissionDecisionKind.AllowCommandInRoom => "command in this room",
                PermissionDecisionKind.AllowCommandAnyRoom => "command in any room",
                _ => "once"
            };
            return $"Allowed {scope} — {answer.ToolName}";
        }

        var reasonSuffix = !string.IsNullOrEmpty(answer.Reason) ? $": {answer.Reason}" : string.Empty;
        return $"Denied — {answer.ToolName}{reasonSuffix}";
    }

    /// <summary>Marks a send as in flight and captures enough state for <see cref="LoadFromMetadata"/> to detect completion. Called by <c>MainWindow</c> right before it posts the operator's just-typed message; clears the composer.</summary>
    public void BeginSend(string message, int currentTurnsCount)
        => MarkInFlight(message, currentTurnsCount, clearInput: true);

    /// <summary>
    /// Marks a *queued* message's send in flight (#1074) — identical to <see cref="BeginSend"/> except
    /// it leaves <see cref="InputText"/> alone, because the operator may be typing the next message
    /// while an earlier queued one drains. Called by <c>MainWindow</c>'s poll when the queue drains.
    /// </summary>
    public void BeginDrainedSend(string message, int currentTurnsCount)
        => MarkInFlight(message, currentTurnsCount, clearInput: false);

    private void MarkInFlight(string message, int currentTurnsCount, bool clearInput)
    {
        _turnsCountAtSendTime = currentTurnsCount;
        _pendingUserMessage = message;
        LiveProgressText = string.Empty;
        _lastProgressWasPartialText = false;
        StatusText = string.Empty;
        // A fresh send attempt clears the drain-pause flag (#1074): a queued send that failed pauses
        // the drain, and the operator acting again — a new send, or a new enqueue — is what resumes it.
        LastSendFailed = false;
        IsSending = true;
        if (clearInput)
        {
            InputText = string.Empty;
        }
    }

    /// <summary>
    /// Queues a message typed while a turn is in flight *or while other messages are already queued*
    /// (#1074), and clears the composer — the send waits for <c>MainWindow</c>'s poll to drain it on
    /// turn completion. Kept off the daemon deliberately: two concurrent turns are exactly what the
    /// queue exists to prevent. Also clears <see cref="LastSendFailed"/> so enqueuing after a failed
    /// drain resumes draining (otherwise, with the queue non-empty every send enqueues, and nothing
    /// would ever clear the pause).
    /// </summary>
    public void EnqueueMessage(string message)
    {
        QueuedMessages.Add(new QueuedChatMessageViewModel(message, RemoveQueuedMessage));
        InputText = string.Empty;
        LastSendFailed = false;
        OnPropertyChanged(nameof(HasQueuedMessages));
    }

    /// <summary>
    /// Reads the head item without removing it (#1074) — the drain peeks, sends, and only
    /// <see cref="RemoveQueuedMessage"/>s that exact item on a successful dispatch, so a failed drained
    /// send leaves the message queued rather than dropping it. Returns the <em>item</em>, not its text,
    /// so the drain removes it by identity: the head stays live (Remove button and all) during the
    /// daemon round trip, and a positional dequeue-index-0 would drop the wrong message if the operator
    /// removed something meanwhile. Returns false (and null) on an empty queue.
    /// </summary>
    public bool TryPeekQueuedMessage(out QueuedChatMessageViewModel? head)
    {
        head = QueuedMessages.Count == 0 ? null : QueuedMessages[0];
        return head is not null;
    }

    /// <summary>
    /// Removes one queued message by identity (#1074) — the Remove button's callback, and the drain's
    /// consume-on-successful-dispatch. Identity, never index, so a removal that races the head's
    /// in-flight dispatch drops exactly the intended item and nothing behind it. No-ops if the item is
    /// already gone (the operator removed it mid-dispatch). Deliberately does NOT touch
    /// <see cref="LastSendFailed"/>: a remove that interleaves a failing dispatch's await would set the
    /// flag before <see cref="FailSend"/> re-set it, so a paused queue resumes only on the next
    /// send/enqueue — race-free, and it self-heals on the operator's next message.
    /// </summary>
    internal void RemoveQueuedMessage(QueuedChatMessageViewModel item)
    {
        if (QueuedMessages.Remove(item))
        {
            OnPropertyChanged(nameof(HasQueuedMessages));
        }
    }

    /// <summary>Called on a failed dispatch (the daemon rejected or was unreachable) — <see cref="LoadFromMetadata"/> never runs to clear <see cref="IsSending"/> in that case since no turn was ever started.</summary>
    public void FailSend(string errorMessage)
    {
        IsSending = false;
        _pendingUserMessage = null;
        StatusText = errorMessage;
        // Pauses the poll's queue drain (#1074) until the operator's next send or enqueue — a failed
        // queued send stays queued (the drain peeks, only removes the item on success), so this stops
        // it retrying every tick without dropping it.
        LastSendFailed = true;
    }

    /// <summary>
    /// Appends one live in-turn stream fragment (<c>/api/ws/progress</c>) to <see cref="LiveProgressText"/>.
    /// #323/#1290: events are discrete labels ("Session started", a tool name, a status) except
    /// <c>kind:"text", IsPartial:true</c> streaming deltas, which are token-level fragments of one
    /// sentence — see <see cref="WorkerProgressEvent"/>'s producers in ClaudeWorkerAdapter/AgyWorkerAdapter.
    /// A continuing partial-text run concatenates raw; anything else gets a separator first so
    /// distinct events don't run together into one unreadable word.
    /// </summary>
    public void AppendProgress(WorkerProgressEvent progressEvent)
    {
        var isContinuingPartialText = _lastProgressWasPartialText && progressEvent is { Kind: "text", IsPartial: true };
        if (!isContinuingPartialText && LiveProgressText.Length > 0)
        {
            LiveProgressText += " · ";
        }

        LiveProgressText += progressEvent.Text;
        _lastProgressWasPartialText = progressEvent is { Kind: "text", IsPartial: true };
    }

    /// <summary>
    /// Populates <see cref="InvokableCommands"/>/<see cref="InfoCommands"/> from a fresh
    /// <c>GET /api/sessions/{id}/commands</c> result (M24 Phase 2 follow-up) — recently-used items
    /// first within each list, matching this vendor's own item order otherwise.
    /// </summary>
    public void LoadCommands(RoomClient.SessionCommandsResult result)
    {
        InvokableCommands.Clear();
        InfoCommands.Clear();

        var recentRank = result.RecentlyUsed
            .Select((name, index) => (name, index))
            .ToDictionary(t => t.name, t => t.index, StringComparer.Ordinal);

        var ordered = result.Items
            .Select(item => new ChatCapabilityItemViewModel(item.Name, item.Kind, item.Description, recentRank.ContainsKey(item.Name), item.IsInvokable))
            .OrderBy(item => recentRank.TryGetValue(item.Name, out var rank) ? rank : int.MaxValue);

        foreach (var item in ordered)
        {
            (item.IsInvokable ? InvokableCommands : InfoCommands).Add(item);
        }
    }

    /// <summary>
    /// Called when /clear wipes the vendor turns so already-rendered permission answers don't
    /// resurface as orphan bubbles — see <see cref="_answersClearedThrough"/> for the mechanism and
    /// its restart limitation.
    /// </summary>
    public void MarkTranscriptCleared()
    {
        var latestAnswer = _permissionAnswers.Count > 0 ? _permissionAnswers.Max(a => a.AnsweredAt) : (DateTimeOffset?)null;
        var latestTransition = _dormancyTransitions.Count > 0 ? _dormancyTransitions.Max(t => t.Timestamp) : (DateTimeOffset?)null;

        if (latestAnswer != null && latestTransition != null)
        {
            _answersClearedThrough = latestAnswer > latestTransition ? latestAnswer : latestTransition;
        }
        else if (latestAnswer != null)
        {
            _answersClearedThrough = latestAnswer;
        }
        else if (latestTransition != null)
        {
            _answersClearedThrough = latestTransition;
        }
    }

    public void Clear()
    {
        _lastMetadata = null;
        _permissionAnswers = [];
        _dormancyTransitions = [];
        _answersClearedThrough = null;
        _isDormant = false;
        _wakeAction = null;
        SessionId = null;
        RoomDirectoryPath = null;
        CurrentAdapter = null;
        HeadlineText = "No room open.";
        WorkerChipText = null;
        StatusText = string.Empty;
        LiveProgressText = string.Empty;
        _lastProgressWasPartialText = false;
        IsSending = false;
        _pendingUserMessage = null;
        LastSendFailed = false;
        LastKnownTurnsCount = 0;
        NewChatWorkingDirectory = string.Empty;
        IsStartingNewChat = false;
        CurrentMode = null;
        Messages.Clear();
        QueuedMessages.Clear();
        InvokableCommands.Clear();
        InfoCommands.Clear();
        IsCommandMenuOpen = false;
        IsPipelineRoom = false;
        IsShapePanelOpen = false;
        PendingDecisions.Clear();
        OnPropertyChanged(nameof(HasQueuedMessages));
        OnPropertyChanged(nameof(HasPendingDecision));
        RaiseOpenStateChanged();
    }

    /// <summary>Populates <see cref="AvailableAdapters"/> from a live PATH probe (#290) — same source and fallback as the desktop template picker's own vendor combo, so the two entry points never disagree about what's offered. Safe to call repeatedly; caller decides cadence (once at startup is enough since PATH doesn't change mid-session in practice).</summary>
    public void PopulateAvailableAdapters(IReadOnlyList<VendorCliStatus>? probeResult = null)
    {
        var probed = probeResult ?? VendorCliPresence.Probe();
        // AdapterName, not BinaryName: this value becomes StartSessionRequest.Adapter, and the
        // daemon resolves adapters by name ("agy"), not by CLI binary ("agy").
        var available = probed.Where(p => p.IsAvailable).Select(p => p.AdapterName).ToList();
        if (available.Count == 0)
        {
            available = ["claude", "agy"]; // vocabulary-ok: adapter contract keys, not display text
        }

        AvailableAdapters.Clear();
        foreach (var adapter in available)
        {
            AvailableAdapters.Add(adapter);
        }

        if (!AvailableAdapters.Contains(NewChatAdapter))
        {
            NewChatAdapter = AvailableAdapters[0];
        }
        else
        {
            RefreshNewChatAdapterSelection();
        }
    }

    /// <summary>
    /// Re-asserts <see cref="NewChatAdapter"/> for the ComboBox (#981): a SelectedItem applied
    /// while ItemsSource was still empty is coerced to no-selection by the control and never
    /// re-evaluated when the items arrive — and an assignment that does not change the value
    /// raises nothing on its own. Callers that route a user into the new-chat bar (#617's ask
    /// affordance) call this so the selection is visible, not merely held.
    /// </summary>
    public void RefreshNewChatAdapterSelection() => OnPropertyChanged(nameof(NewChatAdapter));
}

/// <summary>
/// One message waiting to send while a turn is in flight (#1074) — its text, and a Remove that pulls
/// it from <see cref="ChatViewModel.QueuedMessages"/> before it sends. The queue is a projection the
/// operator can edit, never a second send authority: removing an entry only stops it queuing.
/// </summary>
public sealed partial class QueuedChatMessageViewModel(string text, Action<QueuedChatMessageViewModel> remove)
{
    public string Text { get; } = text;

    [RelayCommand]
    private void Remove() => remove(this);
}
