using System.Collections.ObjectModel;
using Aer.Flow.Concurrency;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// <see cref="MainWindow"/>'s ViewModel layer (M15 Phase 2, issue #138) — introduced for exactly the
/// surface M14 Phase 1 named as the potential second concrete need: the paused-step decision buttons,
/// whose enabled state is tied jointly to projected state (<see cref="PausedSteps"/>) and an
/// in-flight mutation (<see cref="IsMutationInFlight"/>). The rest of the window's read-only
/// rendering (DAG, history, lineage, diff) is untouched, still direct code-behind control
/// manipulation — this type does not attempt to own that.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<PausedStepViewModel> PausedSteps { get; } = [];

    /// <summary>The ▤ front door (M19 Phase 2, #187) — see <see cref="HomeViewModel"/>.</summary>
    public HomeViewModel Home { get; } = new();

    /// <summary>
    /// Which shell section is active (M19 Phase 2, #187) — pure presentation state (like a text
    /// box's contents, UI spec §4), never a projected fact. Opening a room navigates to
    /// <see cref="ShellSection.Task"/>; everything else is the user's own navigation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeVisible))]
    [NotifyPropertyChangedFor(nameof(IsRoomVisible))]
    [NotifyPropertyChangedFor(nameof(IsAuthorVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsChatVisible))]
    [NotifyPropertyChangedFor(nameof(IsRoomsVisible))]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    [NotifyPropertyChangedFor(nameof(IsRoomsFrontDoorSelected))]
    private ShellSection currentSection = ShellSection.Home;

    public bool IsHomeVisible => CurrentSection == ShellSection.Home;
    public bool IsRoomVisible => CurrentSection == ShellSection.Task;
    public bool IsAuthorVisible => CurrentSection == ShellSection.Author;
    public bool IsSettingsVisible => CurrentSection == ShellSection.Settings;
    public bool IsChatVisible => CurrentSection == ShellSection.Chat;
    public bool IsRoomsVisible => CurrentSection == ShellSection.Rooms;

    /// <summary>
    /// The Settings → Appearance theme choice (#1068) — one of <see cref="ThemeNames"/>. Pure
    /// presentation state like <see cref="CurrentSection"/>: the actual variant is applied to the
    /// application by the Avalonia layer, and the persisted value is loaded into this at startup.
    /// Defaults to <see cref="ThemeNames.System"/> (follow the OS), the app's behaviour before the
    /// control existed. The three <c>IsTheme*</c> flags drive the toggle's selected state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeLight))]
    [NotifyPropertyChangedFor(nameof(IsThemeDark))]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem))]
    private string themePreference = ThemeNames.System;

    public bool IsThemeLight => ThemePreference == ThemeNames.Light;
    public bool IsThemeDark => ThemePreference == ThemeNames.Dark;
    public bool IsThemeSystem => ThemePreference == ThemeNames.System;

    /// <summary>
    /// Whether the shell is showing an opened record, whichever shape it has (#336). The switcher
    /// collapsed six rail destinations to four by making "the thing you have open" *one* destination:
    /// <see cref="ShellSection.Task"/> and <see cref="ShellSection.Chat"/> are no longer two places a
    /// user navigates between, they are two renderings of one place, chosen by whether the selected
    /// record is a session or a workflow. Both enum members survive because the two panes are still
    /// genuinely different views; what went away is the user having to know which one they wanted.
    /// </summary>
    public bool IsDetailVisible => IsRoomVisible || IsChatVisible;

    /// <summary>
    /// Whether the single <c>▤ Rooms</c> rail button (#1071) reads as active. The rail collapsed Home
    /// and "the record you have open" into one front-door button, so it is selected whenever the
    /// content pane is showing either the first-run/empty surface (<see cref="IsHomeVisible"/>) or an
    /// open room (<see cref="IsDetailVisible"/>) — the two states that live behind the one glyph.
    /// </summary>
    public bool IsRoomsFrontDoorSelected => IsHomeVisible || IsDetailVisible;

    /// <summary>The Enable Remote Access view's state (M21 Phase 3, issue #234) — see <see cref="RemoteViewModel"/>.</summary>
    public RemoteViewModel Remote { get; } = new();

    /// <summary>An open chat/codebase session's state (M24 Phase 1 desktop wiring, issue #262) — see <see cref="ChatViewModel"/>.</summary>
    public ChatViewModel Chat { get; } = new();

    /// <summary>The fleet management view's state (M24 Phase 5, #278) — see <see cref="RoomsViewModel"/>.</summary>
    public RoomsViewModel Rooms { get; } = new();

    /// <summary>
    /// The template editor's state (M16 Phase 1, issue #150) — the authoring surface, deliberately
    /// its own child ViewModel rather than more fields here: authoring is a separate concern from
    /// the mutation/decision surface this type was introduced for, and it is the first surface
    /// whose fields are two-way bound (see <see cref="TemplateEditorViewModel"/>'s own remarks).
    /// </summary>
    public TemplateEditorViewModel TemplateEditor { get; } = new();

    /// <summary>
    /// The worker-bindings editor's state (M16 Phase 4, issue #153) — the second authoring surface,
    /// alongside <see cref="TemplateEditor"/>, riding the same MVVM shape for the same reason: it is
    /// two-way bound. Bindings are a separate concern from template editing (UI spec §4, §9) — never
    /// persisted in a room directory, never touching a bound snapshot.
    /// </summary>
    public BindingsEditorViewModel BindingsEditor { get; } = new();

    /// <summary>The guided New Workflow flow (M19 Phase 4, #189) — the Author view's primary surface; the file editors above are its advanced disclosure.</summary>
    public NewWorkflowViewModel NewWorkflow { get; } = new();

    /// <summary>
    /// One entry per currently-running or cancellation-pending execution (M15 Phase 4, issue #140) —
    /// the targeted-Cancel surface, alongside <see cref="PausedSteps"/>' decision surface.
    /// </summary>
    public ObservableCollection<RunningExecutionViewModel> RunningExecutions { get; } = [];

    /// <summary>
    /// Owner feedback: the "Working right now" section rendered its heading even with nothing
    /// running underneath, reading as a blank/broken panel rather than an honest empty state.
    /// "Is anything running?" is one of #495's three room questions, answered once (#615): the
    /// change-notification wiring lives on <see cref="RunningExecutions"/> itself because the room
    /// (<c>RoomClient.RebuildRunningExecutions</c>) clears and refills that collection in place on
    /// this same long-lived instance — the collection the room fills IS the stated answer, and this
    /// property only maps it to a bool, so no surface re-counts it (MainWindow's code-behind
    /// previously did, a second copy of the same answer).
    /// </summary>
    public bool HasRunningExecutions => RunningExecutions.Count > 0;

    public MainWindowViewModel()
    {
        RunningExecutions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRunningExecutions));
    }

    /// <summary>
    /// True for the duration of any mutation call this UI process itself is driving — a Run or a
    /// decision — the window's own pump holding the room's §15 lock for that call's entire duration.
    /// Every <see cref="PausedSteps"/> entry's <see cref="PausedStepViewModel.IsEnabled"/> mirrors
    /// this, so a second mutation can never be started from this same process while one is already in
    /// flight (a competing *external* process's lock hold instead surfaces as a
    /// <see cref="Aer.Flow.Concurrency.WorkflowLockedException"/> in-window message, per Phase 1's
    /// precedent — this flag does not, and cannot, prevent that one). <see cref="RunningExecutions"/>
    /// entries are the one deliberate exception: a locally-hosted execution's Cancel stays enabled
    /// exactly while this flag is true (Phase 4) — see <see cref="RunningExecutionViewModel.UpdateEnabled"/>.
    /// </summary>
    [ObservableProperty]
    private bool isMutationInFlight;

    /// <summary>
    /// Owner feedback: "does Run make sense on a finished room? or is it a re-run?" — it's a re-run,
    /// but not in place: <c>MutationInterface.StartWorkflowAsync</c>'s pump finds nothing ready and
    /// nothing in flight for an already-<see cref="Aer.Flow.Domain.WorkflowStatus.Terminal"/> room's
    /// own directory and returns the same state unchanged, a safe but silent no-op, so resuming the
    /// same directory was never an option. <c>MainWindow.OnRoomRunRequestedAsync</c> checks this flag
    /// and, when true, clones the open room's recorded workflow/bindings files into a fresh sibling
    /// <c>room-{timestamp}</c> directory (the same naming the "Save &amp; Run" and template flows
    /// already use) instead of resuming in place — the finished room's own directory and history are
    /// left untouched. Set by <c>MainWindow.RenderProjection</c> from the loaded projection's
    /// <c>State.Status</c>, alongside every other read-only render there.
    /// <para>
    /// #1215 retired the Run button whose <c>IsEnabled</c> and tooltip used to live here as
    /// <c>CanRun</c>/<c>RunButtonToolTipText</c>. What they said is now said by the thing a person
    /// actually reads: <see cref="RoomStoppedCardViewModel"/>'s own headline, body and
    /// <see cref="RoomStoppedCardViewModel.IsEnabled"/>. A tooltip naming a button that no longer
    /// exists is a claim about code that no longer exists.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool isRoomFinished;

    /// <summary>
    /// The open room's workflow definition file, as recorded in its own <c>.aer/workflow-path</c>
    /// (or its <c>workflow.json</c> fallback). Null or empty on a room that has no recorded workflow
    /// file — the "fresh start only" case the retired header box used to say in a placeholder.
    /// <para>
    /// #1215 lifted this and <see cref="BindingsFilePath"/> out of two <c>TextBox</c>es in the room
    /// header, which is the whole point of that slice: a file path is engine plumbing, and the room
    /// header is the room's. They are state, not chrome, which is why the lift is a property here and
    /// not a XAML deletion — <c>RoomClient</c> asks for the bindings path at decide-time ("ask, don't
    /// infer", M14 Phase 2's decision of record), and both are what a Run or a Resume dispatches.
    /// Nullable to preserve exactly what the boxes' own <c>Text</c> carried: the session's
    /// last-path loaders may return null, and callers coalesce at the point of use rather than here.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string? workflowTemplateFilePath;

    /// <summary>Who runs each step, for the open room — see <see cref="WorkflowTemplateFilePath"/> for why both live here.</summary>
    [ObservableProperty]
    private string? bindingsFilePath;

    /// <summary>In-window message surface for a Run's progress ("Running…") or failure — moved here from a directly-set TextBlock when the orchestration moved to <see cref="RoomClient"/> (M19 Phase 2, #187).</summary>
    [ObservableProperty]
    private string runStatusText = string.Empty;

    /// <summary>In-window message surface for a decision's outcome or failure — the same precedent <see cref="RunStatusText"/> established (Phase 1).</summary>
    [ObservableProperty]
    private string decisionStatusText = string.Empty;

    /// <summary>In-window message surface for a targeted Cancel's outcome or failure (Phase 4) — the same precedent as <see cref="DecisionStatusText"/>.</summary>
    [ObservableProperty]
    private string cancelStatusText = string.Empty;

    /// <summary>Issue #618: non-null while the waiting-on-lock state applies — see <see cref="WaitingOnLockBannerViewModel"/> for what it says and when.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaitingOnLockBanner))]
    private WaitingOnLockBannerViewModel? waitingOnLockBanner;

    public bool HasWaitingOnLockBanner => WaitingOnLockBanner is not null;

    /// <summary>
    /// #1215: non-null while the open room is stopped and its transcript therefore carries an offer —
    /// see <see cref="RoomStoppedCardViewModel"/> for which, and <see cref="RoomStoppedReason"/> for
    /// why "stopped" is not the same question as "not running". Set by <see cref="RoomClient"/> on
    /// every load and every applied push, the same places the sibling banners above are set.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomStoppedCard))]
    private RoomStoppedCardViewModel? roomStoppedCard;

    public bool HasRoomStoppedCard => RoomStoppedCard is not null;

    /// <summary>Issue #994: non-null while turn-host status applies for the open room.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomTurnHostBanner))]
    private RoomTurnHostBannerViewModel? roomTurnHostBanner;

    public bool HasRoomTurnHostBanner => RoomTurnHostBanner is not null;

    /// <summary>The open room's steps as the drill-in surface (M19 Phase 3, #188) — rebuilt wholesale on every load/refresh by <see cref="RebuildRoomSteps"/>.</summary>
    public ObservableCollection<StepItemViewModel> RoomSteps { get; } = [];

    /// <summary>The step whose drill-in is open. Re-anchored by step id across rebuilds; defaults needs-you-first (paused, else running, else the first step).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStep))]
    private StepItemViewModel? selectedStep;

    public bool HasSelectedStep => SelectedStep is not null;

    /// <summary>The room-level plain-language headline (the vocabulary map's primary text) — the precise <c>Workflow status:</c> line lives in the Details disclosure.</summary>
    [ObservableProperty]
    private string roomHeadlineText = "No room open.";

    partial void OnSelectedStepChanged(StepItemViewModel? value)
    {
        foreach (var step in RoomSteps)
        {
            step.IsSelected = ReferenceEquals(step, value);
        }
    }

    /// <summary>
    /// Raised when something in the room asks for it to be run — a failed step's "Try again" (#617),
    /// or the stopped-room card's Resume / Run it again (#1215). One event rather than one per
    /// caller: they all mean the same thing to the shell, which decides resume-in-place versus
    /// clone-to-a-fresh-room from the room's own state, not from who asked.
    /// </summary>
    public event Func<Task>? RoomRunRequested;

    /// <summary>Invokes <see cref="RoomRunRequested"/>, or completes immediately if nothing is listening.</summary>
    internal Task RequestRoomRunAsync() => RoomRunRequested?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// Raised when the header's Workflow switch is thrown (#1216). The bool is the state asked for,
    /// not a toggle instruction, so a stale render cannot invert what the person meant.
    /// </summary>
    public event Func<bool, Task<string?>>? WorkflowSwitchRequested;

    /// <summary>
    /// Why the last switch attempt did not take — the engine's own reason
    /// (<c>RoomMutationInterface.SetWorkflowSwitchAsync</c>), shown beside the switch rather than
    /// swallowed. A refusal is an answer, and the person needs to know they must stop the room first;
    /// silently springing the switch back is the "quiet failure" 0026 §5 rules out.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkflowSwitchStatusText))]
    private string? workflowSwitchStatusText;

    public bool HasWorkflowSwitchStatusText => !string.IsNullOrEmpty(WorkflowSwitchStatusText);

    /// <summary>
    /// Asks for the room's workflow to be switched to <paramref name="isOn"/>. Never sets
    /// <c>Chat.IsWorkflowOn</c> itself: the switch renders the journal, so the value comes back
    /// through the projection and a refused switch simply never changes (0020 rule 1).
    /// </summary>
    /// <summary>
    /// What the header switch is bound to. Asks for the opposite of what the room currently says
    /// rather than for "the opposite of the control", because the control is one-way and has already
    /// flipped its own visual by the time this runs — reading it back would invert the request.
    /// </summary>
    [RelayCommand]
    private Task ToggleWorkflowSwitch() => RequestWorkflowSwitchAsync(!Chat.IsWorkflowOn);

    public async Task RequestWorkflowSwitchAsync(bool isOn)
    {
        WorkflowSwitchStatusText = null;
        if (WorkflowSwitchRequested is null)
        {
            return;
        }

        WorkflowSwitchStatusText = await WorkflowSwitchRequested.Invoke(isOn).ConfigureAwait(true);
    }

    /// <summary>
    /// Routes "Ask <worker> to fix it" (#617) to the Chat section with the input drafted — a
    /// message naming the step and quoting the reason text. When no session is open, the new-chat
    /// bar is pre-filled too — the failing step's adapter and the failed room's own directory — so
    /// starting the conversation is one click into the right room with the draft already waiting;
    /// found live, where the draft alone landed invisibly behind "No room open." while the tests
    /// (which read the property, not the screen) stayed green. When a session is already open the
    /// draft lands in that session's input instead, and the adapter selection below is inert
    /// there — <see cref="ChatViewModel.NewChatAdapter"/> is consulted only by the start-new-chat
    /// flow, never by an open session's send path, which keeps speaking to whatever vendor the
    /// session was started with. The open session is always this room's own: the window's
    /// navigation resyncs Chat to whichever directory populates <see cref="RoomSteps"/>
    /// (<c>MainWindow.OpenAsync</c>), and the banner test pins that invariant.
    /// </summary>
    public void AskWorkerToFix(string adapter, string stepId, string reason, string roomDirectoryPath)
    {
        // The banner always supplies the step's own adapter; no invented fallback vendor here — if
        // that adapter is not available on this host, the picker's first available stands in and
        // the user sees which one they are addressing before sending.
        var targetAdapter = adapter.ToLowerInvariant();
        if (Chat.AvailableAdapters.Contains(targetAdapter))
        {
            Chat.NewChatAdapter = targetAdapter;
        }
        else if (Chat.AvailableAdapters.Count > 0)
        {
            Chat.NewChatAdapter = Chat.AvailableAdapters[0];
        }

        if (!Chat.IsSessionOpen)
        {
            Chat.NewChatWorkingDirectory = roomDirectoryPath;
            Chat.RefreshNewChatAdapterSelection();
        }

        // Appended, never assigned over: the input box can already hold a half-typed message in an
        // open session, and an affordance click must not destroy the user's own words.
        var draft = $"Step '{stepId}' failed: {reason}";
        Chat.InputText = string.IsNullOrWhiteSpace(Chat.InputText)
            ? draft
            : $"{Chat.InputText.TrimEnd()}\n{draft}";
        CurrentSection = ShellSection.Chat;
    }

    /// <summary>
    /// Rebuilds <see cref="RoomSteps"/> from a fresh projection (M19 Phase 3, #188). The preview
    /// and conversation delegates are the skin's render targets — the same inversion
    /// <see cref="RoomClient"/> uses, keeping this assembly Avalonia-free.
    /// </summary>
    /// <param name="isFlowLockHeld">
    /// #1219: passed, not probed here, so this and <c>RenderProjection</c>'s own fingerprint answer
    /// from one reading of the room's §15 lock — and required rather than defaulted, for the reason
    /// on <see cref="PlainLanguage.ForWorkflow"/>.
    /// </param>
    public void RebuildRoomSteps(
        RoomProjection projection,
        string roomDirectoryPath,
        bool isFlowLockHeld,
        Func<string, Task> previewFileAsync,
        Action<string, string> showConversation,
        IReadOnlyDictionary<string, string>? workerAdapters = null)
    {
        var previousSelectedStepId = SelectedStep?.StepId;

        RoomSteps.Clear();
        // reRunAction only for a Terminal room: Run's re-run-as-clone flow (see IsRoomFinished)
        // exists only then. While a sibling branch still runs or waits on a decision, the same
        // click resumes the directory in place — for a Failed step with no pending obligation the
        // pump returns unchanged, a silent no-op — so the banner hides Try again until the room
        // finishes (FailedStepBannerViewModel.CanTryAgain). Gated on the projection parameter, not
        // the IsRoomFinished property, so it cannot depend on the skin's render order.
        var reRunAvailable = projection.State.Status == Aer.Flow.Domain.WorkflowStatus.Terminal;
        foreach (var item in StepItemProjector.Build(
            projection, roomDirectoryPath, PausedSteps, previewFileAsync, showConversation,
            select: item => SelectedStep = item,
            workerAdapters: workerAdapters,
            reRunAction: reRunAvailable ? () => _ = RequestRoomRunAsync() : null,
            askWorkerToFixAction: (adapter, stepId, reason) => AskWorkerToFix(adapter, stepId, reason, roomDirectoryPath)))
        {
            RoomSteps.Add(item);
        }

        // #1219: the Task view's headline reads the same lock every other surface does. It is still
        // live until slice 5 retires the section, and without this a room whose process died kept
        // saying "Working — …" here while the switcher and the transcript both said Stopped.
        RoomHeadlineText = PlainLanguage.ForWorkflow(projection, isFlowLockHeld);
        SelectedStep =
            RoomSteps.FirstOrDefault(step => step.StepId == previousSelectedStepId) ??
            RoomSteps.FirstOrDefault(step => step.IsPaused) ??
            RoomSteps.FirstOrDefault(step => step.Status == Aer.Flow.Domain.StepStatus.Running) ??
            RoomSteps.FirstOrDefault();
    }

    /// <summary>Clears the drill-in surface — the error-path counterpart of <see cref="RebuildRoomSteps"/>.</summary>
    public void ClearRoomSteps()
    {
        RoomSteps.Clear();
        SelectedStep = null;
        RoomHeadlineText = "No room open.";
    }

    /// <summary>Selects a step by id — the DAG canvas's node-click entry point (the canvas stays code-behind until Phase 5 makes it a custom control).</summary>
    public void SelectStepById(string stepId)
        => SelectedStep = RoomSteps.FirstOrDefault(step => step.StepId == stepId) ?? SelectedStep;

    partial void OnCurrentSectionChanged(ShellSection value) => SectionChanged?.Invoke(value);

    /// <summary>Raised on navigation so the shell can refresh the newly-activated section (Home rebuilds its cards/inbox on activation — its decision of record).</summary>
    public event Action<ShellSection>? SectionChanged;

    partial void OnIsMutationInFlightChanged(bool value)
    {
        foreach (var step in PausedSteps)
        {
            step.IsEnabled = !value;
        }

        // The inline permission gate answers over the same room lock a step decision does, so it
        // disables on the same signal (0022's ladder is a mutation surface like PausedSteps).
        if (Chat.PendingPermission is { } gate)
        {
            gate.IsEnabled = !value;
        }

        // Not redundant with the PausedSteps loop above, and not an edge case: RoomClient's
        // RebuildPausedSteps clears and re-constructs that collection on every load, while the
        // transcript's cards deliberately keep the instance whose (step, execution) key is still
        // open. So from the second poll a decision stays open across, these are different objects
        // by design, and the loop above reaches none of the cards. Missing them would leave a card
        // live during a mutation this process is already driving.
        foreach (var decisionCard in Chat.PendingDecisions)
        {
            decisionCard.IsEnabled = !value;
        }

        foreach (var execution in RunningExecutions)
        {
            execution.UpdateEnabled(value);
        }
    }
}
