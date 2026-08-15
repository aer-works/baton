using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Artifacts;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Templates;
using Aer.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Win32;



[assembly: InternalsVisibleTo("Aer.Ui.Tests")]

namespace Aer.Ui;

public partial class MainWindow : Window
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;
    private const int MaxArtifactPreviewLength = 8000;

    /// <summary>
    /// #868: monotonic request counter guarding <see cref="ArtifactPreviewBox"/> against two in-flight
    /// <see cref="ShowArtifactPreviewAsync"/> reads racing. Every request (including a bare clear, via
    /// <see cref="ClearArtifactPreview"/>) takes the next value; a completed read — success or error —
    /// only writes the box if its own value is still the latest, so a slower, older read that finishes
    /// after a newer one can never clobber it. A per-request <see cref="CancellationTokenSource"/>
    /// would also work, but would cancel (and typically surface as an <see cref="OperationCanceledException"/>
    /// from) the superseded file read, when the actual requirement is narrower: let it finish, just
    /// don't let it write.
    /// </summary>
    private int _artifactPreviewGeneration;

    /// <summary>
    /// How a preview reads a file. Production is <see cref="File.ReadAllTextAsync(string, CancellationToken)"/>;
    /// a test replaces it with a reader it can hold open, which is the only way to make the #868 race
    /// deterministic rather than a bet on one file being slower than another. The first regression
    /// test for that race forced the window with a 150MB fixture, which reproduces nothing reliably:
    /// too fast a disk and the older read finishes before the newer request is even issued, too slow
    /// and it never finishes inside the observation window -- both directions pass while exercising
    /// nothing. Same seam convention as <c>HeldWorkReconciler</c>'s journal probe.
    /// </summary>
    internal Func<string, CancellationToken, Task<string>> ReadArtifactTextAsync { get; set; } =
        (filePath, cancellationToken) => File.ReadAllTextAsync(filePath, cancellationToken);

    /// <summary>Clears the box and supersedes any in-flight <see cref="ShowArtifactPreviewAsync"/> read, so one that completes afterward cannot silently repopulate what was just cleared.</summary>
    private void ClearArtifactPreview()
    {
        Interlocked.Increment(ref _artifactPreviewGeneration);
        ArtifactPreviewBox.Text = string.Empty;
    }

    /// <summary>
    /// This window's presentation-agnostic half (M19 Phase 2, issue #187): projection loading, pump
    /// hosting, and every mutation-interface call live on the session in <c>Aer.Ui.Core</c> — this
    /// code-behind renders the session's outcomes and raises its intents, nothing more. The
    /// constructor delegates wire the presentation half back in: the bindings box as the
    /// ask-don't-infer path source, the 2-second poller as the mutation-progress renderer, and
    /// <see cref="OpenAsync"/> as the settle-time re-open.
    /// </summary>
    private readonly RoomClient _session;
    internal RoomClient Session => _session;

    /// <summary>Retained for the Settings → Appearance theme (#1068): read at startup to sync the toggle's selected state, written when a choice is made. The store is otherwise the RoomClient's (recents, bindings).</summary>
    private readonly LocalUiConfigurationStore _configurationStore;

    private readonly DispatcherTimer _liveRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// Drives <see cref="RemoteViewModel.TickPairingCodeCountdown"/> once a second while the Remote
    /// section is open — the label was previously set once from the fetch response and never
    /// updated, so "Expires in 60s" stayed frozen even long after the daemon's own 60s expiry
    /// (<c>PairingCodeManager.ValidateAndConsume</c>) had made the code genuinely dead. Auto-fetches
    /// a fresh code on reaching 0 rather than leaving a visibly-expired one on screen.
    /// </summary>
    private readonly DispatcherTimer _pairingCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// Which execution's conversation is currently shown (M18 Phase 2, issue #178) — local UI
    /// selection state like <see cref="RoomDirectoryPathBox"/>'s text (UI spec §4), never a
    /// projected fact: every <see cref="LoadAsync"/> re-renders the conversation from the durable
    /// transcript this directory holds *now*, which is how load-on-refresh follows a still-running
    /// exchange without any push/streaming channel.
    /// </summary>
    private string? _conversationOutputDirectory;
    private string? _conversationLabel;

    /// <summary>Set once <see cref="OnClosing"/> has already stopped an in-flight pump and is closing the window for real — prevents re-entering the stop sequence from the follow-up programmatic <see cref="Window.Close()"/>.</summary>
    private bool _closeConfirmed;

    /// <summary>Test-only observation of the live-refresh polling state (see <see cref="UpdateLiveRefreshTimer"/>) — never consulted by production code.</summary>
    internal bool IsLiveRefreshTimerEnabled => _liveRefreshTimer.IsEnabled;

    /// <summary>
    /// Whether the open room's <em>flow</em> can still change — §12's fixed point, which is what
    /// "the pump is done" means. Distinct from <see cref="IsLiveRefreshTimerEnabled"/> since #1216:
    /// the poller keeps watching a settled room for room-level facts, so the timer being on no longer
    /// answers this question and tests that mean this must ask it directly.
    /// </summary>
    internal bool IsRoomFlowStillChanging => _session.ShouldLiveRefresh;

    /// <summary>
    /// How many times <see cref="RenderProjection"/> has actually re-projected. Exists so a test can
    /// assert a settled room's tick does NOT pay for a re-projection when its journal has not moved —
    /// the cheapness <see cref="UpdateLiveRefreshTimer"/>'s remarks turn on, which no observable
    /// state otherwise distinguishes from a tick that reloaded and found nothing different.
    /// </summary>
    internal int RenderedProjectionCountForTests { get; private set; }

    /// <summary>
    /// This window's ViewModel (M15 Phase 2, issue #138) — set as <see cref="Window.DataContext"/> so
    /// <see cref="MainWindow.axaml"/> can bind the paused-step decision surface and the shared
    /// mutation-in-flight flag directly. See <see cref="MainWindowViewModel"/>'s own remarks for why
    /// this is scoped to that surface only, not the rest of the window's rendering.
    /// </summary>
    internal MainWindowViewModel ViewModel { get; } = new();

    // ── The re-home facade (M19 Phase 2, #187) ─────────────────────────────────────────────────
    // Every pre-shell control, reachable under its original name: the shell moved the controls
    // into Home/Task/Author views (their new homes per docs/archive/ux/information-architecture.md), and
    // these internal properties are how this window's rendering code and the headless round trips
    // keep addressing them — one migration per surface, no behavioral change. Phases 3–4 retire
    // entries as they rebuild each surface properly.
    internal TextBox RoomDirectoryPathBox => HomeViewControl.RoomDirectoryPathBox;
    internal Button OpenButton => HomeViewControl.OpenButton;
    internal Button RefreshButton => HomeViewControl.RefreshButton;

    // #1224: on ChatHeaderView now (see its own summary for where that sits and why). Same button,
    // same wiring.
    internal Button StopButton => ChatHeaderControl.StopButton;
    internal TextBlock RunStatusText => RoomViewControl.RunStatusText;
    internal TextBlock StatusText => RoomViewControl.StatusText;
    internal StackPanel StepsPanel => RoomViewControl.StepsPanel;
    internal TextBlock CancelStatusText => RoomViewControl.CancelStatusText;
    // #1196 slice 3: follows the decision cards into the transcript. The status of answering a
    // decision belongs beside the decision, not in the panel that no longer offers it.
    internal TextBlock DecisionStatusText => ChatViewControl.DecisionStatusText;

    /// <summary>The width the room's shape takes beside a transcript. Wide enough for the DAG's own minimum plus its drill-in.</summary>
    private const double ShapePanelWidth = 460;

    /// <summary>
    /// Whether the shape sits beside the transcript (#1196 slice 3), and how wide it is. Two states
    /// since #1222 retired <c>ShellSection.Task</c>: a workflow room whose shape is toggled on shows
    /// both side by side, and anything else shows no shape at all, its column taking no space. The
    /// third state — the shape alone, full width — is gone with the section that was its only route,
    /// which is what "a room has one rendering" means concretely.
    /// <para>
    /// Imperative rather than bound because <c>GridLength</c> is an Avalonia type and
    /// <see cref="MainWindowViewModel"/> is deliberately Avalonia-free.
    /// </para>
    /// </summary>
    private void ApplyShellLayout()
    {
        var shapeBeside = ViewModel.IsChatVisible && ViewModel.Chat.IsShapeToggleVisible && ViewModel.Chat.IsShapePanelOpen;

        MainRegion.IsVisible = true;
        ShapeRegion.IsVisible = shapeBeside;
        ShapeRegion.Width = ShapePanelWidth;

        // Auto, not a zero pixel width, for the shape column when it is hiding its content: an Auto
        // column sizes to an invisible child as zero, and a zero GridLength does not survive on this
        // grid.
        ShellGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ShellGrid.ColumnDefinitions[1].Width = GridLength.Auto;
    }
    /// <summary>What <see cref="ApplyShellLayout"/> last decided, for the test that pins its three states.</summary>
    internal bool IsMainRegionVisible => MainRegion.IsVisible;
    internal bool IsShapeRegionVisible => ShapeRegion.IsVisible;
    internal GridLength MainColumnWidth => ShellGrid.ColumnDefinitions[0].Width;
    internal GridLength ShapeColumnWidth => ShellGrid.ColumnDefinitions[1].Width;

    internal Canvas DagCanvas => RoomViewControl.DagCanvas;
    internal StackPanel HistoryPanel => RoomViewControl.HistoryPanel;
    internal StackPanel ConversationExecutionsPanel => RoomViewControl.ConversationExecutionsPanel;
    internal StackPanel ConversationPanel => RoomViewControl.ConversationPanel;
    internal StackPanel DecisionsPanel => RoomViewControl.DecisionsPanel;
    internal StackPanel SupplementaryPanel => RoomViewControl.SupplementaryPanel;
    internal StackPanel LineagePanel => RoomViewControl.LineagePanel;
    internal TextBox ArtifactPreviewBox => RoomViewControl.ArtifactPreviewBox;
    internal TabControl StepDetailTabControl => RoomViewControl.StepDetailTabControl;
    internal TextBox TemplateComparePathBox => RoomViewControl.TemplateComparePathBox;
    internal Button CompareButton => RoomViewControl.CompareButton;
    internal StackPanel DiffPanel => RoomViewControl.DiffPanel;

    internal TextBox TemplateEditorPathBox => AuthorViewControl.TemplateEditorPathBox;
    internal Button NewTemplateButton => AuthorViewControl.NewTemplateButton;
    internal Button EditTemplateButton => AuthorViewControl.EditTemplateButton;
    internal Button SaveTemplateButton => AuthorViewControl.SaveTemplateButton;
    internal Button AddStepButton => AuthorViewControl.AddStepButton;
    internal Canvas TemplateEditorDagCanvas => AuthorViewControl.TemplateEditorDagCanvas;
    internal TextBox BindingsEditorPathBox => AuthorViewControl.BindingsEditorPathBox;
    internal Button NewBindingsButton => AuthorViewControl.NewBindingsButton;
    internal Button EditBindingsButton => AuthorViewControl.EditBindingsButton;
    internal Button SaveBindingsButton => AuthorViewControl.SaveBindingsButton;
    internal Button AddBindingEntryButton => AuthorViewControl.AddBindingEntryButton;
    internal Button CheckBindingsAgainstTemplateButton => AuthorViewControl.CheckBindingsAgainstTemplateButton;

    // #1068: Remote folded into Settings, so its pairing controls now live one level down, on the
    // RemoteView embedded in SettingsView's "Your phone" group.
    internal Button RemoteToggleButton => SettingsViewControl.RemoteViewControl.RemoteToggleButton;
    internal Button RemoteRefreshCodeButton => SettingsViewControl.RemoteViewControl.RemoteRefreshCodeButton;
    internal Button RemoteOpenSidecarAuthButton => SettingsViewControl.RemoteViewControl.RemoteOpenSidecarAuthButton;
    internal Button RemoteForgetSidecarButton => SettingsViewControl.RemoteViewControl.RemoteForgetSidecarButton;
    internal Button SaveTailscaleAuthKeyButton => SettingsViewControl.RemoteViewControl.SaveTailscaleAuthKeyButton;
    internal Button ThemeLightButton => SettingsViewControl.ThemeLightButton;
    internal Button ThemeDarkButton => SettingsViewControl.ThemeDarkButton;
    internal Button ThemeSystemButton => SettingsViewControl.ThemeSystemButton;

    internal TextBox ChatInputBox => ChatViewControl.ChatInputBox;
    internal Button ChatSendButton => ChatViewControl.ChatSendButton;
    internal Button ChatCommandsButton => ChatViewControl.ChatCommandsButton;
    internal Button ChatModeAutoButton => ChatViewControl.ChatModeAutoButton;
    internal Button ChatModeDefaultButton => ChatViewControl.ChatModeDefaultButton;
    internal Button ChatModePlanButton => ChatViewControl.ChatModePlanButton;
    internal ComboBox ChatNewAdapterCombo => ChatViewControl.ChatNewAdapterCombo;
    internal TextBox ChatNewWorkingDirectoryBox => ChatViewControl.ChatNewWorkingDirectoryBox;
    internal Button ChatStartNewButton => ChatViewControl.ChatStartNewButton;

    /// <summary>
    /// The re-homed counterpart of <c>Window.FindControl</c> for the headless round trips: controls
    /// now live in the views' name scopes, so the window's own scope no longer resolves them — this
    /// searches Home, Task, Author, then Chat, preserving every test's by-name lookup unchanged.
    /// </summary>
    internal T? FindViewControl<T>(string name) where T : Control
        => HomeViewControl.FindControl<T>(name)
           ?? RoomViewControl.FindControl<T>(name)
           ?? AuthorViewControl.FindControl<T>(name)
           ?? ChatViewControl.FindControl<T>(name);

    private static readonly bool IsUnderTest = AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.FullName != null && (a.FullName.Contains("xunit") || a.FullName.Contains("Test") || a.FullName.Contains("test")));

    public MainWindow()
        : this(LocalUiConfigurationStore.CreateDefault(), WorkerAdapterRegistry.Default, IsUnderTest ? null : "http://localhost:5000")
    {
    }

    /// <summary>
    /// Takes the recents store as a constructor argument, never constructing
    /// <see cref="LocalUiConfigurationStore.CreateDefault"/> unconditionally, so a test can point it
    /// at a temp file instead of the real per-user config directory — the same "production wiring
    /// is the caller's decision" seam <c>Aer.Cli</c>'s <c>RunCommand</c> established for the adapter
    /// registry (M11 Phase 3).
    /// </summary>
    public MainWindow(LocalUiConfigurationStore configurationStore)
        : this(configurationStore, WorkerAdapterRegistry.Default, IsUnderTest ? null : "http://localhost:5000")
    {
    }

    /// <summary>
    /// Takes the worker-adapter registry as a constructor argument too (M15 Phase 1, issue #137) —
    /// the same production-wiring-is-the-caller's-decision seam as <paramref name="configurationStore"/>
    /// and, before this window existed, <c>RunCommand.ExecuteAsync</c>'s own adapter-registry
    /// parameter (M11 Phase 3). Defaults to <see cref="WorkerAdapterRegistry.Default"/> via the
    /// other two constructors so production callers never have to name it explicitly, while
    /// <c>Aer.Ui.Tests</c> can substitute a deterministic shell-stub registry instead of resolving a
    /// live vendor CLI.
    /// </summary>
    public MainWindow(LocalUiConfigurationStore configurationStore, IReadOnlyDictionary<string, IWorkerAdapter> adapters, string? daemonUrl = null)
    {
        InitializeComponent();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Win32Properties.AddWndProcHookCallback(this, WndProcHook);
        }
        DataContext = ViewModel;
        _configurationStore = configurationStore;
        _session = new RoomClient(
            configurationStore,
            adapters,
            ViewModel,
            bindingsFilePathProvider: () => ViewModel.BindingsFilePath,
            mutationStarted: _liveRefreshTimer.Start,
            mutationFailed: _liveRefreshTimer.Stop,
            reopenRoomAsync: (roomDirectoryPath, cancellationToken) => OpenAsync(roomDirectoryPath, cancellationToken),
            onProjectionUpdated: (projection, roomDirectoryPath) => RenderProjection(projection, roomDirectoryPath),
            daemonUrl: daemonUrl);

        // M16 Phase 4 (issue #153): adapter names are offered from the registry this window was
        // constructed with — reflect, don't invent — carried per-row on WorkerBindingEntryViewModel
        // rather than bound from a shared ancestor, since ItemsControl.ItemTemplate's DataContext is
        // the entry itself.
        ViewModel.BindingsEditor.SetAdapterRegistry(adapters);
        ViewModel.NewWorkflow.SetAdapterRegistry(adapters);

        // #290: populated once here (PATH doesn't change mid-session in practice), same source
        // TemplatePickerWindow's own vendor combo uses, so the Chat page's direct "start new chat"
        // entry point never disagrees with the template picker about what's offered.
        ViewModel.Chat.PopulateAvailableAdapters();

        _liveRefreshTimer.Tick += (_, _) => _ = OnLiveRefreshTickAsync();
        OpenButton.Click += (_, _) => _ = OpenAsync(RoomDirectoryPathBox.Text ?? string.Empty);
        RefreshButton.Click += (_, _) => _ = RefreshAsync();
        CompareButton.Click += (_, _) => _ = CompareToTemplateAsync(TemplateComparePathBox.Text ?? string.Empty);
        ViewModel.RoomRunRequested += OnRoomRunRequestedAsync;
        ViewModel.WorkflowSwitchRequested += OnWorkflowSwitchRequestedAsync;
        StopButton.Click += (_, _) => _ = StopAsync();
        NewTemplateButton.Click += (_, _) => NewTemplate();
        EditTemplateButton.Click += (_, _) => _ = OpenTemplateInEditorAsync(TemplateEditorPathBox.Text ?? string.Empty);
        SaveTemplateButton.Click += (_, _) => _ = SaveTemplateAsync(TemplateEditorPathBox.Text ?? string.Empty);
        AddStepButton.Click += (_, _) => ViewModel.TemplateEditor.AddStep();
        ViewModel.TemplateEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TemplateEditorViewModel.PreviewLayout))
            {
                RenderTemplateEditorDag();
            }
        };
        NewBindingsButton.Click += (_, _) => NewBindings();
        EditBindingsButton.Click += (_, _) => _ = OpenBindingsInEditorAsync(BindingsEditorPathBox.Text ?? string.Empty);
        SaveBindingsButton.Click += (_, _) => _ = SaveBindingsAsync(BindingsEditorPathBox.Text ?? string.Empty);
        AddBindingEntryButton.Click += (_, _) =>
        {
            ViewModel.BindingsEditor.AddEntry();
            RefreshBindingsTemplateCrossCheck();
        };
        CheckBindingsAgainstTemplateButton.Click += (_, _) => RefreshBindingsTemplateCrossCheck();
        // #1071: one ▤ Rooms button covers the front door. With a room open it returns to that room;
        // with nothing open it lands on the first-run/empty surface (Home). #336 folded Chat into
        // "the record you have open"; this folds Home in too, so the rail is the three glyphs
        // 02-screens draws. Which pane to return to stopped being a question at #1222 — a room has
        // one rendering — so this no longer remembers one.
        NavRoomsButton.Click += (_, _) => ViewModel.CurrentSection =
            _session.CurrentRoomDirectoryPath is not null ? ShellSection.Chat : ShellSection.Home;
        NavAuthorButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Author;
        NavSettingsButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Settings;
        // #336: Chat and Tasks are no longer rail destinations. Chat is reached by opening a session
        // (the switcher routes to the right pane); the management surface is reached from the foot of
        // the switcher list.
        SwitcherManageButton.Click += (_, _) => ViewModel.CurrentSection = ShellSection.Rooms;
        SwitcherRefreshButton.Click += (_, _) => _ = ViewModel.Rooms.RefreshAsync(_session);
        SwitcherNewButton.Click += (_, _) => _ = StartNewRoomFromTemplateAsync();
        RoomsViewControl.RoomsRefreshButton.Click += (_, _) => _ = ViewModel.Rooms.RefreshAsync(_session);
        RoomsViewControl.RoomsIncludeArchivedCheckBox.IsCheckedChanged += (_, _) => _ = ViewModel.Rooms.RefreshAsync(_session);
        // Bulk select (issue #288): these two need the session the same way the single-row actions'
        // closures do (RoomsViewModel.RefreshAsync wires those per row), but the bulk actions live on
        // RoomsViewModel itself rather than per-row, so they're wired here instead.
        RoomsViewControl.RoomsBulkArchiveButton.Click += (_, _) => _ = ViewModel.Rooms.BulkArchiveAsync(_session);
        RoomsViewControl.RoomsBulkDeleteConfirmButton.Click += (_, _) => _ = ViewModel.Rooms.ConfirmBulkDeleteAsync(_session);
        ChatSendButton.Click += (_, _) => _ = SendChatMessageAsync();
        // Enter-to-send / Shift+Enter-newline — see OnChatInputBoxKeyDown and IsSendKeystroke.
        ChatInputBox.KeyDown += OnChatInputBoxKeyDown;
        // #390 / 0022 §4: y/n answer a pending permission from anywhere in the window (bubbling KeyDown
        // reaches the window from whatever is focused) — never from a focused text field, and never on
        // Enter. Window-wide, not on ChatView, because the gate's caption promises it regardless of
        // which pane holds focus.
        KeyDown += OnPermissionGateKeyDown;
        ChatStartNewButton.Click += (_, _) => _ = StartNewChatAsync();
        ChatCommandsButton.Click += (_, _) => _ = ToggleChatCommandsAsync();
        ChatModeAutoButton.Click += (_, _) => _ = SetChatModeAsync("auto");
        ChatModeDefaultButton.Click += (_, _) => _ = SetChatModeAsync("default");
        ChatModePlanButton.Click += (_, _) => _ = SetChatModeAsync("plan");
        // Per-item command-picker selection (M24 Phase 2 follow-up): the same "sender's DataContext
        // is the bound item" idiom RoomView/AuthorView already use for per-item buttons, wired via
        // event bubbling since ChatCommandsList's buttons come from a DataTemplate, not named XAML.
        ChatViewControl.ChatCommandsList.AddHandler(Button.ClickEvent, OnChatCommandItemClick);
        // M24 Phase 1's live in-turn streaming (issue #262): the daemon broadcasts every session's
        // progress on the same /api/ws/progress socket, so this filters to whichever session
        // directory is actually open in the Chat view right now.
        _session.SessionProgressReceived += (directoryPath, _, progressEvent) =>
        {
            if (ViewModel.Chat.RoomDirectoryPath == directoryPath)
            {
                ViewModel.Chat.AppendProgress(progressEvent);
            }
        };
        // #336: the switcher list is permanently visible, so it no longer gets a section activation to
        // rebuild on. Every projection push updates its row instead — including pushes for sessions
        // this client is not currently viewing, which is exactly the case the detail pane's own
        // filter (RoomClient.ShouldApplyProjectionPush, #262) deliberately drops.
        _session.FleetProjectionReceived += (directoryPath, projection) =>
            ViewModel.Rooms.ApplyProjectionPush(directoryPath, projection);
        // Selecting a row *is* opening the record — the switcher has no separate "open" action. Guarded
        // against re-entry: OpenAsync itself refreshes the fleet list, which re-finds and re-assigns
        // CurrentItem, and without this an open would recurse through its own selection change.
        ViewModel.Rooms.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RoomsViewModel.CurrentItem) || _isOpeningFromSwitcher)
            {
                return;
            }

            if (ViewModel.Rooms.CurrentItem is { } row && row.RoomDirectoryPath != _session.CurrentRoomDirectoryPath)
            {
                _ = OpenFromSwitcherAsync(row.RoomDirectoryPath);
            }
        };
        RemoteToggleButton.Click += (_, _) => _ = ViewModel.Remote.ToggleRemoteAsync(_session);
        RemoteRefreshCodeButton.Click += (_, _) => _ = ViewModel.Remote.GeneratePairingCodeAsync(_session);
        // M21 Phase 5 (#242): the one-time interactive Tailscale sign-in the tsnet sidecar needs on
        // first enrollment. UseShellExecute=true is the standard cross-platform "hand this URL to
        // whatever the OS's default browser is" — Aer.Ui.Core has no process-launching capability
        // of its own (kept Avalonia/OS-free), so this stays here with the rest of the button wiring.
        RemoteOpenSidecarAuthButton.Click += (_, _) =>
        {
            if (ViewModel.Remote.SidecarAuthUrl is { } url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* best-effort */ }
            }
        };
        // The only prior way to disconnect the sidecar's tsnet node was deleting it from the
        // Tailscale admin console and restarting Aer.Ui — found live via direct user feedback.
        RemoteForgetSidecarButton.Click += (_, _) => _ = ViewModel.Remote.ForgetSidecarAsync(_session);
        SaveTailscaleAuthKeyButton.Click += (_, _) => _ = ViewModel.Remote.SaveTailscaleAuthKeyAsync(_session);
        // #1068: Settings → Appearance. Each choice applies to the running app immediately, marks the
        // toggle's selected button, and persists so the next launch opens in the chosen theme.
        ThemeLightButton.Click += (_, _) => _ = ChooseThemeAsync(ThemeNames.Light);
        ThemeDarkButton.Click += (_, _) => _ = ChooseThemeAsync(ThemeNames.Dark);
        ThemeSystemButton.Click += (_, _) => _ = ChooseThemeAsync(ThemeNames.System);
        // #1071: Home is the ▤ front door's first-run/empty surface now. Activating it refreshes the
        // "No rooms yet." empty-state (Home.HasNoRooms) and the vendor-readiness line — 02-screens'
        // first-run screen shows readiness (#478/#285, the same source Settings and Author use, a CLI
        // installed while the app is open should show without a restart). Fire-and-forget like every
        // other event-handler entry point here.
        ViewModel.SectionChanged += section =>
        {
            if (section == ShellSection.Home)
            {
                _ = RefreshHomeAsync(CancellationToken.None);
                ViewModel.NewWorkflow.RefreshVendorReadiness();
            }

            // M19 Phase 4 (#189): the read-only vendor-readiness line refreshes on Author
            // activation — presence can change while the app is open (a CLI just installed).
            if (section == ShellSection.Author)
            {
                ViewModel.NewWorkflow.RefreshVendorReadiness();
            }

            // #1068: Settings folds Workers + Your phone in, so activating it refreshes both the
            // vendor-readiness line (a CLI may have just been installed, same reasoning as Author's
            // refresh above) and — M21 Phase 3 (#234) — remote status + a fresh pairing code, since a
            // code expires in 60s and a re-visit should never show a stale/dead one.
            if (section == ShellSection.Settings)
            {
                ViewModel.NewWorkflow.RefreshVendorReadiness();
                _ = ViewModel.Remote.RefreshAsync(_session);
                _pairingCountdownTimer.Start();
            }
            else
            {
                _pairingCountdownTimer.Stop();
            }

            // M24 Phase 5 (#278): the fleet list rebuilds on every activation, same reasoning as
            // Home's own rebuild-on-activation — archive/unarchive/delete elsewhere (or from
            // another client) shouldn't require leaving and re-entering this view to see reflected.
            if (section == ShellSection.Rooms)
            {
                _ = ViewModel.Rooms.RefreshAsync(_session);
            }
        };
        _pairingCountdownTimer.Tick += (_, _) =>
        {
            ViewModel.Remote.TickPairingCodeCountdown();
            // Regenerate on expiry only while the pairing code is actually shown (ShowPairingBlock) —
            // the same #1068 reasoning as RefreshAsync's mint gate: with remote off (or no host) the
            // code isn't displayed, so refreshing it just churns the daemon's single active code while
            // someone sits on Settings for an unrelated reason.
            if (ViewModel.Remote.ShowPairingBlock && ViewModel.Remote.PairingCodeExpiresInSeconds <= 0)
            {
                _ = ViewModel.Remote.GeneratePairingCodeAsync(_session);
            }

            // M21 Phase 5 (#242): reuses this same 1s tick to poll the tsnet sidecar's status
            // rather than a second timer — stops once Ready (the tailnet IP shouldn't change while
            // the process runs), so this doesn't poll forever after enrollment completes.
            if (ViewModel.Remote.ShouldPollSidecarStatus)
            {
                _ = ViewModel.Remote.RefreshSidecarStatusAsync(_session);
            }

            // Phase 6 (#243) follow-up: a phone pairing while this page is already open used to
            // only show up in "Paired devices" after navigating away and back (RefreshPairedClientsAsync
            // was only ever called from RefreshAsync's own activation/toggle path) — found live. Only
            // polls while remote access is actually on, since a new pairing can't happen otherwise.
            if (ViewModel.Remote.IsRemoteEnabled)
            {
                _ = ViewModel.Remote.RefreshPairedClientsAsync(_session);
            }
        };
        // M19 Phase 4 (#189): Save & Run without leaving the flow — each run gets a fresh room
        // directory beside the authored files (one workspace per workflow, rooms inside it), then
        // the shell shows that room and drives the same RunAsync every other caller drives.
        //
        // #1222: it shows it as a transcript, like every other room. It used to navigate to the Task
        // pane, which meant a room started here rendered as a full-width graph for exactly as long as
        // its pump ran and then became a transcript underneath the person when the run settled and
        // the reopen callback (see reopenRoomAsync above) called OpenAsync. One room, two renderings,
        // swapping mid-run.
        ViewModel.NewWorkflow.RunRequested += StartAuthoredRunAsync;
        // #211: the Outputs preview box is imperative control state, not bound — nothing cleared
        // or refreshed it when the drill-in moved to a different step, so it kept showing the
        // previously-selected step's last-previewed file. Clear on every change, then auto-load
        // the new step's first output (if it has one) so a freshly-opened step's Outputs tab isn't
        // just an unexplained blank box either.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedStep))
            {
                _ = ShowSelectedStepFirstOutputAsync();
            }

            if (e.PropertyName == nameof(MainWindowViewModel.CurrentSection))
            {
                ApplyShellLayout();
            }
        };
        ViewModel.Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChatViewModel.IsShapePanelOpen) or nameof(ChatViewModel.IsPipelineRoom) or nameof(ChatViewModel.IsWorkflowOn))
            {
                ApplyShellLayout();
            }
        };
        ApplyShellLayout();
        Closed += (_, _) =>
        {
            _liveRefreshTimer.Stop();
            _pairingCountdownTimer.Stop();
        };
        Closing += OnClosing;
    }

    /// <summary>
    /// #217: keeps the custom title bar's maximize/restore glyph in sync with <see cref="Window.WindowState"/>
    /// regardless of what changed it — this window's own two maximize entry points, but also Aero
    /// Snap, the taskbar, and Win+Up/Down, none of which route through this window's own click handlers.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeRestoreIcon((WindowState)change.NewValue!);
        }
    }

    /// <summary>The #211 hook above's body — its own method so a test can await it deterministically instead of racing the fire-and-forget subscription.</summary>
    private Task ShowSelectedStepFirstOutputAsync()
    {
        ClearArtifactPreview();
        var firstOutput = ViewModel.SelectedStep?.OutputFiles.FirstOrDefault();
        return firstOutput is null ? Task.CompletedTask : firstOutput.PreviewCommand.ExecuteAsync(null);
    }

    // ── #217: the custom title bar's own chrome. MainWindow.axaml marks the bar and its three
    // buttons with chrome:WindowDecorationProperties.ElementRole (TitleBar/MinimizeButton/
    // MaximizeButton/CloseButton), which is what gives native drag-to-move, double-click-to-
    // maximize, and Aero-Snap/taskbar integration on platforms that honor it. The handlers below
    // are a second, always-active path to the same four actions — belt-and-suspenders for
    // whichever platform or input device doesn't route through the non-client role. ─────────────

    /// <summary>Drag-to-move: <see cref="Window.BeginMoveDrag"/> on any left-press over the bar's empty space (the icon/title label is IsHitTestVisible="False"; the three buttons handle their own clicks and never bubble a press up to this handler).</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Double-click-to-maximize: the one title-bar convention <see cref="OnTitleBarPointerPressed"/>'s drag doesn't already cover for free.</summary>
    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximizeRestore();

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT
    {
        public int styleOld;
        public int styleNew;
    }

    private const uint WM_STYLECHANGING = 0x007C;
    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_STYLECHANGING && (int)wParam == GWL_STYLE)
        {
            var styleStruct = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            styleStruct.styleNew |= WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            Marshal.StructureToPtr(styleStruct, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximizeRestore()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Swaps the maximize button between a square (maximize) and an overlapping-squares
    /// glyph (restore) — the same convention every platform's own caption buttons use, so a
    /// maximized window doesn't offer the same "maximize" affordance twice.</summary>
    private void UpdateMaximizeRestoreIcon(WindowState state)
    {
        var isMaximized = state == WindowState.Maximized;
        MaximizeButtonIcon.Data = Geometry.Parse(isMaximized
            ? "M 3,5 L 9,5 L 9,11 L 3,11 Z M 5,5 L 5,3 L 11,3 L 11,9 L 9,9"
            : "M 3,3 L 11,3 L 11,11 L 3,11 Z");
        MaximizeButton.SetValue(ToolTip.TipProperty, isMaximized ? "Restore" : "Maximize");
    }

    /// <summary>
    /// Populates the ▤ front door's first-run/empty surface (the "No rooms yet." state + the
    /// vendor-readiness line, #1071/#478) from Local UI Configuration (UI spec §3.1), plus (M15
    /// Phase 1) pre-fills the Run action's bindings/template inputs from whatever was last
    /// remembered — call once at startup. Readiness is refreshed here as well as on activation so a
    /// bare launch that lands on first-run shows it without a section change firing.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _ = _session.EnsureDaemonConnectedAsync(cancellationToken);

        await RefreshHomeAsync(cancellationToken);
        ViewModel.NewWorkflow.RefreshVendorReadiness();

        // #336: the switcher is chrome, not a destination, so nothing will ever "activate" it into
        // existence — it has to be populated once at startup and kept current by pushes thereafter.
        await ViewModel.Rooms.RefreshAsync(_session, cancellationToken);

        ViewModel.BindingsFilePath = await _session.LoadLastBindingsFilePathAsync(cancellationToken);
        ViewModel.WorkflowTemplateFilePath = await _session.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);

        // #1068: the theme was already applied to the app by App startup; this syncs the Settings
        // toggle's selected button to the persisted choice (or System when nothing was ever chosen).
        ViewModel.ThemePreference = await _configurationStore.LoadThemeAsync(cancellationToken) ?? ThemeNames.System;
    }

    /// <summary>
    /// #1068: applies a Settings → Appearance choice — to the running app immediately (no restart),
    /// to the toggle's selected state, and to disk so the next launch opens in it.
    /// </summary>
    internal async Task ChooseThemeAsync(string theme)
    {
        ViewModel.ThemePreference = theme;
        AppearanceTheme.Apply(theme);
        await _configurationStore.RecordThemeAsync(theme);
    }

    /// <summary>
    /// Rooms-as-root landing (#1055, <c>docs/design/02-screens.md</c>: "Same root. Both surfaces open
    /// on rooms. 'Needs you' is a filter, not the front door"). The desktop lands in the work rather
    /// than the Home dashboard: the switcher is needs-you-first ordered (#1054), so its top row is the
    /// most important room — open it into the detail pane, exactly the daily-driver mockup (a room
    /// selected in the list, its content in the main pane).
    /// <para>
    /// <see cref="App"/> orchestrates startup and calls this <em>after</em> <see cref="InitializeAsync"/>
    /// has populated the switcher — and only on a bare launch. When a launch argument names a room
    /// (<c>aer-ui &lt;room-directory&gt;</c>), App opens that directory instead and never calls this, so the
    /// two opens are sequenced rather than racing to mutate the session's current room. Keeping the
    /// landing out of <see cref="InitializeAsync"/> is what makes that ordering possible.
    /// </para>
    /// <para>
    /// Deliberately a no-op in two cases. When the fleet is empty, <see cref="MainWindowViewModel.CurrentSection"/>
    /// stays <see cref="ShellSection.Home"/> so the first-run empty state is untouched — replacing that
    /// with the design's "Point Baton at a folder" is its own slice and touches journey J8. When a
    /// record is already open, this must never yank it out from under the user. The switcher's fleet is
    /// daemon-only (<c>RoomClient.GetFleetAsync</c>), so this reads whatever <see cref="RoomsViewModel"/>
    /// was already populated with rather than fetching again.
    /// </para>
    /// </summary>
    internal async Task LandOnTopRoomAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentRoomDirectoryPath is not null)
        {
            return;
        }

        if (ViewModel.Rooms.Items.Count > 0)
        {
            await OpenAsync(ViewModel.Rooms.Items[0].RoomDirectoryPath, cancellationToken);
        }
    }

    /// <summary>
    /// The full "open a room directory" action (UI spec §3.1): loads and renders it via
    /// <see cref="LoadAsync"/>, then — only on success — records it as the most recently opened
    /// directory and starts/stops live re-projection (M14 Phase 2, issue #119) depending on whether
    /// the projected workflow has reached a terminal state. This is what <see cref="OpenButton"/>
    /// and a Home room card's Open both call; <see cref="App"/>'s CLI-argument
    /// launch path calls it too, so a directory opened that way is remembered exactly like one
    /// opened by hand.
    /// <para>
    /// If <paramref name="roomDirectoryPath"/> names a file rather than a directory, it is opened as
    /// a raw <c>WorkflowDefinition</c> template instead (M14 Phase 3, issue #120: the DAG view
    /// renders both bound rooms and not-yet-instantiated templates). A template is not a room —
    /// there is no execution state to remember a re-projection cadence for, so it is neither
    /// recorded to <see cref="LocalUiConfigurationStore"/> (that store is room-directory recents
    /// specifically, per its Phase 2 decision of record) nor live-refreshed.
    /// </para>
    /// </summary>
    /// <summary>
    /// Re-entry guard for the switcher's selection-is-opening wiring (#336) — see its subscription
    /// above. Not a general busy flag: it suppresses exactly the selection change that
    /// <see cref="OpenAsync"/> causes by refreshing the list it was opened from.
    /// </summary>
    private bool _isOpeningFromSwitcher;

    /// <summary>
    /// Opens the record a switcher row points at (#336). Routing between the chat and workflow panes
    /// is <see cref="OpenAsync"/>'s existing job — it already decides by whether the directory has
    /// session metadata, the same structural fact <see cref="RoomFleetItem.IsSession"/> carries, so
    /// this deliberately does not re-derive it here.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget, matching every other event-handler entry point in this file. An unloadable
    /// directory is not an exception — <see cref="LoadAsync"/> renders <c>outcome.ErrorMessage</c>
    /// into the detail pane's status line, and the selection stays where the user put it. Anything
    /// that does throw here faults this task unobserved, which is the pre-existing shape of every
    /// <c>_ = SomethingAsync()</c> in this file rather than something new; a general answer for
    /// surfacing background-work failures in the UI belongs with #462, not bolted on here.
    /// </remarks>
    private async Task OpenFromSwitcherAsync(string roomDirectoryPath)
    {
        _isOpeningFromSwitcher = true;
        try
        {
            await OpenAsync(roomDirectoryPath);
        }
        finally
        {
            _isOpeningFromSwitcher = false;
        }
    }

    public async Task OpenAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        RoomDirectoryPathBox.Text = roomDirectoryPath;

        // #1222: a file is not a room, and this no longer draws one as though it were. The decision
        // and its reasons are 02-screens.md's #1222 amendment — including that it IS a decision
        // rather than a reading of the passages around it, which a second reader was right to press
        // on. Said rather than silently ignored: the box is labelled "Room directory", so a file in
        // it is a mistake worth a sentence.
        if (File.Exists(roomDirectoryPath) && !Directory.Exists(roomDirectoryPath))
        {
            StatusText.Text =
                "That is a file, not a room. A workflow file is opened in Author — Edit shape.";
            return;
        }

        ViewModel.BindingsFilePath = await _session.LoadLastBindingsFilePathAsync(cancellationToken);
        ViewModel.WorkflowTemplateFilePath = await _session.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);

        _session.SetCurrentRoomDirectory(roomDirectoryPath);

        await LoadAsync(roomDirectoryPath, cancellationToken);

        // M24 Phase 1 (issue #262): whether the directory materialized an interactive session
        // (.aer/room.json marker with Kind=Interactive) — see RoomClient.LoadSessionMetadataAsync's
        // remarks.
        var sessionMetadata = await _session.LoadSessionMetadataAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        ViewModel.CurrentSection = ShellSection.Chat;
        if (sessionMetadata != null)
        {
            ViewModel.Chat.LoadFromMetadata(sessionMetadata, roomDirectoryPath);
            await RefreshChatModeAsync(sessionMetadata.SessionId, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            // The fork here is no longer "which screen" — both kinds of room render in the
            // transcript, which is why the section is set once above them both — only "is there a
            // session to talk to", which is what IsPipelineRoom carries into the composer.
            ViewModel.Chat.Clear();
            ViewModel.Chat.OpenPipelineRoom(roomDirectoryPath, ViewModel.PausedSteps);
        }

        if (_session.LastLoadSucceeded)
        {
            await _session.RecordOpenedAsync(roomDirectoryPath, cancellationToken);
            await RefreshRecordListsAsync(cancellationToken);
        }

        UpdateLiveRefreshTimer();
    }

    /// <summary>
    /// The mutation seam this phase exists to prove (issue #137): a Run action that either starts a
    /// fresh room from <paramref name="workflowTemplateFilePath"/> + <paramref name="bindingsFilePath"/>,
    /// or resumes an already-bound <paramref name="roomDirectoryPath"/> after a pause or stop — the
    /// same <c>RunCommand.ExecuteAsync</c> call <c>aer run</c> makes, reused in-process rather than
    /// spawning the installed binary (the seam decision this phase resolves). Bindings are never
    /// record-once-ok: #443 src/Aer.Ui.Core/BindingsEditorViewModel.cs
    /// persisted in a room directory (M14 Phase 2's decision of record) and the template is only
    /// ever <em>bound from</em> on a fresh start (<see cref="RunOptions.WorkflowFilePath"/>'s own
    /// remarks, which also cover what a resume now reads it for), so both are asked for here rather
    /// than inferred — "ask, don't infer," the same discipline the recents list already follows for
    /// room-directory discovery (UI spec §3.1).
    /// <para>
    /// The pump itself runs on a background thread (<see cref="Task.Run(Func{Task})"/>): a live
    /// execution can take however long a real worker takes, and the UI thread must never await that
    /// directly. This method starts <see cref="MainWindow"/>'s existing 2-second poller
    /// (<see cref="_liveRefreshTimer"/>) immediately, before the pump even begins, so it is what
    /// renders progress for the run's entire duration — this method itself only touches projection
    /// controls once more, via <see cref="OpenAsync"/>, after the pump has already reached its
    /// fixed point.
    /// </para>
    /// </summary>
    public async Task RunAsync(
        string roomDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        RoomDirectoryPathBox.Text = roomDirectoryPath;
        // Kept in sync here, not just read from at dispatch time, so a later decision — whose
        // bindings path the session asks this same property for at call time ("ask, don't infer",
        // M14 Phase 2's decision of record) — has a value even when RunAsync was invoked directly
        // (a test, or a future non-button caller) rather than through a click handler.
        ViewModel.BindingsFilePath = bindingsFilePath;

        await _session.RunAsync(roomDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken);
    }

    /// <summary>
    /// Author's Save &amp; Run (M19 Phase 4, #189): each run gets a fresh room directory beside the
    /// authored files — one workspace per workflow, rooms inside it — and the shell shows that room
    /// while it runs.
    /// <para>
    /// #1222: it shows it as a transcript, like every other room. It used to navigate to the Task
    /// pane, so a room started here rendered as a full-width graph for exactly as long as its pump
    /// ran, then turned into a transcript underneath the person when the run settled and the reopen
    /// callback (see <c>reopenRoomAsync</c> in the constructor) called <see cref="OpenAsync"/>. One
    /// room, two renderings, swapping mid-run.
    /// </para>
    /// <para>
    /// The room is opened <em>before</em> the run rather than after it: <see cref="RunAsync"/> does
    /// not return until the pump reaches its fixed point, and the transcript has to be on screen
    /// while the work happens, not once it is over. Nothing here touches the directory itself, which
    /// <see cref="RunAsync"/> creates. A named method rather than the lambda it used to be so a test
    /// can drive the claim without authoring a workflow through the guided flow first.
    /// </para>
    /// </summary>
    internal async Task StartAuthoredRunAsync(string workflowFilePath, string bindingsFilePath)
    {
        var roomDirectoryPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(workflowFilePath)!,
            $"room-{DateTime.Now:yyyyMMdd-HHmmss}");
        ViewModel.Chat.Clear();
        ViewModel.Chat.OpenPipelineRoom(roomDirectoryPath, ViewModel.PausedSteps);
        ViewModel.CurrentSection = ShellSection.Chat;
        await RunAsync(roomDirectoryPath, workflowFilePath, bindingsFilePath);
    }

    /// <summary>
    /// The "new room" action behind both entry points to it — Home's empty-state button and the
    /// switcher header's "+ New" (docs/design/02-screens.md:58, the list header reads "Rooms + New").
    /// Extracted here so the two callers share one path: open the Template Picker, and on a materialized
    /// room, register it (<see cref="RefreshRecordListsAsync"/>) then open or run it by its kind — Open,
    /// not Run, for an interactive room whose first turn the daemon already dispatches (issue #262).
    /// Returns the materialized room's directory, or null if the picker was dismissed.
    /// </summary>
    internal async Task<string?> StartNewRoomFromTemplateAsync(CancellationToken cancellationToken = default)
    {
        var picker = new Views.TemplatePickerWindow(this);
        await picker.ShowDialog(this);

        if (picker.MaterializedRoomDirectoryPath is not { } roomPath)
        {
            return null;
        }

        await RefreshRecordListsAsync(cancellationToken);
        if (await InteractiveSessionMaterializer.ReadRoomKindAsync(roomPath) == RoomKind.Interactive)
        {
            await OpenAsync(roomPath, cancellationToken);
        }
        else
        {
            var workflowPath = System.IO.Path.Combine(roomPath, "workflow.json");
            var bindingsPath = System.IO.Path.Combine(roomPath, "bindings.json"); // vocabulary-ok: technical file path
            await RunAsync(roomPath, workflowPath, bindingsPath, cancellationToken);
        }

        return roomPath;
    }

    /// <summary>
    /// The Template Picker's chat/codebase session creation (M24 Phase 1 desktop wiring, issue #262)
    /// — <see cref="RoomClient.StartInteractiveSessionAsync"/> exposed the same way <see cref="RunAsync"/>
    /// exposes the run mutation, so a modal window with no session reference of its own can still go
    /// through the daemon-first path instead of materializing directly in-process.
    /// </summary>
    public Task<RoomClient.SessionStartOutcome> StartInteractiveSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
        => _session.StartInteractiveSessionAsync(request, cancellationToken);

    /// <summary>
    /// The Chat view's Send button (M24 Phase 1, issue #262): dispatches the next turn via
    /// <see cref="RoomClient.SendSessionMessageAsync"/> and marks it in flight
    /// (<see cref="ChatViewModel.BeginSend"/>) — completion is observed by the same live-refresh
    /// poll <see cref="RefreshAsync"/> already drives, not by awaiting this call any further.
    /// </summary>
    private async Task SendChatMessageAsync()
    {
        var chat = ViewModel.Chat;
        var message = chat.InputText.Trim();
        if (message.Length == 0 || chat.RoomDirectoryPath is not { } roomDirectoryPath)
        {
            return;
        }

        // #1074: the composer never blocks (slice of #462) — the enqueue-vs-post decision lives on
        // ChatViewModel.SendJoinsQueue (its doc carries the FIFO and #1167 open-gate clauses).
        // MainWindow's live-refresh poll drains one per completion.
        if (chat.SendJoinsQueue)
        {
            chat.EnqueueMessage(message);
            return;
        }

        chat.BeginSend(message, chat.LastKnownTurnsCount);
        await PostChatTurnAsync(roomDirectoryPath, message).ConfigureAwait(true);
    }

    /// <summary>
    /// Posts one already-in-flight chat turn to the daemon (#262) and surfaces a dispatch failure.
    /// The caller marks the send in flight first — <see cref="ChatViewModel.BeginSend"/> for a typed
    /// message, <see cref="ChatViewModel.BeginDrainedSend"/> for a queued one (#1074). Returns true if
    /// the turn dispatched, false if it failed — the drain uses that to decide whether to remove the
    /// queued message. Completion itself is observed later by the live-refresh poll.
    /// </summary>
    private async Task<bool> PostChatTurnAsync(string roomDirectoryPath, string message)
    {
        var outcome = await _session.SendSessionMessageAsync(
            new SendSessionMessageRequest(DirectoryPath: roomDirectoryPath, Message: message)).ConfigureAwait(true);

        if (outcome.ErrorMessage is { } error)
        {
            ViewModel.Chat.FailSend(error);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The composer's send-vs-newline rule (design 04-workers-commands-control.md: "Enter sends,
    /// shift-enter breaks a line ... getting it backwards is a daily irritation"). A bare Enter sends;
    /// Enter with any modifier (Shift for a newline, or anything else) is left to the <c>AcceptsReturn</c>
    /// TextBox. Pure so the decision is unit-tested without standing up a window.
    /// </summary>
    internal static bool IsSendKeystroke(Key key, KeyModifiers modifiers)
        => key == Key.Enter && modifiers == KeyModifiers.None;

    private void OnChatInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsSendKeystroke(e.Key, e.KeyModifiers))
        {
            return;
        }

        // Handled before the TextBox inserts a newline. SendChatMessageAsync itself no-ops on an empty
        // message or with no room open, so a stray Enter in an empty composer is harmless.
        e.Handled = true;
        _ = SendChatMessageAsync();
    }

    /// <summary>
    /// The permission-gate keyboard rule (0022 §4, #481): a <em>bare</em> <c>y</c> answers "Allow once"
    /// and a bare <c>n</c> answers "Deny once" — returning the <see cref="PermissionDecisionKind"/> for
    /// that key, or <see langword="null"/> for anything else. Any modifier disqualifies it, so a `y`/`n`
    /// that is the letter in an accelerator (Ctrl+Y, Alt+N) never answers a live ask; and Enter is not a
    /// case here at all, so a reflex key can never approve. Pure, so the rule is unit-tested without a window.
    /// </summary>
    internal static string? PermissionAnswerFor(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None)
        {
            return null;
        }

        return key switch
        {
            Key.Y => PermissionDecisionKind.AllowOnce,
            Key.N => PermissionDecisionKind.Deny,
            _ => null,
        };
    }

    private void OnPermissionGateKeyDown(object? sender, KeyEventArgs e)
    {
        if (PermissionAnswerFor(e.Key, e.KeyModifiers) is not { } decisionKind
            || ViewModel.Chat.PendingPermission is not { } gate)
        {
            return;
        }

        // Never steal the keystroke from a focused text field — the operator may be typing a reply or
        // editing a path. A gate answered by muscle memory while typing would be exactly the reflex
        // 0022 §4 exists to prevent.
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        var command = decisionKind == PermissionDecisionKind.AllowOnce
            ? gate.AllowOnceCommand
            : gate.DenyCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The Chat page's own "start new chat" entry point (#290) — mirrors
    /// <see cref="Views.TemplatePickerWindow"/>'s "chat session" template option (same
    /// <see cref="StartSessionRequest"/> shape, same <see cref="StartInteractiveSessionAsync"/> call)
    /// without the picker's four unrelated template kinds. An empty working directory starts a plain
    /// chat, matching the picker's own behavior with no project directory entered.
    /// </summary>
    private async Task StartNewChatAsync()
    {
        var chat = ViewModel.Chat;
        if (chat.IsStartingNewChat)
        {
            return;
        }

        chat.IsStartingNewChat = true;
        chat.StatusText = string.Empty;

        try
        {
            var workingDirectory = string.IsNullOrWhiteSpace(chat.NewChatWorkingDirectory)
                ? null
                : chat.NewChatWorkingDirectory.Trim();

            var request = new StartSessionRequest(
                Adapter: chat.NewChatAdapter,
                WorkingDirectory: workingDirectory);

            var outcome = await StartInteractiveSessionAsync(request).ConfigureAwait(true);
            if (outcome.Metadata is not { } metadata)
            {
                chat.StatusText = outcome.ErrorMessage ?? "Failed to start the room.";
                return;
            }

            _session.SetCurrentRoomDirectory(metadata.RoomDirectoryPath);
            await _session.RecordOpenedAsync(metadata.RoomDirectoryPath).ConfigureAwait(true);
            chat.LoadFromMetadata(metadata, metadata.RoomDirectoryPath);
            await RefreshRecordListsAsync(CancellationToken.None).ConfigureAwait(true);
            UpdateLiveRefreshTimer();
        }
        finally
        {
            chat.IsStartingNewChat = false;
        }
    }

    /// <summary>
    /// The Chat view's Commands button (M24 Phase 2 follow-up): fetches this session's discovered
    /// skills/commands/agents (plus recently-used ordering) on open, closes without a fetch on
    /// toggle-off.
    /// </summary>
    private async Task ToggleChatCommandsAsync()
    {
        var chat = ViewModel.Chat;
        if (chat.IsCommandMenuOpen)
        {
            chat.IsCommandMenuOpen = false;
            return;
        }

        if (chat.SessionId is not { } sessionId)
        {
            return;
        }

        var (result, error) = await _session.GetSessionCommandsAsync(sessionId).ConfigureAwait(true);
        if (result is { } commands)
        {
            chat.LoadCommands(commands);
            chat.IsCommandMenuOpen = true;
        }
        else if (error is { } err)
        {
            chat.StatusText = err;
        }
    }

    /// <summary>
    /// An invokable command/skill/agent picked from the Commands menu (#286). "/compact" and
    /// "/clear" are real dedicated actions, not text insertion — inserting them as literal text only
    /// ever "worked" because the resulting message happened to be interpreted by the vendor CLI's
    /// own (unverified, vendor-owned) slash-command handling, not because AER actually invoked
    /// anything. Everything else still inserts into the message box for the user to review/edit
    /// before Send, and gets recorded as recently-used the same way regardless of which path ran.
    /// </summary>
    private async void OnChatCommandItemClick(object? sender, RoutedEventArgs e)
    {
        var chat = ViewModel.Chat;
        if (e.Source is not Control { DataContext: ChatCapabilityItemViewModel item } || chat.SessionId is not { } sessionId)
        {
            return;
        }

        chat.IsCommandMenuOpen = false;
        _ = _session.RecordCommandUsedAsync(sessionId, item.Name);

        switch (item.Name)
        {
            case "/compact":
                var compactOutcome = await _session.CompactSessionAsync(sessionId).ConfigureAwait(true);
                chat.StatusText = compactOutcome.ErrorMessage ?? "Compacting room context…";
                break;

            case "/clear":
                var (cleared, clearError) = await _session.ClearSessionAsync(sessionId).ConfigureAwait(true);
                if (cleared != null && chat.RoomDirectoryPath is { } roomDirectoryPath)
                {
                    chat.MarkTranscriptCleared();
                    chat.LoadFromMetadata(cleared, roomDirectoryPath);
                    chat.StatusText = "Room context cleared.";
                }
                else
                {
                    chat.StatusText = clearError ?? "Failed to clear room context.";
                }
                break;

            default:
                chat.InputText = string.IsNullOrEmpty(chat.InputText) ? item.Name : $"{chat.InputText} {item.Name}";
                break;
        }
    }

    /// <summary>Session-level mode (M24 Phase 2 follow-up): applies to whichever vendor is currently active, taking effect on the next turn. Updates the persistent header indicator on success (#286) — re-reads from the daemon rather than assuming the requested mode round-tripped verbatim, since a future non-canonical grant would otherwise desync the indicator from the truth in bindings.json.</summary>
    private async Task SetChatModeAsync(string mode)
    {
        var chat = ViewModel.Chat;
        if (chat.SessionId is not { } sessionId)
        {
            return;
        }

        var outcome = await _session.SetSessionModeAsync(sessionId, mode).ConfigureAwait(true);
        if (outcome.ErrorMessage is { } error)
        {
            chat.StatusText = error;
            return;
        }

        chat.StatusText = $"Mode set to {mode}.";
        await RefreshChatModeAsync(sessionId).ConfigureAwait(true);
    }

    /// <summary>Re-reads the active session mode from the daemon (#286) and reflects it in <see cref="ChatViewModel.CurrentMode"/> — best-effort, since a stale/missing mode indicator is a cosmetic gap, not a failure worth surfacing as a chat error.</summary>
    private async Task RefreshChatModeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var (mode, _) = await _session.GetSessionModeAsync(sessionId, cancellationToken).ConfigureAwait(true);
        ViewModel.Chat.CurrentMode = mode;
    }

    /// <summary>
    /// Everything that asks for the open room to be run (review follow-up, issue #250; #1215 made it
    /// one event rather than one handler per caller). On a room that hasn't finished, this is exactly
    /// the old unconditional resume-in-place call. On a finished room —
    /// <see cref="MainWindowViewModel.IsRoomFinished"/> — resuming the same directory is a proven
    /// no-op (see that property's remarks), so this clones the currently-open room's recorded
    /// <c>.aer/workflow-path</c>/bindings file into a fresh sibling <c>room-{timestamp}</c> directory
    /// instead, the same naming <see cref="MainWindow"/>'s "Save &amp; Run" and template flows
    /// already use, and runs that. The finished room's own directory is left untouched.
    /// <para>
    /// The fork stays here rather than moving to the card: which of the two a click means is the
    /// room's state's answer, not the caller's, and duplicating it per caller is how the two would
    /// drift apart.
    /// </para>
    /// </summary>
    private async Task OnRoomRunRequestedAsync()
    {
        var roomDirectoryPath = RoomDirectoryPathBox.Text ?? string.Empty;
        var workflowTemplateFilePath = ViewModel.WorkflowTemplateFilePath;
        var bindingsFilePath = ViewModel.BindingsFilePath ?? string.Empty;

        if (ViewModel.IsRoomFinished && !string.IsNullOrWhiteSpace(roomDirectoryPath))
        {
            var parentDirectory = System.IO.Path.GetDirectoryName(roomDirectoryPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                roomDirectoryPath = System.IO.Path.Combine(parentDirectory, $"room-{DateTime.Now:yyyyMMdd-HHmmss}");
            }
        }

        await RunAsync(roomDirectoryPath, workflowTemplateFilePath, bindingsFilePath);
    }

    /// <summary>
    /// Handles the header's Workflow switch (#1216). Returns the engine's refusal reason, or null on
    /// success, and re-loads either way: on success so the switch renders the journal it just changed,
    /// and on refusal so a <see cref="ToggleButton"/> that flipped its own visual on click is put back
    /// to what the room actually says.
    /// </summary>
    private async Task<string?> OnWorkflowSwitchRequestedAsync(bool isOn)
    {
        var roomDirectoryPath = RoomDirectoryPathBox.Text ?? string.Empty;
        var refusal = await _session.SetWorkflowSwitchAsync(roomDirectoryPath, isOn).ConfigureAwait(true);

        // The fingerprint's isWorkflowOff term is what makes this re-render rather than short-circuit.
        if (!string.IsNullOrWhiteSpace(roomDirectoryPath))
        {
            await LoadAsync(roomDirectoryPath).ConfigureAwait(true);
        }

        return refusal;
    }

    /// <summary>
    /// Starts a fresh template-editing session over a blank in-memory <see cref="WorkflowDefinition"/>
    /// (M16 Phase 1, issue #150) — nothing touches disk until <see cref="SaveTemplateAsync"/>.
    /// Deliberately synchronous: there is no file to read.
    /// </summary>
    public void NewTemplate() => ViewModel.TemplateEditor.StartNewFile();

    /// <summary>
    /// Opens <paramref name="templateFilePath"/> into the template editor (M16 Phase 1, issue #150)
    /// via the engine's own <see cref="TemplateProjectionLoader"/>/<c>WorkflowDefinitionParser</c> —
    /// never a second parser. This is a separate surface from <see cref="OpenAsync"/>'s read-only
    /// template projection (M14 Phase 3), which stays untouched: the read-only view is how a
    /// template (or a bound snapshot's diff against one) is *inspected*; this editor is how a
    /// template *file* is changed — and only ever a file, never a bound snapshot (UI spec §2, §4,
    /// §5). Phase 1 exposes exactly the metadata fields (<c>WorkflowTemplateId</c>,
    /// <c>WorkflowTemplateVersion</c>); the loaded steps ride through every save untouched until
    /// Phase 2's structural editing.
    /// </summary>
    public async Task OpenTemplateInEditorAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        TemplateEditorPathBox.Text = templateFilePath;
        await ViewModel.TemplateEditor.OpenFromFileAsync(templateFilePath, cancellationToken);
    }

    /// <summary>
    /// Saves the editor's current state to <paramref name="templateFilePath"/> through
    /// <c>WorkflowDefinitionWriter</c>, so the saved file round-trips through the exact
    /// parser/validator every other consumer uses (M16 Phase 1, issue #150). Implements Flow spec
    /// §11.1's version-increment rule directly (settled ahead of this phase): a save whose content
    /// differs from the loaded baseline increments <c>WorkflowTemplateVersion</c> — unless the user
    /// explicitly set a different version themselves, which is respected as-is (a hand-editor may
    /// legitimately do the same) — a no-op save writes nothing and increments nothing, and a
    /// brand-new template's first save has no predecessor to distinguish from, so it saves the
    /// version as entered. Deliberately not gated on <see cref="MainWindowViewModel.IsMutationInFlight"/>:
    /// record-once-ok: #443 src/Aer.Ui.Core/TemplateEditorViewModel.cs
    /// a template file is not durable room state, no §15 room lock is involved, and an edit is
    /// visible only to future instantiations regardless (UI spec §5).
    /// </summary>
    public async Task SaveTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
        => await ViewModel.TemplateEditor.SaveToFileAsync(templateFilePath, cancellationToken);

    /// <summary>
    /// Starts a fresh worker-bindings editing session over an empty config (M16 Phase 4, issue #153)
    /// — nothing touches disk until <see cref="SaveBindingsAsync"/>. Deliberately synchronous: there
    /// is no file to read.
    /// </summary>
    public void NewBindings() => ViewModel.BindingsEditor.StartNewFile();

    /// <summary>
    /// Opens <paramref name="bindingsFilePath"/> into the bindings editor (M16 Phase 4, issue #153)
    /// via <see cref="BindingsProjectionLoader"/> — never a second parser. Bindings are a UI/CLI
    /// input, never durable room state (UI spec §4, §9; M14 Phase 2's decision of record), so unlike
    /// <see cref="OpenAsync"/> there is no read-only counterpart this editor has to stay separate
    /// from: authoring is the only surface a bindings file has in this UI.
    /// </summary>
    public async Task OpenBindingsInEditorAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        BindingsEditorPathBox.Text = bindingsFilePath;
        await ViewModel.BindingsEditor.OpenFromFileAsync(bindingsFilePath, cancellationToken);
        RefreshBindingsTemplateCrossCheck();
    }

    /// <summary>
    /// Saves the bindings editor's current rows to <paramref name="bindingsFilePath"/> through
    /// <c>WorkerBindingConfigWriter</c>, so the saved file round-trips through the exact
    /// <c>WorkerBindingConfigParser.Parse</c> every other consumer uses (M16 Phase 4, issue #153,
    /// the same round-trip bar as Phase 1's template writer). Unlike <see cref="SaveTemplateAsync"/>,
    /// there is no version field to increment — a bindings file has no §11.1 counterpart.
    /// </summary>
    public async Task SaveBindingsAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        await ViewModel.BindingsEditor.SaveToFileAsync(bindingsFilePath, cancellationToken);
        RefreshBindingsTemplateCrossCheck();
    }

    /// <summary>
    /// Recomputes <see cref="MainWindowViewModel.BindingsEditor"/>'s
    /// <see cref="BindingsEditorViewModel.MissingTemplateWorkerNames"/> (UI spec §9's advisory
    /// cross-check, M16 Phase 4's named open question) — which <c>Worker</c> names the template
    /// currently open <em>in the template editor</em> (<see cref="TemplateEditorViewModel.Baseline"/>)
    /// declares that have no entry in the bindings editor's own <see cref="BindingsEditorViewModel.Entries"/>.
    /// <para>
    /// <b>Source decision of record:</b> reads <c>ViewModel.TemplateEditor.Baseline</c> — the
    /// template-editing surface's own in-memory state — rather than the read-only DAG view's
    /// transient <c>LoadTemplateAsync</c> result, which is never retained as a field. This is a
    /// read-only consultation of already-computed state, not a change to template-editing code
    /// (Phases 1-3 own that; this phase excludes touching it) — nothing here writes to, or is called
    /// from, <see cref="TemplateEditorViewModel"/> or <see cref="OpenTemplateInEditorAsync"/>.
    /// </para>
    /// <para>
    /// Advisory display only, never a save gate (§9): bindings are deliberately not template data
    /// and never persisted in a room directory, so <see cref="SaveBindingsAsync"/> never consults
    /// this. Called explicitly — after New/Open/Save bindings and after adding a row — rather than
    /// wired to any template-editor change notification, since this phase does not touch that
    /// surface's events either.
    /// </para>
    /// </summary>
    private void RefreshBindingsTemplateCrossCheck()
        => ViewModel.BindingsEditor.RefreshTemplateCrossCheck(ViewModel.TemplateEditor.Baseline);

    /// <summary>
    /// Re-projects the currently open room directory in place (M14 Phase 2's change-observation
    /// requirement, issue #119) — a no-op if nothing has been opened yet. Public and directly
    /// awaitable for the same reason <see cref="LoadAsync"/> is (issue #118): a test can drive
    /// exactly one re-projection deterministically, rather than pumping the dispatcher and waiting
    /// on <see cref="_liveRefreshTimer"/>'s real elapsed-time tick, which is what actually calls this
    /// in production.
    /// </summary>
    /// <summary>
    /// One tick of <see cref="_liveRefreshTimer"/>. A room that can still change re-projects every
    /// tick as it always has; a settled one pays only a <see cref="FileInfo"/> stat until its
    /// <c>room.jsonl</c> actually moves — see <see cref="UpdateLiveRefreshTimer"/> for why a settled
    /// room is watched at all now.
    /// </summary>
    internal async Task OnLiveRefreshTickAsync(CancellationToken cancellationToken = default)
    {
        if (_session.ShouldLiveRefresh)
        {
            await RefreshAsync(cancellationToken);
            return;
        }

        if (_session.CurrentRoomDirectoryPath is { } settledRoomDirectoryPath
            && HasRoomJournalChanged(settledRoomDirectoryPath))
        {
            await RefreshAsync(cancellationToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentRoomDirectoryPath is not { } currentRoomDirectoryPath)
        {
            return;
        }

        await LoadAsync(currentRoomDirectoryPath, cancellationToken);

        // M24 Phase 1 (issue #262): the chat step is paused indefinitely between turns, never
        // Terminal, so ShouldLiveRefresh (and therefore this tick) keeps running the whole time a
        // session is open — exactly the completion signal ChatViewModel.LoadFromMetadata relies on
        // instead of a second polling loop or a push from POST /api/sessions/send.
        if (ViewModel.IsChatVisible && _session.IsClientMode)
        {
            var sessionMetadata = await _session.LoadSessionMetadataAsync(currentRoomDirectoryPath, cancellationToken).ConfigureAwait(true);
            if (sessionMetadata != null)
            {
                var chat = ViewModel.Chat;
                chat.LoadFromMetadata(sessionMetadata, currentRoomDirectoryPath);

                // #1074: LoadFromMetadata clears IsSending when the turn lands — drain the head here,
                // on the same tick that observed completion. Peek-then-post-then-DequeueHead-on-success
                // so a failed dispatch leaves the message queued (never dropped). The gate conditions
                // live on ChatViewModel.CanDrainQueue (its doc carries #1167's open-gate clause;
                // LastSendFailed's own doc carries why it, not StatusText, pauses the drain).
                if (chat.CanDrainQueue && chat.TryPeekQueuedMessage(out var queued) && queued is not null)
                {
                    chat.BeginDrainedSend(queued.Text, sessionMetadata.Turns.Count);
                    if (await PostChatTurnAsync(currentRoomDirectoryPath, queued.Text).ConfigureAwait(true))
                    {
                        // Remove the exact item dispatched, by identity — the head stayed live during
                        // the post, so a positional dequeue could drop a different message the operator
                        // removed meanwhile (#1074 second-reader). No-ops if they removed this one.
                        chat.RemoveQueuedMessage(queued);
                    }
                }
            }
        }

        // While the poller is observing an open room, a visible Home stays live too — the cards
        // and inbox ride the same tick rather than owning a second timer (HomeViewModel's
        // scan-scope decision of record).
        if (ViewModel.IsHomeVisible)
        {
            await RefreshHomeAsync(cancellationToken);
        }

        UpdateLiveRefreshTimer();
    }

    /// <summary>
    /// The seam this phase exists to prove (issue #118), reaching the screen: loads
    /// <paramref name="roomDirectoryPath"/> through <see cref="RoomProjectionLoader"/> and renders
    /// its per-step statuses as plain <see cref="TextBlock"/> rows — deliberately minimal, per
    /// Phase 1's exclusion of "any styling worth defending". Public and directly awaitable (rather
    /// than fired from the constructor or a <c>Loaded</c> event) so a test can drive it
    /// deterministically without pumping the dispatcher on a timer. Extended in Phase 2 (issue #119)
    /// to also render the fuller <see cref="RoomProjection.History"/> surface, but
    /// <see cref="StatusText"/>/<see cref="StepsPanel"/>'s own rendering is untouched from Phase 1.
    /// </summary>
    public async Task LoadAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var outcome = await _session.LoadAsync(roomDirectoryPath, cancellationToken);

        if (outcome.Projection is not { } projection)
        {
            // A real GUI has no stderr/exit-code convention to fail into (Aer.Cli's Program.cs
            // boundary) — an invalid room directory or a malformed snapshot/event log renders as an
            // in-window message instead. The session has already cleared the mutation surfaces.
            StatusText.Text = outcome.ErrorMessage;
            ClearProjectionPanels();
            ViewModel.IsRoomFinished = false;
            return;
        }

        RenderProjection(projection, roomDirectoryPath);
    }

    private string? _lastRenderedProjectionFingerprint;

    /// <summary>
    /// Renders a loaded <see cref="RoomProjection"/> across all view panels without re-querying the session.
    /// </summary>
    public void RenderProjection(RoomProjection projection, string roomDirectoryPath)
    {
        RenderedProjectionCountForTests++;
        var stepsFingerprint = string.Join(",", projection.State.Steps.Select(s => $"{s.StepId.Value}:{s.Status}:{s.LatestExecutionId?.Value}"));
        var attemptsCount = projection.History.AttemptsByStepId.Sum(kv => kv.Value.Count);
        var convLength = _conversationOutputDirectory != null && File.Exists(System.IO.Path.Combine(_conversationOutputDirectory, "transcript.jsonl")) ? new FileInfo(System.IO.Path.Combine(_conversationOutputDirectory, "transcript.jsonl")).Length : 0;
        // #390: the inline permission gate keys on projection.PendingPermission (a sibling of State on
        // RoomProjection, not a FlowState member), which none of the other terms above reflect — a turn
        // can raise or clear a gate with no step/status/decision change, so without this the fingerprint
        // would short-circuit the render that must show or hide it.
        var pendingPermissionId = projection.PendingPermission?.PermissionRequestId ?? "none";
        // #1178: dormancy transitions ride room.jsonl like the gate does, so a dormancy-only change
        // (including the one a successful Wake produces) alters no other term — same class of bug as
        // the pendingPermissionId note above, same fix.
        var dormancyCount = projection.DormancyTransitions.Count;
        // #1219: exactly the same class of bug as the two notes above, and found by a control arm
        // rather than reasoned about. Since #1219 the room's §15 lock is an input to what this method
        // renders (the headline reads Stopped or Working from it), and a process dying changes the
        // lock while changing *nothing* in the projection — so without this term the fingerprint
        // short-circuits, and a room that dies while you are looking at it goes on saying "Working"
        // indefinitely. One reading, used for the key and for the render below.
        var isFlowLockHeld = ConcurrencyGuard.IsHeld(roomDirectoryPath);
        // #1216: the workflow switch is another room.jsonl fact that moves nothing else — throwing it
        // changes no step, no status and no decision — so it joins the list above for the same reason.
        var isWorkflowOff = projection.IsWorkflowOff;
        var fingerprint = $"{roomDirectoryPath}|{projection.State.Status}|{stepsFingerprint}|{attemptsCount}|{projection.History.Decisions.Count}|{projection.Lineage.Executions.Count}|{convLength}|{pendingPermissionId}|{dormancyCount}|{isFlowLockHeld}|{isWorkflowOff}"; // vocabulary-ok: state fingerprint key

        if (_lastRenderedProjectionFingerprint == fingerprint)
        {
            return;
        }
        _lastRenderedProjectionFingerprint = fingerprint;

        RoomDirectoryPathBox.Text = roomDirectoryPath;

        var workflowPathFile = System.IO.Path.Combine(roomDirectoryPath, ".aer", "workflow-path");
        if (File.Exists(workflowPathFile))
        {
            try { ViewModel.WorkflowTemplateFilePath = File.ReadAllText(workflowPathFile).Trim(); } catch { }
        }
        else
        {
            var fallbackWorkflowJson = System.IO.Path.Combine(roomDirectoryPath, "workflow.json");
            if (File.Exists(fallbackWorkflowJson))
            {
                ViewModel.WorkflowTemplateFilePath = fallbackWorkflowJson;
            }
            else
            {
                ViewModel.WorkflowTemplateFilePath = string.Empty;
            }
        }

        var bindingsPathFile = System.IO.Path.Combine(roomDirectoryPath, ".aer", "bindings-path"); // vocabulary-ok: technical file path
        if (File.Exists(bindingsPathFile))
        {
            try { ViewModel.BindingsFilePath = File.ReadAllText(bindingsPathFile).Trim(); } catch { }
        }

        ViewModel.IsRoomFinished = projection.State.Status == WorkflowStatus.Terminal;
        StatusText.Text = $"Workflow status: {projection.State.Status}"; // vocabulary-ok: technical status display

        StepsPanel.Children.Clear();
        foreach (var step in projection.State.Steps)
        {
            StepsPanel.Children.Add(new TextBlock { Text = $"{step.StepId}: {step.Status}" });
        }

        var statusByStepId = projection.State.Steps.ToDictionary(step => step.StepId, step => step.Status);
        RenderDag(projection.Snapshot.Steps, statusByStepId);

        RenderExecutionHistory(projection);
        RenderDecisions(projection);
        RenderSupplementaryExecutions(projection);
        RenderArtifactLineage(projection, roomDirectoryPath);
        RenderConversationExecutions(projection, roomDirectoryPath);
        RenderConversation();

        var workerAdapters = GetWorkerAdapters(roomDirectoryPath, ViewModel.BindingsFilePath);

        // M19 Phase 3 (#188): the per-step drill-in — built after the session has rebuilt
        // PausedSteps, so each paused step's inline decision card is the same live VM instance.
        ViewModel.RebuildRoomSteps(
            projection, roomDirectoryPath, isFlowLockHeld,
            previewFileAsync: filePath => ShowArtifactPreviewAsync(filePath),
            showConversation: ShowConversation,
            workerAdapters: workerAdapters);

        // #390: surface (or clear) the inline conversational permission gate from the same projection.
        // The answer delegate captures this render's roomDirectoryPath; the daemon broadcasts a fresh
        // projection on answer, and the LoadAsync refresh below it re-renders with the gate cleared.
        ViewModel.Chat.SurfacePendingPermission(
            projection.PendingPermission,
            projection.PermissionAnswers,
            (permissionRequestId, decisionKind, reason) =>
                AnswerPermissionFromGateAsync(roomDirectoryPath, permissionRequestId, decisionKind, reason),
            projection.DormancyTransitions,
            projection.IsDormant,
            () => _ = WakeDormantRoomAsync(roomDirectoryPath),
            projection.RecordedDecisionMoments,
            ViewModel.PausedSteps);

        // #1216: the header switch renders the room's durable fact rather than remembering what was
        // last clicked — 0020's rule 1, and the reason a room switched off on the phone shows off here.
        ViewModel.Chat.IsWorkflowOn = !isWorkflowOff;
    }

    private async Task WakeDormantRoomAsync(string roomDirectoryPath)
    {
        var success = await _session.ClearTurnHostDormancyAsync(roomDirectoryPath).ConfigureAwait(true);
        if (success)
        {
            await LoadAsync(roomDirectoryPath).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Records the operator's answer to the inline permission gate (0022, #390) and re-renders. The
    /// client call owns the <see cref="MainWindowViewModel.IsMutationInFlight"/> lifecycle (disabling
    /// the gate for its duration); the follow-up <see cref="LoadAsync"/> re-reads the projection whose
    /// <c>PendingPermission</c> the daemon has now cleared, so the gate vanishes on the same code path
    /// that drew it. A failed answer surfaces on the chat status line and leaves the gate up to retry.
    /// </summary>
    private async Task AnswerPermissionFromGateAsync(
        string roomDirectoryPath, string permissionRequestId, string decisionKind, string? reason)
    {
        var outcome = await _session.AnswerPermissionAsync(
            roomDirectoryPath, permissionRequestId, decisionKind, reason).ConfigureAwait(true);
        if (outcome.ErrorMessage is { } error)
        {
            ViewModel.Chat.StatusText = error;
            return;
        }

        // Clear the gate synchronously, before the awaited LoadAsync below yields to the dispatcher.
        // AnswerPermissionAsync's finally already re-enabled the buttons (IsMutationInFlight=false), and
        // the daemon has removed this request from its registry — so without this, a second click during
        // LoadAsync's I/O would POST again and 404. The answer succeeded; the gate's job is done.
        ViewModel.Chat.PendingPermission = null;
        await LoadAsync(roomDirectoryPath).ConfigureAwait(true);
    }

    private static Dictionary<string, string> GetWorkerAdapters(string roomDirectoryPath, string? bindingsFilePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetBindingsFile = bindingsFilePath;
        if (string.IsNullOrWhiteSpace(targetBindingsFile) || !File.Exists(targetBindingsFile))
        {
            targetBindingsFile = System.IO.Path.Combine(roomDirectoryPath, "bindings.json"); // vocabulary-ok: technical file path
        }
        if (!File.Exists(targetBindingsFile))
        {
            var metaFile = System.IO.Path.Combine(roomDirectoryPath, ".aer", "bindings-path"); // vocabulary-ok: technical file path
            if (File.Exists(metaFile))
            {
                try { targetBindingsFile = File.ReadAllText(metaFile).Trim(); } catch { }
            }
        }
        if (File.Exists(targetBindingsFile))
        {
            try
            {
                var json = File.ReadAllText(targetBindingsFile);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("Adapter", out var adapterProp) || prop.Value.TryGetProperty("adapter", out adapterProp)) // vocabulary-ok: JSON property name
                    {
                        if (adapterProp.GetString() is { } adapterStr)
                        {
                            result[prop.Name] = adapterStr;
                        }
                    }
                }
            }
            catch { }
        }
        return result;
    }

    /// <summary>Clears every read-only projection panel — the error-path counterpart of a successful render, shared by room and template loads.</summary>
    private void ClearProjectionPanels()
    {
        _lastRenderedProjectionFingerprint = null;
        StepsPanel.Children.Clear();
        DagCanvas.Children.Clear();
        HistoryPanel.Children.Clear();
        DecisionsPanel.Children.Clear();
        SupplementaryPanel.Children.Clear();
        LineagePanel.Children.Clear();
        ConversationExecutionsPanel.Children.Clear();
        ClearConversation();
        ClearArtifactPreview();
        DiffPanel.Children.Clear();
        ViewModel.ClearRoomSteps();
    }

    /// <summary>The one status system's token keys (M19 Phase 5, #190) — line color and area tint per <see cref="StepStatus"/>, resolved from the active theme at render time so the DAG follows light/dark like every other surface.</summary>
    private static readonly IReadOnlyDictionary<StepStatus, (string Border, string Background)> StatusTokenKeys =
        new Dictionary<StepStatus, (string, string)>
        {
            [StepStatus.Pending] = ("Status.Idle", "Status.IdleBg"),
            [StepStatus.Running] = ("Status.Working", "Status.WorkingBg"),
            [StepStatus.Succeeded] = ("Status.Finished", "Status.FinishedBg"),
            [StepStatus.Failed] = ("Status.Failed", "Status.FailedBg"),
            [StepStatus.Cancelled] = ("Status.Idle", "Status.IdleBg"),
            [StepStatus.Paused] = ("Status.NeedsInput", "Status.NeedsInputBg"),
            [StepStatus.Rejected] = ("Status.Failed", "Status.FailedBg"),
        };

    // this.FindResource(key) (no theme argument) resolves against ThemeVariant.Default, never this
    // window's ActualThemeVariant -- it silently matches Tokens.axaml's literal x:Key="Default"
    // dictionary (the light palette) regardless of which variant the window is actually rendering
    // in, unlike XAML DynamicResource bindings, which are ActualThemeVariant-aware. Every DAG node
    // border/fill went through this before the fix, producing light-palette color against the
    // dark-palette inherited text -- the washed-out boxes.
    private IBrush Token(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private const double DagCellWidth = 170;
    private const double DagCellHeight = 150;
    private const double DagNodeWidth = 150;
    // Tall enough for the icon plus up to 4 label lines (step id, worker, status, optional
    // pause line) at Type.Caption.FontSize — 56 fit the old text-only, 2-line label but let a
    // 3-4 line label with the #206 status icon on top spill past the border.
    private const double DagNodeHeight = 96;

    /// <summary>
    /// Renders <see cref="DagLayoutEngine.Layout"/>'s result over <paramref name="steps"/> as boxes
    /// (one per step, positioned by <see cref="DagNode.Rank"/>/<see cref="DagNode.Column"/>) joined
    /// by lines (one per <see cref="DagEdge"/>): solid for an ordinary <c>DependsOn</c> dependency,
    /// dashed for a declared <c>PausePoint.SupersedeTargets</c> entry (UI spec §10; issue #120).
    /// <paramref name="statusByStepId"/> is <c>null</c> for a raw template — nothing to overlay — or
    /// populated from the bound room's <see cref="FlowState"/> for a real room directory; either way
    /// every node still renders, just without a status-derived background in the template case.
    /// </summary>
    private void RenderDag(IReadOnlyList<WorkflowStepDefinition> steps, IReadOnlyDictionary<StepId, StepStatus>? statusByStepId)
        => RenderDag(
            DagLayoutEngine.Layout(steps), DagCanvas, statusByStepId,
            // M19 Phase 3 (#188): a node click opens that step's drill-in — room canvas only; the
            // template editor's preview has no room state to drill into.
            onNodeSelect: stepId => ViewModel.SelectStepById(stepId.Value));

    /// <summary>
    /// Re-layouts and renders <see cref="TemplateEditorViewModel.PreviewLayout"/> into
    /// <see cref="TemplateEditorDagCanvas"/> (M16 Phase 2, issue #151) — a dedicated canvas, not the
    /// read-only <see cref="DagCanvas"/>, so the editor's live preview can never collide with an
    /// independently-opened room or template's read-only rendering (Phase 1's separate-surfaces
    /// decision, extended to the graph view). <see langword="null"/> (an invalid or empty in-progress
    /// graph) clears the canvas rather than rendering a stale layout.
    /// </summary>
    private void RenderTemplateEditorDag()
    {
        if (ViewModel.TemplateEditor.PreviewLayout is not { } layout)
        {
            TemplateEditorDagCanvas.Children.Clear();
            TemplateEditorDagCanvas.Width = 0;
            TemplateEditorDagCanvas.Height = 0;
            return;
        }

        RenderDag(layout, TemplateEditorDagCanvas, statusByStepId: null);
    }

    private void RenderDag(
        DagLayout layout, Canvas canvas, IReadOnlyDictionary<StepId, StepStatus>? statusByStepId,
        Action<StepId>? onNodeSelect = null)
    {
        canvas.Children.Clear();

        if (layout.Nodes.Count == 0)
        {
            canvas.Width = 0;
            canvas.Height = 0;
            return;
        }

        var nodeByStepId = layout.Nodes.ToDictionary(node => node.StepId);

        foreach (var edge in layout.Edges)
        {
            var from = nodeByStepId[edge.From];
            var to = nodeByStepId[edge.To];

            var line = new Line
            {
                StartPoint = new Point(
                    from.Column * DagCellWidth + DagNodeWidth / 2,
                    from.Rank * DagCellHeight + DagNodeHeight),
                EndPoint = new Point(
                    to.Column * DagCellWidth + DagNodeWidth / 2,
                    to.Rank * DagCellHeight),
                // A supersede edge is prospective — DagEdge.IsSupersede's doc has the canonical
                // wording — so it borrows the muted RESTING hue (Idle), not Unavailable's
                // "no longer readable": possible and quiet, never yet a constraint.
                Stroke = edge.IsSupersede ? Token("Status.Idle") : Token("Color.Border"),
                StrokeThickness = 1.5,
            };

            if (edge.IsSupersede)
            {
                line.StrokeDashArray = [4, 2];
            }

            canvas.Children.Add(line);
        }

        foreach (var node in layout.Nodes)
        {
            var status = statusByStepId?.GetValueOrDefault(node.StepId);
            // A bound room's node carries its status as border + tint (the one status system);
            // a raw template's node is a plain surface — nothing has executed, nothing to say.
            var (borderBrush, background) = status is { } knownStatus && StatusTokenKeys.TryGetValue(knownStatus, out var keys)
                ? (Token(keys.Border), Token(keys.Background))
                : (Token("Color.Border"), Token("Color.Surface"));

            var label = status is { } renderedStatus
                ? $"{node.StepId}\n{node.Worker}\n{renderedStatus}"
                : $"{node.StepId}\n{node.Worker}";

            if (node.HasPausePoint)
            {
                label += node.SupersedeTargets.Count > 0
                    ? $"\n[pause -> {string.Join(", ", node.SupersedeTargets)}]"
                    : "\n[pause]";
            }

            var textBlock = new TextBlock
            {
                Text = label,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = this.FindResource("Type.Caption.FontSize") as double? ?? 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            // Post-M19 design review (#206/#209): status icon per node, same glyph set and mapping
            // as every other status-bearing surface — a raw template's node has no status, so no
            // icon (nothing has executed, nothing to say, same as its plain-surface color).
            var content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 4,
            };
            if (status is { } iconStatus)
            {
                content.Children.Add(new ShapePath
                {
                    Data = this.FindResource(Converters.StatusIconMap.GeometryKeyFor(iconStatus)) as Geometry,
                    Stroke = borderBrush,
                    StrokeThickness = 1.6,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                });
            }

            content.Children.Add(textBlock);

            var border = new Border
            {
                Width = DagNodeWidth,
                Height = DagNodeHeight,
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = this.FindResource("Radius.Medium") is CornerRadius radius ? radius : default,
                Child = content,
            };

            if (onNodeSelect is { } select)
            {
                var stepId = node.StepId;
                border.PointerPressed += (_, _) => select(stepId);
            }

            Canvas.SetLeft(border, node.Column * DagCellWidth);
            Canvas.SetTop(border, node.Rank * DagCellHeight);
            canvas.Children.Add(border);
        }

        var maxColumn = layout.Nodes.Max(node => node.Column);
        var maxRank = layout.Nodes.Max(node => node.Rank);
        canvas.Width = (maxColumn + 1) * DagCellWidth;
        canvas.Height = (maxRank + 1) * DagCellHeight;
    }

    /// <summary>
    /// Renders every step's full attempt history (not just the latest attempt
    /// <see cref="StepsPanel"/> already shows), plus its retry count, pause state, and declared
    /// <c>SupersedeTargets</c> — the read-model surface <see cref="Aer.Flow.Domain.FlowState"/>
    /// alone does not carry (issue #119).
    /// </summary>
    private void RenderExecutionHistory(RoomProjection projection)
    {
        HistoryPanel.Children.Clear();
        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        foreach (var stepState in projection.State.Steps)
        {
            var attempts = projection.History.AttemptsByStepId.GetValueOrDefault(
                stepState.StepId, (IReadOnlyList<ExecutionAttempt>)[]);

            for (var index = 0; index < attempts.Count; index++)
            {
                var attempt = attempts[index];
                var classificationSuffix = attempt.FailureClassification is { } classification
                    ? $" ({classification})"
                    : string.Empty;
                var nonProcessSuffix = attempt.IsNonProcess ? " [non-process]" : string.Empty;

                // #597: same diagnostic as the plain-language step list, on the raw history panel.
                // Wrapped because a contract-failure reason names every unsatisfied output and runs
                // to 500 characters, which would otherwise widen the panel off-screen.
                var reasonSuffix = string.IsNullOrWhiteSpace(attempt.Reason)
                    ? string.Empty
                    : $" — {attempt.Reason}";

                HistoryPanel.Children.Add(new TextBlock
                {
                    Text = $"{stepState.StepId} attempt {index + 1}/{attempts.Count}: " +
                           $"{attempt.ExecutionId} -> {attempt.Status}{classificationSuffix}{nonProcessSuffix}" +
                           reasonSuffix,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                });
            }

            var summary = $"{stepState.StepId}: consecutive failures={stepState.ConsecutiveFailureCount}";
            if (stepState.Status == StepStatus.Paused)
            {
                var pausePoint = stepDefinitionByStepId[stepState.StepId].PausePoint;
                var supersedeTargets = pausePoint?.SupersedeTargets is { Count: > 0 } targets
                    ? string.Join(", ", targets)
                    : "none";
                summary += $", paused (underlying outcome={stepState.PausedOutcome}), supersede targets=[{supersedeTargets}]"; // vocabulary-ok: internal step summary
            }

            if (stepState.PendingSupplementaryExecutionId is { } pendingSupplement)
            {
                summary += $", pending supplementary execution={pendingSupplement}";
            }

            if (stepState.IsPendingSupersedeTarget)
            {
                summary += ", pending supersede dispatch"; // vocabulary-ok: internal step summary
            }

            // Wraps for the same reason the attempt rows above do — a half-wrapped panel is worse
            // than either choice made consistently.
            HistoryPanel.Children.Add(new TextBlock
            {
                Text = summary,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }
    }

    /// <summary>
    /// The Ctrl+C equivalent (§9's host-initiated stop; M15 Phase 4, issue #140): cancels whichever
    /// pump this window's own Run/Decide action currently has in flight — a no-op when nothing is.
    /// Fire-and-forget by design, mirroring <c>Aer.Cli.Program.cs</c>'s <c>Console.CancelKeyPress</c>
    /// handler: signalling <see cref="RoomClient.RequestHostStop"/> is only the signal.
    /// <see cref="RunAsync"/>/<see cref="DecideAsync"/>'s own awaited pump is what actually drives
    /// §9's intent-first record for every execution still in flight, then the durable
    /// <c>ExecutionCancelled</c> §7's second reflection phase needs, and clears
    /// <see cref="MainWindowViewModel.IsMutationInFlight"/> once that pump reaches its fixed point.
    /// </summary>
    public Task StopAsync()
    {
        _session.RequestHostStop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Window-close semantics with a pump in flight (issue #140): the first <c>Closing</c> is
    /// cancelled and treated as a Stop request instead of a silent abandonment — the CLI's Ctrl+C
    /// equivalent still fires even though there is no terminal to Ctrl+C in. Once the retained pump
    /// task has actually reached its fixed point (<see cref="RunAsync"/>/<see cref="DecideAsync"/>'s
    /// own <c>finally</c> already reflects that in the projection via their trailing
    /// <see cref="OpenAsync"/>), this closes the window for real — a plain, uncancelled close, since
    /// <see cref="_closeConfirmed"/> is now set.
    /// </summary>
    public async void ConfirmCloseAndExit()
    {
        if (_session.IsDaemonConfigured)
        {
            Show();
            Activate();

            var result = await ExitConfirmationWindow.ShowPromptAsync(this, ViewModel.HasRunningExecutions);
            if (result == null)
            {
                return;
            }

            _closeConfirmed = true;
            if (result == true)
            {
                _session.RequestHostStop();
                _ = _session.ShutdownDaemonAsync();
            }
            Close();
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        else
        {
            _closeConfirmed = true;
            Close();
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_session.IsDaemonConfigured)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (_session.IsDaemonConfigured)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_session.CurrentPumpTask is not { IsCompleted: false } pumpTask)
        {
            return;
        }

        e.Cancel = true;
        _session.RequestHostStop();
        _ = CloseOncePumpSettlesAsync(pumpTask);
    }

    private async Task CloseOncePumpSettlesAsync(Task pumpTask)
    {
        try
        {
            await pumpTask.ConfigureAwait(true);
        }
        catch
        {
            // RunAsync/DecideAsync's own try/catch already renders any AerFlowException as an
            // in-window message on their own await of this same task; this second await exists only
            // to learn that the pump has reached a fixed point, not to re-observe its outcome.
        }

        _closeConfirmed = true;
        Close();
    }



    private void RenderDecisions(RoomProjection projection)
    {
        DecisionsPanel.Children.Clear();
        foreach (var decision in projection.History.Decisions)
        {
            var target = decision.TargetStepId is { } targetStepId ? $", target={targetStepId}" : string.Empty;
            var supplement = decision.SupplementaryExecutionId is { } supplementaryExecutionId
                ? $", supplement={supplementaryExecutionId}"
                : string.Empty;
            var resolution = decision.Resolved ? "resolved" : "unresolved";

            DecisionsPanel.Children.Add(new TextBlock
            {
                Text = $"{decision.DecisionId}: {decision.DecisionType} on {decision.ReferencedExecutionId}" +
                       $"{target}{supplement} ({resolution})",
            });
        }
    }

    private void RenderSupplementaryExecutions(RoomProjection projection)
    {
        SupplementaryPanel.Children.Clear();
        foreach (var execution in projection.History.StepLessExecutions)
        {
            var nonProcessSuffix = execution.IsNonProcess ? " [non-process]" : string.Empty;
            SupplementaryPanel.Children.Add(new TextBlock
            {
                Text = $"{execution.ExecutionId} ({execution.Worker}): {execution.Status}{nonProcessSuffix}",
            });
        }
    }

    /// <summary>
    /// Renders <see cref="ArtifactLineage"/> (M14 Phase 4, issue #121): one block per execution,
    /// naming its declared inputs' resolved producers, then a row of buttons — one per file actually
    /// present in its output directory — each wired to <see cref="ShowArtifactPreviewAsync"/>.
    /// <paramref name="roomDirectoryPath"/> is <see cref="LoadAsync"/>'s own parameter, not
    /// <see cref="RoomClient.CurrentRoomDirectoryPath"/>: <c>LoadAsync</c> is a supported, directly-callable
    /// entry point in its own right (issue #118) that a caller may invoke without ever going through
    /// <see cref="OpenAsync"/> (which is the only place that field is set) — the rendered buttons must
    /// resolve against the directory this exact call just loaded, not a field that might still be
    /// null or, worse, stale from a previously opened task.
    /// </summary>
    private void RenderArtifactLineage(RoomProjection projection, string roomDirectoryPath)
    {
        LineagePanel.Children.Clear();

        var artifactsRootPath = System.IO.Path.Combine(roomDirectoryPath, ArtifactsDirectoryName);

        foreach (var execution in projection.Lineage.Executions)
        {
            var header = execution.StepId is { } stepId
                ? $"{stepId} — {execution.ExecutionId} ({execution.Worker})"
                : $"(supplementary) — {execution.ExecutionId} ({execution.Worker})";
            LineagePanel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.SemiBold });

            foreach (var link in execution.Inputs)
            {
                LineagePanel.Children.Add(new TextBlock
                {
                    Text = $"    input '{link.InputName}' <- {link.ProducerStepId} ({link.ProducerExecutionId})",
                });
            }

            if (execution.OutputFiles.Count == 0)
            {
                LineagePanel.Children.Add(new TextBlock { Text = "    (no output files)" });
                continue;
            }

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
            var filesPanel = new WrapPanel { Margin = new Thickness(16, 0, 0, 0) };
            foreach (var fileName in execution.OutputFiles)
            {
                var filePath = System.IO.Path.Combine(outputDirectory, fileName);
                var button = new Button { Content = fileName, Margin = new Thickness(0, 0, 4, 4) };
                button.Click += (_, _) => _ = ShowArtifactPreviewAsync(filePath);
                filesPanel.Children.Add(button);
            }

            LineagePanel.Children.Add(filesPanel);
        }
    }

    /// <summary>
    /// Reads one artifact file's content into <see cref="ArtifactPreviewBox"/> — "a file listing +
    /// plain-text preview," this phase's stated ceiling (issue #121), not content rendering of any
    /// kind beyond that. Public and directly awaitable, the same reason every other load-driving
    /// entry point on this window is (issue #118): a test can trigger exactly one preview
    /// deterministically instead of raising a real button-click event. Truncated defensively at
    /// <see cref="MaxArtifactPreviewLength"/> — an artifact is not guaranteed to be small or textual,
    /// and this preview is deliberately the cheapest thing that could show it, not a text-viewer.
    ///
    /// #868: every caller (the artifact chip buttons, the auto-preview fired on step selection, and
    /// the drill-in's own <c>previewFileAsync</c> wiring) funnels through this one method, which is
    /// what makes <see cref="_artifactPreviewGeneration"/> a single choke point rather than something
    /// each caller has to apply for itself. Two overlapping calls race their <c>File.ReadAllTextAsync</c>
    /// reads exactly as before; what changes is that only the call still holding the latest generation
    /// when its read (or its failure) completes is allowed to write the box, on both the success and
    /// the error path — an older, slower call that finishes after a newer one silently discards its
    /// own result instead of clobbering it.
    /// </summary>
    public async Task ShowArtifactPreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _artifactPreviewGeneration);
        try
        {
            var content = await ReadArtifactTextAsync(filePath, cancellationToken);
            if (generation != Volatile.Read(ref _artifactPreviewGeneration))
            {
                return;
            }

            ArtifactPreviewBox.Text = content.Length > MaxArtifactPreviewLength
                ? content[..MaxArtifactPreviewLength] + "\n… (truncated)"
                : content;
        }
        catch (OperationCanceledException)
        {
            // #871: every caller here is fire-and-forget, so an escaping cancellation becomes an
            // unobserved task exception rather than anything a person sees. A cancelled preview has
            // nothing to say -- discard it, exactly as a superseded one does above.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation != Volatile.Read(ref _artifactPreviewGeneration))
            {
                return;
            }

            ArtifactPreviewBox.Text = $"Cannot preview '{System.IO.Path.GetFileName(filePath)}': {ex.Message}";
        }
    }

    /// <summary>
    /// Rebuilds the conversation entry rows (M18 Phase 2, issue #178; UI spec §10.1): one row per
    /// execution whose durable output directory contains a transcript — discovery by content
    /// alone, never by which worker or binding produced the execution, so the row set is a pure
    /// projection of the artifact directories (§11), exactly like
    /// <see cref="RenderArtifactLineage"/>'s file buttons. Strictly per-execution: a retried or
    /// superseded step lists one row per attempt that recorded a transcript, each opening its own
    /// conversation.
    /// </summary>
    private void RenderConversationExecutions(RoomProjection projection, string roomDirectoryPath)
    {
        ConversationExecutionsPanel.Children.Clear();

        var artifactsRootPath = System.IO.Path.Combine(roomDirectoryPath, ArtifactsDirectoryName);

        foreach (var execution in projection.Lineage.Executions)
        {
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
            if (!TranscriptProjectionLoader.HasTranscript(outputDirectory))
            {
                continue;
            }

            var label = execution.StepId is { } stepId
                ? $"{stepId} — {execution.ExecutionId} ({execution.Worker})"
                : $"(supplementary) — {execution.ExecutionId} ({execution.Worker})";

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

            var viewButton = new Button { Content = "View conversation" };
            viewButton.Click += (_, _) => ShowConversation(outputDirectory, label);
            row.Children.Add(viewButton);

            ConversationExecutionsPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// Renders one execution's transcript as the conversation view (M18 Phase 2, issue #178) and
    /// remembers the selection so every subsequent <see cref="LoadAsync"/> re-renders it from the
    /// durable file — load-on-refresh (riding the same refresh/live-timer path as every other
    /// projection surface) is how the view follows a still-running exchange; there is deliberately
    /// no push/streaming channel (UI spec §10, live streaming assigned to no milestone). Public and
    /// directly callable for the same testability reason as <see cref="ShowArtifactPreviewAsync"/>.
    /// </summary>
    public void ShowConversation(string executionOutputDirectory, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionOutputDirectory);
        ArgumentException.ThrowIfNullOrEmpty(label);

        _conversationOutputDirectory = executionOutputDirectory;
        _conversationLabel = label;
        RenderConversation();
    }

    private void ClearConversation()
    {
        _conversationOutputDirectory = null;
        _conversationLabel = null;
        ConversationPanel.Children.Clear();
    }

    private void RenderConversation()
    {
        ConversationPanel.Children.Clear();

        if (_conversationOutputDirectory is null)
        {
            return;
        }

        // A selection can legitimately point at nothing durable by the next refresh (the room
        // directory was deleted or recreated) — clear rather than render a guess (§12).
        if (TranscriptProjectionLoader.Load(_conversationOutputDirectory) is not { } transcript)
        {
            ClearConversation();
            return;
        }

        ConversationPanel.Children.Add(new TextBlock { Text = _conversationLabel, FontWeight = FontWeight.SemiBold });

        if (transcript.Lines.Count == 0)
        {
            ConversationPanel.Children.Add(new TextBlock { Text = "(transcript exists but records no turns yet)" });
            return;
        }

        foreach (var line in transcript.Lines)
        {
            ConversationPanel.Children.Add(line switch
            {
                TranscriptLine.Turn turn => RenderTurn(turn),
                TranscriptLine.Malformed malformed => new TextBlock
                {
                    Text = $"line {malformed.LineNumber}: not a schema-valid turn — left as recorded in {TranscriptProjectionLoader.TranscriptFileName}",
                    Foreground = Token("Status.Failed"),
                    TextWrapping = TextWrapping.Wrap,
                },
                _ => throw new InvalidOperationException($"Unknown transcript line kind: {line.GetType().Name}"),
            });
        }
    }

    private Border RenderTurn(TranscriptLine.Turn turn)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = $"{turn.Sequence} · {turn.Role} ({turn.Vendor})",
            FontWeight = FontWeight.SemiBold,
        });
        content.Children.Add(new TextBlock { Text = turn.Text, TextWrapping = TextWrapping.Wrap });

        // Prompt on demand only (the phase plan's default): durable and §12-traceable, but each
        // prompt embeds the entire prior transcript (M17's full-transcript threading), so
        // expanded-by-default would drown the conversation in its own repetition.
        content.Children.Add(new Expander
        {
            Header = "Prompt",
            IsExpanded = false,
            Content = new TextBlock { Text = turn.Prompt, TextWrapping = TextWrapping.Wrap },
        });

        return new Border
        {
            Background = Token("Color.Surface"),
            BorderBrush = Token("Color.BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = this.FindResource("Radius.Medium") is CornerRadius radius ? radius : default,
            Padding = new Thickness(8),
            Child = content,
        };
    }

    /// <summary>
    /// The snapshot-vs-template diff surface (UI spec §5; M14 Phase 4, issue #121): loads
    /// <paramref name="templateFilePath"/> via <see cref="TemplateProjectionLoader"/> and compares it
    /// against the currently open room's bound snapshot via <see cref="SnapshotTemplateDiffer"/>.
    /// Requires a room directory to already be open — <see cref="RoomClient.LastSnapshot"/> is only ever set by
    /// <see cref="LoadAsync"/>'s success path, never by opening a raw template on its own, since a
    /// template with nothing bound to it has nothing to diff against.
    /// </summary>
    public async Task CompareToTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        DiffPanel.Children.Clear();

        if (_session.LastSnapshot is not { } snapshot)
        {
            DiffPanel.Children.Add(new TextBlock { Text = "Open a room directory before comparing it to a template." });
            return;
        }

        try
        {
            var template = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken);
            RenderDiff(SnapshotTemplateDiffer.Diff(snapshot, template));
        }
        catch (AerFlowException ex)
        {
            DiffPanel.Children.Add(new TextBlock { Text = ex.Message });
        }
    }

    private void RenderDiff(SnapshotTemplateDiff diff)
    {
        DiffPanel.Children.Clear();

        if (diff.TemplateIdMismatch)
        {
            DiffPanel.Children.Add(new TextBlock
            {
                Text = "This file is a different template than the one the room is bound to — a " +
                       "mismatch, not a divergence; no diff is shown.",
            });
            return;
        }

        DiffPanel.Children.Add(new TextBlock
        {
            Text = $"Bound snapshot is template version {diff.SnapshotTemplateVersion}; " +
                   $"current template file is version {diff.TemplateVersion}.",
        });

        if (!diff.HasDiverged)
        {
            DiffPanel.Children.Add(new TextBlock { Text = "No divergence: the bound snapshot matches the current template." });
            return;
        }

        foreach (var addedStepId in diff.AddedStepIds)
        {
            DiffPanel.Children.Add(new TextBlock { Text = $"+ {addedStepId} (added in template; not in the bound snapshot)" });
        }

        foreach (var removedStepId in diff.RemovedStepIds)
        {
            DiffPanel.Children.Add(new TextBlock { Text = $"- {removedStepId} (in the bound snapshot; removed from the template)" });
        }

        foreach (var changedStep in diff.ChangedSteps)
        {
            var changedFields = new List<string>();
            if (changedStep.WorkerChanged)
            {
                changedFields.Add("worker");
            }

            if (changedStep.InputsChanged)
            {
                changedFields.Add("inputs");
            }

            if (changedStep.OutputsChanged)
            {
                changedFields.Add("outputs");
            }

            if (changedStep.DependsOnChanged)
            {
                changedFields.Add("dependsOn");
            }

            if (changedStep.RetryPolicyChanged)
            {
                changedFields.Add("retryPolicy");
            }

            if (changedStep.PausePointChanged)
            {
                changedFields.Add("pausePoint"); // vocabulary-ok: internal field name
            }

            DiffPanel.Children.Add(new TextBlock { Text = $"~ {changedStep.StepId} changed: {string.Join(", ", changedFields)}" });
        }
    }

    /// <summary>Refreshes the ▤ front door's first-run empty state (#1071): whether any room exists, from Local UI Configuration's recents. The room cards + decision inbox this used to build moved to the switcher and its needs-you filter (#1072).</summary>
    private Task RefreshHomeAsync(CancellationToken cancellationToken)
        => ViewModel.Home.RefreshAsync(_session, cancellationToken);

    /// <summary>
    /// Rebuilds *both* surfaces that answer "what records exist" — the ▤ front door's first-run empty
    /// state and the switcher's list — after a structural change: a record was created, or one was
    /// opened for the first time. (The recents cards this once also rebuilt retired with #1071.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// These must refresh together, and the first cut of #336 is why this exists as one call rather
    /// than two adjacent ones. The switcher was populated once at startup and kept live by projection
    /// pushes thereafter — but a push only ever *updates an existing row*, so a room created after
    /// launch never joined the list at all. Found by running the app, not by a test: a freshly
    /// created session vanished from every surface except the folder picker.
    /// </para>
    /// <para>
    /// Deliberately not called from the live-refresh poller tick — that fires repeatedly while a room
    /// runs, and a full fleet re-fetch per tick would be pure waste. Status changes are exactly what
    /// the push fan-out already carries; this is only for records appearing or disappearing.
    /// </para>
    /// </remarks>
    internal async Task RefreshRecordListsAsync(CancellationToken cancellationToken = default)
    {
        await RefreshHomeAsync(cancellationToken);
        await ViewModel.Rooms.RefreshAsync(_session, cancellationToken);
    }

    /// <summary>
    /// Polling, not a <see cref="System.IO.FileSystemWatcher"/> (issue #119's named open question):
    /// simplest thing that works identically across the win/linux/mac CI matrix without depending on
    /// a given filesystem's watch semantics inside a container. Runs while a room is open.
    /// </summary>
    /// <remarks>
    /// It used to stop outright at <see cref="WorkflowStatus.Terminal"/>, on the reasoning that once
    /// nothing further can change (spec §12) there is nothing left to observe. #1216 ended that: the
    /// workflow switch is a <c>room.jsonl</c> fact, and a room is *most* likely to be switched while
    /// terminal, since one with work in flight is refused. Found by driving — another client switched
    /// a finished room and the open window went on saying the opposite indefinitely.
    ///
    /// The saving that reasoning bought is kept rather than thrown away, because it was real:
    /// <see cref="RefreshAsync"/> re-projects the whole room, and
    /// <see cref="Aer.Flow.Projection.ArtifactLineageProjector"/> does per-execution directory I/O.
    /// So a settled room ticks against <see cref="HasRoomJournalChanged"/> — one <see cref="FileInfo"/>
    /// stat — and only pays for the reload when the journal actually moved.
    /// </remarks>
    private void UpdateLiveRefreshTimer()
    {
        if (_session.ShouldLiveRefresh || _session.CurrentRoomDirectoryPath is not null)
        {
            _liveRefreshTimer.Start();
        }
        else
        {
            _liveRefreshTimer.Stop();
        }
    }

    private (long Length, DateTime WrittenAtUtc)? _lastSeenRoomJournalStamp;

    /// <summary>
    /// Whether the open room's <c>room.jsonl</c> has changed since the last tick — the cheap gate a
    /// settled room's tick runs instead of a full re-projection (see <see cref="UpdateLiveRefreshTimer"/>).
    /// Length AND write time, because a switch off followed by a switch on appends two lines of
    /// different lengths but could land inside one filesystem timestamp granule.
    /// A room with no journal is the common case and reads as unchanged, not as a change every tick.
    /// </summary>
    private bool HasRoomJournalChanged(string roomDirectoryPath)
    {
        (long, DateTime)? stamp = null;
        var journalPath = System.IO.Path.Combine(roomDirectoryPath, "room.jsonl");
        if (File.Exists(journalPath))
        {
            var info = new FileInfo(journalPath);
            stamp = (info.Length, info.LastWriteTimeUtc);
        }

        if (Equals(stamp, _lastSeenRoomJournalStamp))
        {
            return false;
        }

        _lastSeenRoomJournalStamp = stamp;
        return true;
    }
}
