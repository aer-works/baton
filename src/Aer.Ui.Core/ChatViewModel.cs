using System.Collections.ObjectModel;
using System.Linq;
using Aer.Adapters;
using Aer.Flow.Projection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>One rendered row in <see cref="ChatViewModel.Messages"/> — a human turn or an assistant response, never both (M24 Phase 1 desktop chat UI, issue #262).</summary>
public sealed record ChatMessageViewModel(string SenderLabel, string Text, DateTimeOffset Timestamp, bool IsFromUser, bool IsSystem = false);

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
    /// Reconciles the inline gate with the projection's current <paramref name="pending"/>: builds a
    /// fresh <see cref="PendingPermissionViewModel"/> when a new permission appears, keeps the existing
    /// instance while the same one is still open (so an in-flight <see cref="PendingPermissionViewModel.IsEnabled"/>
    /// toggle survives a poll that changed nothing else), and clears it when the projection no longer
    /// carries a permission. Idempotent — called from the same render path as
    /// <see cref="LoadFromMetadata"/> on every load/refresh.
    /// </summary>
    public void SurfacePendingPermission(Aer.Flow.Projection.PendingPermission? pending, AnswerPermissionDelegate answer)
        => SurfacePendingPermission(pending, null, answer);

    public void SurfacePendingPermission(
        Aer.Flow.Projection.PendingPermission? pending,
        IReadOnlyList<PermissionAnswer>? permissionAnswers,
        AnswerPermissionDelegate answer)
    {
        if (permissionAnswers != null)
        {
            _permissionAnswers = permissionAnswers;
            RebuildMessages();
        }

        if (pending is null)
        {
            PendingPermission = null;
            return;
        }

        if (PendingPermission?.PermissionRequestId == pending.PermissionRequestId)
        {
            return; // same open permission — keep the live instance rather than resetting its state
        }

        PendingPermission = new PendingPermissionViewModel(pending, answer);
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

    /// <summary>Rebuilds <see cref="Messages"/> from a freshly loaded/polled <see cref="SessionMetadata"/> — the chat view's counterpart of <see cref="MainWindowViewModel.RebuildRoomSteps"/>.</summary>
    public void LoadFromMetadata(SessionMetadata metadata, string roomDirectoryPath)
    {
        _lastMetadata = metadata;
        SessionId = metadata.SessionId;
        RoomDirectoryPath = roomDirectoryPath;
        CurrentAdapter = metadata.CurrentAdapter;
        LastKnownTurnsCount = metadata.Turns.Count;
        // Daily-driver header (02-screens.md): the room name + a worker chip, not "vendor — turn N".
        // The name is the SAME canonical derivation the switcher renders — never a second one (#461/#976).
        HeadlineText = RoomProjectionLoader.FriendlyNameFor(roomDirectoryPath);
        WorkerChipText = metadata.CurrentAdapter;
        OnPropertyChanged(nameof(IsSessionOpen));

        RebuildMessages();
    }

    private void RebuildMessages()
    {
        Messages.Clear();
        if (_lastMetadata is null)
        {
            return;
        }

        var turns = _lastMetadata.Turns;
        int turnIdx = 0;
        int ansIdx = 0;

        while (turnIdx < turns.Count || ansIdx < _permissionAnswers.Count)
        {
            if (turnIdx < turns.Count && ansIdx < _permissionAnswers.Count)
            {
                var turn = turns[turnIdx];
                var answer = _permissionAnswers[ansIdx];

                if (turn.ExecutedAt <= answer.AnsweredAt)
                {
                    AddTurnMessages(turn);
                    turnIdx++;
                }
                else
                {
                    AddAnswerMessage(answer);
                    ansIdx++;
                }
            }
            else if (turnIdx < turns.Count)
            {
                AddTurnMessages(turns[turnIdx]);
                turnIdx++;
            }
            else
            {
                AddAnswerMessage(_permissionAnswers[ansIdx]);
                ansIdx++;
            }
        }

        if (IsSending && turns.Count > _turnsCountAtSendTime)
        {
            IsSending = false;
            LiveProgressText = string.Empty;
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
        if (turn.AssistantResponse is { } response)
        {
            Messages.Add(new ChatMessageViewModel(turn.Vendor, response, turn.ExecutedAt, IsFromUser: false));
        }
    }

    private void AddAnswerMessage(PermissionAnswer answer)
    {
        var text = FormatPermissionAnswerWording(answer);
        Messages.Add(new ChatMessageViewModel("System", text, answer.AnsweredAt, IsFromUser: false, IsSystem: true));
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

    /// <summary>Appends one live in-turn stream fragment (<c>/api/ws/progress</c>) to <see cref="LiveProgressText"/>.</summary>
    public void AppendProgress(WorkerProgressEvent progressEvent)
    {
        LiveProgressText += progressEvent.Text;
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

    public void Clear()
    {
        _lastMetadata = null;
        _permissionAnswers = [];
        SessionId = null;
        RoomDirectoryPath = null;
        CurrentAdapter = null;
        HeadlineText = "No room open.";
        WorkerChipText = null;
        StatusText = string.Empty;
        LiveProgressText = string.Empty;
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
        OnPropertyChanged(nameof(HasQueuedMessages));
        OnPropertyChanged(nameof(IsSessionOpen));
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
