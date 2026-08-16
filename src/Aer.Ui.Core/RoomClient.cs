using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Ui.Core;

public record OpenRoomRequest(string DirectoryPath);
// #1184: deliberately NO settle-on-exhaustion field. Attendedness is 0026 §4's "did the operator
// just try to use it", and nothing on the far side of an HTTP POST can tell whether that is true —
// it would become whatever the caller claimed, on ordinary workflow runs as much as chat turns.
// The one attended caller (the daemon's own session turn) reaches the pump in-process, so the knob
// belongs on the in-process seam only; RunAsync refuses rather than dropping a remote request for it.
public record RunRoomRequest(string DirectoryPath, string? WorkflowTemplateFilePath, string BindingsFilePath);
public record ArtifactReference(string ExecutionId, string FileName);

public record DecideRoomRequest(
    string DirectoryPath,
    string StepId,
    string ExecutionId,
    DecisionType DecisionType,
    string? TargetStepId = null,
    string? RevisionFilePath = null,
    string? SupplementaryWorker = null,
    string? SupplementaryOutputName = null,
    ArtifactReference? ArtifactReference = null,
    /// <summary>
    /// Optional. Decision 0056 (#1230) is the record: it governs when this is consulted at all, which
    /// is never for a room that already knows its workers.
    /// </summary>
    string? BindingsFilePath = null);

public record RunTemplateRequest(
    string TemplateId,
    string? PrimaryAdapter = null,
    string? SecondaryAdapter = null,
    string? RoomName = null,
    string? CustomPrompt = null,
    string? SecondaryCustomPrompt = null);

public record CancelRoomRequest(string DirectoryPath, string? ExecutionId = null);

/// <summary>M24 Phase 5 (#278): the request body shape shared by <c>/api/rooms/archive</c>, <c>/api/rooms/unarchive</c>, and <c>/api/rooms/delete</c>.</summary>
public record RoomDirectoryRequest(string DirectoryPath);
public record DaemonVersionInfo(string Version, bool HasRunningRooms, bool IsRemote = false);

public class BindingsPathHolder
{
    public string? BindingsFilePath { get; set; }
}

/// <summary>
/// One room's orchestration (M19 Phase 2, issue #187), updated in M20 Phase 2/3 to
/// support client-first daemonization: connects to Aer.Daemon background host process via REST/WebSockets
/// to execute pumps and stream real-time room projections. Falls back to in-process execution seamlessly
/// if the daemon cannot be reached or started. Enforces global mutex single-instance checks, 
/// local auth tokens, process supervision, and version-skew protection.
/// </summary>
public sealed partial class RoomClient
{
    // Partial split (#426, no behaviour change): this file holds the shell (shared connection/
    // client state + the constructor) and the *per-session core* — SetCurrentRoomDirectory /
    // LoadAsync / RunAsync / DecideAsync / CancelExecutionAsync and the ShouldApplyProjectionPush /
    // UpdateProjection / Rebuild* projection helpers. Peripheral clusters live in
    // RoomClient.{Connection,Sessions,Fleet,Remote,Persistence}.cs — same partial class, same
    // fields.
    //
    // #426 named a triplet here (CurrentRoomDirectoryPath, CurrentPumpTask,
    // _currentInFlightExecutions) as the surface #335 would lift into a per-session type. #335 has
    // landed and did exactly that for the *host* half: the registry, stop source and pump task now
    // live in HostedRun, keyed by session directory in _hostedRuns, so the daemon holds as many as
    // it is running. CurrentRoomDirectoryPath deliberately stayed single-valued — it is the
    // *client's* idea of what it is looking at, and desktop multi-session is #336's switcher.

    /// <summary>The outcome one load produces: exactly one of the two is non-null (§3's honest-error rule — an invalid directory is a rendered message, never a crash).</summary>
    public sealed record LoadOutcome(RoomProjection? Projection, string? ErrorMessage);

    /// <summary>Null on success; the in-window message otherwise (the M14 Phase 1 precedent: a GUI has no stderr/exit-code convention to fail into).</summary>
    public sealed record MutationOutcome(string? ErrorMessage);

    /// <summary><see cref="LoadOutcome"/>'s counterpart for <see cref="StartInteractiveSessionAsync"/> (M24 Phase 1 desktop wiring, issue #262).</summary>
    public sealed record SessionStartOutcome(SessionMetadata? Metadata, string? ErrorMessage);

    /// <summary><c>GET /api/sessions/{id}/commands</c>'s shape (M24 Phase 2 follow-up) — <see cref="WorkerCapabilities"/>'s own fields plus the additive <see cref="RecentlyUsed"/> sibling.</summary>
    public sealed record SessionCommandsResult(string Vendor, IReadOnlyList<WorkerCapabilityItem> Items, IReadOnlyList<string> Models, IReadOnlyList<string> RecentlyUsed);

    private readonly LocalUiConfigurationStore _configurationStore;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters;
    private readonly Func<string?> _bindingsFilePathProvider;
    private readonly Action _mutationStarted;
    private readonly Action _mutationFailed;
    private readonly Func<string, CancellationToken, Task> _reopenRoomAsync;
    private readonly Action<RoomProjection, string>? _onProjectionUpdated;
    private readonly string? _daemonUrl;

    /// <summary>#998: whether a failed daemon probe may launch a fresh Aer.Daemon child. The
    /// desktop app wants true; a test constructing a real <see cref="RoomClient"/> must pass
    /// false or a probe failure spawns a daemon that rewrites the REAL ~/.aer registration.</summary>
    private readonly bool _spawnDaemonOnDemand;
    private readonly string? _clientVersion;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;

    private bool _isClientMode;
    private string? _activeDaemonUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _wsCts;
    private ClientWebSocket? _progressWebSocket;
    private CancellationTokenSource? _progressWsCts;

    /// <summary>
    /// The client-side consumer for <c>/api/ws/progress</c> (M24 Phase 1's live in-turn streaming,
    /// issue #262) -- previously nothing subscribed to this socket at all, so a session's live
    /// <c>WorkerProgressEvent</c>s were broadcast into the void. Fires on the same
    /// <see cref="SynchronizationContext"/> marshaling <see cref="ReceiveWebSocketDataAsync"/>
    /// already uses for projection pushes. <c>DirectoryPath</c>/<c>StepId</c> are carried alongside
    /// the event exactly as the daemon broadcasts them, since this session may not have a chat
    /// directory open at all (a subscriber filters by directory itself).
    /// </summary>
    public event Action<string, string, WorkerProgressEvent>? SessionProgressReceived;

    /// <summary>
    /// Every projection push this client receives, for *any* directory (#336) — the switcher's list
    /// shows all known sessions at once, so a push for a session that is not the one currently open
    /// is precisely what that session's row needs. Deliberately separate from the detail pane's own
    /// consumer, which still goes through <see cref="ShouldApplyProjectionPush"/>: see
    /// <see cref="ReceiveWebSocketDataAsync"/>'s remarks on why this is two consumers of one frame
    /// rather than a widened filter. Fires on the same <see cref="SynchronizationContext"/> as
    /// <see cref="SessionProgressReceived"/>, and like it carries the directory alongside the payload
    /// so a subscriber can filter by directory itself.
    /// </summary>
    public event Action<string, RoomProjection>? FleetProjectionReceived;

    private void RaiseFleetProjectionReceived(string directoryPath, RoomProjection projection)
    {
        if (FleetProjectionReceived == null)
        {
            return;
        }

        if (_syncContext != null)
        {
            _syncContext.Post(_ => FleetProjectionReceived?.Invoke(directoryPath, projection), null);
        }
        else
        {
            FleetProjectionReceived.Invoke(directoryPath, projection);
        }
    }

    /// <summary>
    /// One in-flight pump this process is hosting, and everything needed to reach it: the
    /// caller-retained delivery point for a targeted cancel (M15 Phase 4, issue #140), the host-stop
    /// source that is the Ctrl+C equivalent <c>Aer.Cli</c> wires to <c>Console.CancelKeyPress</c>,
    /// and the pump task itself so a caller can wait for a durable fixed point rather than
    /// abandoning it mid-write.
    /// </summary>
    /// <remarks>
    /// A targeted cancel on an execution registered here is delivered in-process via
    /// <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>, never a second
    /// mutation-surface call, since §15's guard is already held for this call's entire duration
    /// (M10's decision of record).
    /// </remarks>
    internal sealed class HostedRun(InFlightExecutionRegistry inFlightExecutions, CancellationTokenSource hostStopSource)
    {
        public InFlightExecutionRegistry InFlightExecutions { get; } = inFlightExecutions;

        public CancellationTokenSource HostStopSource { get; } = hostStopSource;

        public Task? PumpTask { get; set; }
    }

    /// <summary>
    /// Every pump this process is hosting, keyed by session directory (#335).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this these were three single-slot fields, so a second concurrent run overwrote the
    /// first's registry and stop source. The consequences were not subtle: a targeted cancel for
    /// session A fell through to the out-of-process <c>CancelCommand</c> path because
    /// <see cref="CurrentRoomDirectoryPath"/> had moved to B, and <see cref="RequestHostStop()"/>
    /// cancelled whichever run started last — so asking the daemon to stop A stopped B instead.
    /// Whichever pump finished first then nulled the shared fields, breaking cancellation for the
    /// one still running.
    /// </para>
    /// <para>
    /// Keyed by <see cref="AerPaths.RecordKey"/>, deliberately the same normaliser #393's per-session
    /// turn lock uses: two directory keys that disagree about whether two spellings are one session
    /// is exactly the failure both primitives exist to prevent.
    /// </para>
    /// <para>
    /// Entries are removed by the run that added them, in its own <c>finally</c> — never "clear the
    /// current one", which is the bug above in a new spelling.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, HostedRun> _hostedRuns = new(AerPaths.RecordKeyComparer);

    /// <summary>The hosted run for <paramref name="roomDirectoryPath"/>, or null if this process is not running it.</summary>
    internal HostedRun? HostedRunFor(string roomDirectoryPath) =>
        _hostedRuns.TryGetValue(AerPaths.RecordKey(roomDirectoryPath), out var run) ? run : null;

    /// <summary>How many pumps this process is hosting right now — the multi-session invariant, observable.</summary>
    internal int HostedRunCount => _hostedRuns.Count;

    /// <summary>
    /// Claims the host slot for <paramref name="roomDirectoryPath"/>. Overwrites any stale entry
    /// rather than refusing: §15's <c>flow.lock</c> is what actually prevents two mutators on one
    /// session, and duplicating that decision here would mean two guards that can disagree.
    /// </summary>
    private HostedRun RegisterHostedRun(
        string roomDirectoryPath, InFlightExecutionRegistry inFlightExecutions, CancellationTokenSource hostStopSource)
    {
        var hostedRun = new HostedRun(inFlightExecutions, hostStopSource);
        _hostedRuns[AerPaths.RecordKey(roomDirectoryPath)] = hostedRun;
        return hostedRun;
    }

    /// <summary>
    /// Releases the host slot, but <b>only if it still holds this run</b>. The identity check is the
    /// point: an unconditional remove would let a finishing run evict a newer run that had already
    /// claimed the same directory, silently disarming cancellation for the one still going.
    /// </summary>
    private void ReleaseHostedRun(string roomDirectoryPath, HostedRun hostedRun) =>
        _hostedRuns.TryRemove(new KeyValuePair<string, HostedRun>(AerPaths.RecordKey(roomDirectoryPath), hostedRun));

    public MainWindowViewModel ViewModel { get; }

    /// <summary>
    /// Which room directory *this client instance* is currently viewing — set only by this
    /// session's own actions (<see cref="SetCurrentRoomDirectory"/>, <see cref="RunAsync"/>,
    /// <see cref="StartInteractiveSessionAsync"/>), never by another client's. Aer.Daemon's own
    /// "current room" is a separate, process-wide notion the daemon uses only to decide what a
    /// brand-new WS connection sees before this client has opened anything of its own — see
    /// <see cref="ShouldApplyProjectionPush"/>, which is what actually keeps two clients pointed
    /// at different directories from corrupting each other's view (pre-M24 defect, filed as part
    /// of issue #262's chat work).
    /// </summary>
    public string? CurrentRoomDirectoryPath { get; private set; }

    public bool LastLoadSucceeded { get; private set; }
    public WorkflowStatus? LastWorkflowStatus { get; private set; }
    public WorkflowDefinitionSnapshot? LastSnapshot { get; private set; }
    public bool IsDaemonConfigured => !string.IsNullOrEmpty(_daemonUrl);
    public bool IsClientMode => _isClientMode;

    /// <summary>
    /// The background task driving whichever pump is currently in flight — retained so the desktop's
    /// window-close handler can wait for it to reach a durable fixed point before actually closing,
    /// rather than abandoning it mid-write (issue #140).
    /// </summary>
    /// <remarks>
    /// Its one caller is the desktop close handler, which hosts a single run, so "the pump" is
    /// unambiguous there. In the daemon, which may host several (#335), this returns an arbitrary
    /// one — callers that mean a specific session must go through <see cref="HostedRunFor"/>. This
    /// stays deliberately non-plural rather than being made per-session for a caller that has no
    /// second session to distinguish; desktop multi-session is #336.
    /// </remarks>
    public Task? CurrentPumpTask => _hostedRuns.Values.FirstOrDefault()?.PumpTask;

    /// <summary>Whether the poller should keep observing: a successfully opened room that has not reached §12's terminal fixed point.</summary>
    public bool ShouldLiveRefresh => LastLoadSucceeded && LastWorkflowStatus != WorkflowStatus.Terminal;

    /// <param name="clientVersion">
    /// The version this client compares against the daemon's, passed in rather than read from an
    /// assembly here — the same "production wiring is the caller's decision" seam
    /// <paramref name="configurationStore"/> and <paramref name="adapters"/> already use, and the
    /// only shape that can name <c>Aer.Ui</c>'s version without <c>Aer.Ui.Core</c> reaching upward
    /// into the layer above it. Reading it here is what #1260 was.
    /// <para>
    /// <c>null</c> skips the skew check and connects. A caller that does not say who it is has no
    /// basis on which to claim skew, and shutting a daemon down over an unanswerable question is
    /// worse than talking to a possibly-older one.
    /// </para>
    /// </param>
    public RoomClient(
        LocalUiConfigurationStore configurationStore,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        MainWindowViewModel viewModel,
        Func<string?> bindingsFilePathProvider,
        Action mutationStarted,
        Action mutationFailed,
        Func<string, CancellationToken, Task> reopenRoomAsync,
        Action<RoomProjection, string>? onProjectionUpdated = null,
        string? daemonUrl = null,
        bool spawnDaemonOnDemand = true,
        string? clientVersion = null)
    {
        _configurationStore = configurationStore;
        _adapters = adapters;
        ViewModel = viewModel;
        _bindingsFilePathProvider = bindingsFilePathProvider;
        _mutationStarted = mutationStarted;
        _mutationFailed = mutationFailed;
        _reopenRoomAsync = reopenRoomAsync;
        _onProjectionUpdated = onProjectionUpdated;
        _daemonUrl = daemonUrl;
        _spawnDaemonOnDemand = spawnDaemonOnDemand;
        _clientVersion = clientVersion;
    }

    /// <summary>Points the session at <paramref name="roomDirectoryPath"/> without loading — <c>OpenAsync</c>'s bookkeeping half; the load itself goes through <see cref="LoadAsync"/>.</summary>
    public void SetCurrentRoomDirectory(string? roomDirectoryPath) => CurrentRoomDirectoryPath = roomDirectoryPath;

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Decides whether an incoming projection push for <paramref name="incomingDirectoryPath"/>
    /// should be applied to this client's state, seeding <see cref="CurrentRoomDirectoryPath"/>
    /// from the first push a fresh client ever sees (typically whatever Aer.Daemon last had
    /// open) and rejecting every later push for a different directory. Before this (issue #262
    /// follow-up), every push was applied unconditionally, so one client opening a different
    /// room silently corrupted every other connected client's view with that room's data,
    /// mislabeled under whatever directory the victim client had open. Extracted from
    /// <see cref="ReceiveWebSocketDataAsync"/> so this decision is unit-testable without a live
    /// daemon connection.
    /// </summary>
    internal bool ShouldApplyProjectionPush(string? incomingDirectoryPath)
    {
        CurrentRoomDirectoryPath ??= incomingDirectoryPath;
        return incomingDirectoryPath == CurrentRoomDirectoryPath;
    }

    private void UpdateProjection(RoomProjection projection)
    {
        if (CurrentRoomDirectoryPath != null)
        {
            RebuildPausedSteps(projection, CurrentRoomDirectoryPath);
            RebuildRunningExecutions(projection, CurrentRoomDirectoryPath);
            // A fresh reading here, not the one LoadAsync took: this path is also the WS push, which
            // can land long after a load. A stopped room emits no pushes at all, so this probe runs
            // only for a room something is actually doing something to.
            RefreshRoomStoppedCard(
                projection, ConcurrencyGuard.IsHeld(CurrentRoomDirectoryPath),
                ConcurrencySlotGate.IsWaiting(CurrentRoomDirectoryPath), CurrentRoomDirectoryPath);
            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            LastSnapshot = projection.Snapshot;
        }
    }

    /// <summary>
    /// #1299 (#480, Fable's ruling): sets or clears the waiting-on-lock banner from the SAME
    /// canonical derivation every other surface reads — never a second probe-driven state machine.
    /// The holder description + acquired-at are still a local filesystem read
    /// (<see cref="ConcurrencyGuard.ReadHolderInfo"/>), taken only when <paramref name="status"/> is
    /// already <see cref="RoomCardStatus.WaitingOnLock"/>, so a released lock clears the banner on
    /// the next refresh the same way <see cref="RefreshRoomStoppedCard"/>'s sibling probe does.
    /// </summary>
    private void RefreshWaitingOnLockBanner(string roomDirectoryPath, RoomCardStatus status)
    {
        if (status == RoomCardStatus.WaitingOnLock)
        {
            var (holderDescription, acquiredAtUtc) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
            ViewModel.WaitingOnLockBanner = new WaitingOnLockBannerViewModel(holderDescription, acquiredAtUtc, () => LoadAsync(roomDirectoryPath));
        }
        else
        {
            ViewModel.WaitingOnLockBanner = null;
        }
    }

    /// <summary>
    /// The active-attempt path: a mutation (Run, Decide, Cancel) was just refused because another
    /// process holds the lock right now. This is real-time feedback on the attempt just made, not a
    /// re-derivation of the room's steady-state status — the projection reflecting that status may
    /// not even have been reloaded yet. Kept as a direct probe for that reason.
    /// </summary>
    private void SetWaitingOnLockBannerFromActiveRefusal(string roomDirectoryPath, string? holderDescription, DateTime? acquiredAtUtc)
    {
        ViewModel.WaitingOnLockBanner = new WaitingOnLockBannerViewModel(holderDescription, acquiredAtUtc, () => LoadAsync(roomDirectoryPath));
    }

    /// <summary>
    /// #1215: what the open room's transcript should offer, or null if it should offer nothing.
    /// <para>
    /// #1219 turned this from a second state machine into a <em>rendering</em> of the first, which is
    /// 0020's rule 1 ("a surface may map a state to a mark, a word, a colour, or a layout; it may not
    /// decide the state"). The first version asked its own questions of <see cref="FlowState"/> and
    /// reached its own answer, and it disagreed with the switcher — which called a room this said had
    /// stopped "Working — …", with a spinner. There is one derivation,
    /// <see cref="RoomCardViewModel.DeriveStatus"/>, and this maps three of its states onto an offer. Every
    /// other state offers nothing: a room that is working needs no button, and a room that needs you
    /// already has your action on screen.
    /// </para>
    /// </summary>
    internal static RoomStoppedReason? DeriveRoomStoppedReason(
        RoomProjection projection, bool isFlowLockHeld, bool isWaitingToStart)
    {
        ArgumentNullException.ThrowIfNull(projection);

        // #1296 (second-reader finding): a fresh room's first turn, queued behind the concurrency
        // cap, is WorkflowStatus.Running with no lock held and no paused/failed steps -- exactly the
        // shape DeriveStatus's Stopped arm otherwise matches. Without threading the real signal here
        // that room rendered "This room stopped mid-run" with a Resume offer while it was actually
        // about to auto-dispatch on its own.
        var (_, status) = RoomCardViewModel.DeriveStatus(
            projection, projection.PendingPermission, isFlowLockHeld, isWaitingToStart);

        return status switch
        {
            RoomCardStatus.Stopped => RoomStoppedReason.StoppedMidRun,
            RoomCardStatus.Finished => RoomStoppedReason.Finished,
            // #1215 mapped every Terminal room to the "finished" offer, which is what the retired Run
            // button did too. Reading the canonical status instead splits that apart, and two of the
            // pieces need saying: a Failed room is left to #617's failed-step banner, which says what
            // broke and offers the worker that broke it — strictly more than "run it again" — while a
            // Cancelled room would otherwise be left with no way back at all, since it has no failed
            // step for that banner to attach to. It gets its own offer rather than being told it
            // "finished", which is precisely the sentence #461 existed to delete.
            RoomCardStatus.Cancelled => RoomStoppedReason.Cancelled,
            _ => null,
        };
    }

    /// <summary>
    /// Sets or clears <see cref="MainWindowViewModel.RoomStoppedCard"/> from the projection plus the
    /// lock reading <see cref="LoadAsync"/> took. Mode-independent for the same reason
    /// <see cref="RefreshWaitingOnLockBanner"/> is: the §15 lock is a local filesystem fact, and which
    /// process answered the load does not change who holds the directory.
    /// <para>
    /// <b>This runs on the live-refresh tick</b> — roughly every 2s for as long as a non-terminal room
    /// is open, because <c>MainWindow</c>'s timer calls <c>RefreshAsync</c> which calls
    /// <see cref="LoadAsync"/>. A second reader refuted the first version of this comment, which
    /// claimed the opposite. That is accepted rather than worked around: the probe is one local file
    /// open/close, #618's banner has been paying it on this exact path since long before this card
    /// existed, and the reading is now taken <em>once</em> in <see cref="LoadAsync"/> and shared, so
    /// this card costs nothing on top of what was already there. It also means both surfaces answer
    /// from the same instant rather than from two probes a few statements apart.
    /// </para>
    /// </summary>
    private void RefreshRoomStoppedCard(RoomProjection projection, bool isFlowLockHeld, bool isWaitingToStart, string roomDirectoryPath)
    {
        var reason = DeriveRoomStoppedReason(projection, isFlowLockHeld, isWaitingToStart);

        ViewModel.RoomStoppedCard = reason is { } stoppedReason
            ? new RoomStoppedCardViewModel(stoppedReason, () => ViewModel.RequestRoomRunAsync())
            : null;

        // #1299: same reading DeriveRoomStoppedReason already took, called again rather than
        // threaded through it — DeriveRoomStoppedReason stays a small, directly-tested pure
        // function (RoomStoppedCardTests), and deriving twice over already-in-memory data costs
        // nothing next to the I/O RefreshWaitingOnLockBanner does only when it actually applies.
        var (_, status) = RoomCardViewModel.DeriveStatus(projection, projection.PendingPermission, isFlowLockHeld, isWaitingToStart);
        RefreshWaitingOnLockBanner(roomDirectoryPath, status);
    }

    /// <summary>
    /// Loads <paramref name="roomDirectoryPath"/> through <see cref="RoomProjectionLoader"/> and
    /// rebuilds the ViewModel's mutation surfaces (<see cref="MainWindowViewModel.PausedSteps"/>,
    /// <see cref="MainWindowViewModel.RunningExecutions"/>) from the projected facts.
    /// </summary>
    public async Task<LoadOutcome> LoadAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        // #1299: the waiting-on-lock banner used to be set here, speculatively, from a raw lock
        // probe alone — before any projection existed. That predates the state's promotion into
        // RoomCardViewModel.DeriveStatus, which needs the projection to tell a genuinely-running
        // room's own turn apart from a foreign holder. Both branches below reach a projection and
        // call RefreshRoomStoppedCard, which now derives and renders this banner from that one
        // canonical status — so nothing here needs to probe ahead of it.
        var isFlowLockHeld = ConcurrencyGuard.IsHeld(roomDirectoryPath);

        await RefreshRoomTurnHostBannerAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);

        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/rooms/open", new OpenRoomRequest(roomDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    var projection = await response.Content.ReadFromJsonAsync<RoomProjection>(DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
                    if (projection != null)
                    {
                        UpdateProjection(projection);
                        return new LoadOutcome(projection, null);
                    }
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    return new LoadOutcome(null, err);
                }
            }
            catch (Exception ex)
            {
                return new LoadOutcome(null, ex.Message);
            }
        }

        // In-process fallback
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);

            RebuildPausedSteps(projection, roomDirectoryPath);
            RebuildRunningExecutions(projection, roomDirectoryPath);
            RefreshRoomStoppedCard(projection, isFlowLockHeld, ConcurrencySlotGate.IsWaiting(roomDirectoryPath), roomDirectoryPath);

            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            LastSnapshot = projection.Snapshot;
            return new LoadOutcome(projection, null);
        }
        catch (AerFlowException ex)
        {
            ViewModel.PausedSteps.Clear();
            ViewModel.DecisionStatusText = string.Empty;
            ViewModel.RunningExecutions.Clear();
            ViewModel.CancelStatusText = string.Empty;
            // No projection means nothing is known about whether this room is stopped; an offer left
            // over from the last room that did load would be an offer against the wrong directory.
            ViewModel.RoomStoppedCard = null;

            if (ex is WorkflowLockedException wle)
            {
                SetWaitingOnLockBannerFromActiveRefusal(roomDirectoryPath, wle.HolderDescription, wle.AcquiredAtUtc);
            }
            else if (ConcurrencyGuard.IsHeld(roomDirectoryPath))
            {
                // #1299: a directory that fails to load for a DIFFERENT reason (no snapshot.json
                // yet — InvalidRoomDirectoryException, the common case for a room whose first turn
                // never ran) can still be externally locked at the same time. There is no projection
                // for DeriveStatus to read here, so this stays the direct probe RefreshWaitingOnLockBanner
                // normally replaces — the one case where "no projection" does not mean "nothing to say
                // about the lock". Re-probed here rather than trusting isFlowLockHeld (captured before
                // the load, which can take a while) — a lock released in between must not show a
                // banner for a hold that is already gone (second-reader finding).
                var (holderDescription, acquiredAtUtc) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
                SetWaitingOnLockBannerFromActiveRefusal(roomDirectoryPath, holderDescription, acquiredAtUtc);
            }
            else
            {
                ViewModel.WaitingOnLockBanner = null;
            }

            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new LoadOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// The Run mutation: dispatches the Run command to the daemon or executes in-process as fallback.
    /// </summary>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — forwarded to <see cref="RunCommand.ExecuteAsync"/>'s
    /// own same-named parameter, and therefore only takes effect on the in-process fallback path
    /// below (a delegate can't cross the HTTP call to a real remote daemon). <c>Aer.Daemon</c>'s own
    /// <see cref="RoomClient"/> singleton always takes that fallback path (it has no daemon of its
    /// own to delegate to), which is exactly the case that needs this.
    /// </param>
    /// <summary>
    /// #1184: the settle-instead-of-park choice does not cross the daemon's HTTP boundary — see the
    /// remark on <see cref="RunRoomRequest"/> for why it must not. Every attended caller today is
    /// the daemon's own session turn, which runs the pump in-process, so a caller asking for it on
    /// the remote branch is asking for something that would not happen. Refuse loudly instead of
    /// running the turn with the opposite retry behaviour and reporting success.
    /// </summary>
    private static void RefuseRemoteSettleOnVendorExhaustion(bool settleOnVendorExhaustion)
    {
        if (settleOnVendorExhaustion)
        {
            throw new NotSupportedException(
                "settleOnVendorExhaustion has no remote form: the attended/unattended split (0026 §4) "
                + "is decided at the in-process pump, and a daemon on the far side of HTTP cannot tell "
                + "whether an operator is waiting on this run.");
        }
    }

    public async Task<MutationOutcome> RunAsync(
        string roomDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null,
        bool settleOnVendorExhaustion = false)
    {
        CurrentRoomDirectoryPath = roomDirectoryPath;

        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            RefuseRemoteSettleOnVendorExhaustion(settleOnVendorExhaustion);
            try
            {
                var request = new RunRoomRequest(roomDirectoryPath, workflowTemplateFilePath, bindingsFilePath);
                ViewModel.IsMutationInFlight = true;
                ViewModel.RunStatusText = "Running…";
                _mutationStarted();

                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/rooms/run", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    ViewModel.RunStatusText = string.Empty;
                    ViewModel.IsMutationInFlight = false;

                    if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
                    {
                        await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
                    }
                    await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
                    await RecordRoomPathMetadataAsync(roomDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken).ConfigureAwait(true);
                    await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
                    return new MutationOutcome(null);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    _mutationFailed();
                    // The daemon's locked refusal is a plain string with no holder in it — the
                    // local probe is what turns it into the waiting-on-lock state (#618).
                    var (activeRefusalHolder, activeRefusalAcquiredAt) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
                    SetWaitingOnLockBannerFromActiveRefusal(roomDirectoryPath, activeRefusalHolder, activeRefusalAcquiredAt);
                    ViewModel.RunStatusText = err;
                    ViewModel.IsMutationInFlight = false;
                    return new MutationOutcome(err);
                }
            }
            catch (Exception ex)
            {
                _mutationFailed();
                ViewModel.RunStatusText = ex.Message;
                ViewModel.IsMutationInFlight = false;
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        var options = new RunOptions(
            string.IsNullOrWhiteSpace(workflowTemplateFilePath) ? null : workflowTemplateFilePath,
            bindingsFilePath,
            roomDirectoryPath,
            SettleOnVendorExhaustion: settleOnVendorExhaustion);

        ViewModel.IsMutationInFlight = true;
        ViewModel.RunStatusText = "Running…";
        _mutationStarted();

        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostedRun = RegisterHostedRun(roomDirectoryPath, inFlightExecutions, hostStopSource);

        try
        {
            var pumpTask = Task.Run(
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token, onWorkerStdoutLine), hostStopSource.Token);
            hostedRun.PumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.RunStatusText = string.Empty;

            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
            }

            await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
            await RecordRoomPathMetadataAsync(roomDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken).ConfigureAwait(true);
        }
        catch (AerFlowException ex)
        {
            _mutationFailed();
            if (ex is WorkflowLockedException wle)
            {
                ViewModel.RunStatusText = string.Empty;
                SetWaitingOnLockBannerFromActiveRefusal(roomDirectoryPath, wle.HolderDescription, wle.AcquiredAtUtc);
            }
            else
            {
                ViewModel.RunStatusText = ex.Message;
            }

            // #330: a failed pump used to return here without ever calling _reopenRoomAsync, so
            // Aer.Daemon's own wiring of that hook (reopenRoomAsync -> BroadcastStateAsync,
            // Program.cs) never fired for this run at all -- a connected phone watching this
            // directory saw nothing, permanently, instead of learning the run stopped.
            await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            ReleaseHostedRun(roomDirectoryPath, hostedRun);
            hostStopSource.Dispose();
        }

        await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    private static async Task RecordRoomPathMetadataAsync(string roomDirectoryPath, string? workflowTemplateFilePath, string? bindingsFilePath, CancellationToken cancellationToken)
    {
        try
        {
            var aerDir = Path.Combine(roomDirectoryPath, ".aer");
            Directory.CreateDirectory(aerDir);
            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await File.WriteAllTextAsync(Path.Combine(aerDir, "workflow-path"), workflowTemplateFilePath, cancellationToken).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(bindingsFilePath))
            {
                await File.WriteAllTextAsync(Path.Combine(aerDir, "bindings-path"), bindingsFilePath, cancellationToken).ConfigureAwait(false); // vocabulary-ok: technical file path
            }
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// The paused-step decision mutation: dispatches the Decide command to the daemon or executes in-process.
    /// </summary>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — see <see cref="RunAsync"/>'s remarks on the same
    /// parameter; identical in-process-fallback-only behavior applies here.
    /// </param>
    public async Task<MutationOutcome> DecideAsync(
        string roomDirectoryPath,
        StepId stepId,
        ExecutionId executionId,
        DecisionType decisionType,
        StepId? targetStepId,
        string? revisionFilePath,
        string? supplementaryWorker,
        string? supplementaryOutputName,
        CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null,
        bool settleOnVendorExhaustion = false)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            RefuseRemoteSettleOnVendorExhaustion(settleOnVendorExhaustion);
            try
            {
                ViewModel.DecisionStatusText = $"Deciding {stepId.Value}…";
                ViewModel.IsMutationInFlight = true;
                _mutationStarted();

                // Decision 0056 (#1246): send _bindingsFilePathProvider() unconditionally so the daemon can heal un-bound rooms.
                // 0056 rules that the field is never consulted for a room that knows its workers, so the condition lives on the daemon side, once.
                var request = new DecideRoomRequest(
                    roomDirectoryPath,
                    stepId.Value,
                    executionId.Value,
                    decisionType,
                    targetStepId?.Value,
                    revisionFilePath,
                    supplementaryWorker,
                    supplementaryOutputName,
                    BindingsFilePath: _bindingsFilePathProvider());

                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/rooms/decide", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    ViewModel.DecisionStatusText = string.Empty;
                    ViewModel.IsMutationInFlight = false;
                    ViewModel.Rooms.RetireInboxItem(roomDirectoryPath, stepId, executionId);
                    await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
                    return new MutationOutcome(null);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    _mutationFailed();
                    // Same reason as RunAsync's daemon-refusal arm: the string carries no holder;
                    // the local probe renders the state (#618).
                    var (activeRefusalHolder, activeRefusalAcquiredAt) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
                    SetWaitingOnLockBannerFromActiveRefusal(roomDirectoryPath, activeRefusalHolder, activeRefusalAcquiredAt);
                    ViewModel.DecisionStatusText = err;
                    ViewModel.IsMutationInFlight = false;
                    return new MutationOutcome(err);
                }
            }
            catch (Exception ex)
            {
                _mutationFailed();
                ViewModel.DecisionStatusText = ex.Message;
                ViewModel.IsMutationInFlight = false;
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        ViewModel.DecisionStatusText = $"Deciding {stepId.Value}…";
        ViewModel.IsMutationInFlight = true;
        _mutationStarted();

        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostedRun = RegisterHostedRun(roomDirectoryPath, inFlightExecutions, hostStopSource);

        try
        {
            ExecutionId? supplementaryExecutionId = null;

            // #1230's second reader: read the provider ONCE. It is one process-wide mutable slot, and
            // the supply step below awaits — so reading it again afterwards could pick up a different
            // room's path written in that window by an unrelated open, session start or chat turn,
            // none of which hold this room's turn lock. One read means the supply and the decision
            // cannot disagree about whose workers they are, whatever else moves meanwhile.
            var bindingsFilePath = _bindingsFilePathProvider() ?? string.Empty;

            if (revisionFilePath is not null)
            {
                var supplyOptions = new SupplyOptions(
                    roomDirectoryPath,
                    supplementaryWorker ?? string.Empty,
                    supplementaryOutputName ?? string.Empty,
                    revisionFilePath,
                    bindingsFilePath);

                var supplyResult = await Task.Run(() => SupplyCommand.ExecuteAsync(supplyOptions, _adapters, hostStopSource.Token), hostStopSource.Token)
                    .ConfigureAwait(true);

                supplementaryExecutionId = supplyResult.ExecutionId;
            }

            var options = new DecideOptions(
                roomDirectoryPath,
                executionId.Value,
                decisionType,
                targetStepId,
                supplementaryExecutionId?.Value,
                bindingsFilePath,
                SettleOnVendorExhaustion: settleOnVendorExhaustion);

            var pumpTask = Task.Run(
                () => DecideCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token, onWorkerStdoutLine), hostStopSource.Token);
            hostedRun.PumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            ViewModel.DecisionStatusText = string.Empty;
        }
        catch (Exception ex) when (ex is AerFlowException or FileNotFoundException)
        {
            _mutationFailed();
            if (ex is WorkflowLockedException wle)
            {
                ViewModel.DecisionStatusText = string.Empty;
                SetWaitingOnLockBannerFromActiveRefusal(roomDirectoryPath, wle.HolderDescription, wle.AcquiredAtUtc);
            }
            else
            {
                ViewModel.DecisionStatusText = ex.Message;
            }
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
            ReleaseHostedRun(roomDirectoryPath, hostedRun);
            hostStopSource.Dispose();
        }

        ViewModel.Rooms.RetireInboxItem(roomDirectoryPath, stepId, executionId);
        await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// The targeted-Cancel surface: cancels via daemon or executes in-process.
    /// </summary>
    public async Task<MutationOutcome> CancelExecutionAsync(
        string roomDirectoryPath, ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                ViewModel.CancelStatusText = $"Cancelling {executionId.Value}…";
                ViewModel.IsMutationInFlight = true;
                _mutationStarted();

                var request = new CancelRoomRequest(roomDirectoryPath, executionId.Value);
                var response = await _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/rooms/cancel", request, cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    ViewModel.CancelStatusText = string.Empty;
                    ViewModel.IsMutationInFlight = false;
                    await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
                    return new MutationOutcome(null);
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
                    _mutationFailed();
                    ViewModel.CancelStatusText = err;
                    ViewModel.IsMutationInFlight = false;
                    return new MutationOutcome(err);
                }
            }
            catch (Exception ex)
            {
                _mutationFailed();
                ViewModel.CancelStatusText = ex.Message;
                ViewModel.IsMutationInFlight = false;
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback. Resolved by the directory the caller named, never by whichever
        // session happens to be "current" (#335): with two runs in flight the current one is
        // simply the more recent, so cancelling the other used to miss its live registry entirely
        // and fall through to the out-of-process path below.
        if (HostedRunFor(roomDirectoryPath) is { } hostedRun)
        {
            await hostedRun.InFlightExecutions.RequestCancellationAsync(executionId, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }

        ViewModel.CancelStatusText = $"Cancelling {executionId.Value}…";
        ViewModel.IsMutationInFlight = true;
        _mutationStarted();

        try
        {
            var options = new CancelOptions(roomDirectoryPath, executionId.Value, _bindingsFilePathProvider() ?? string.Empty);
            await Task.Run(() => CancelCommand.ExecuteAsync(options, _adapters, cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(true);

            ViewModel.CancelStatusText = string.Empty;
        }
        catch (AerFlowException ex)
        {
            _mutationFailed();
            ViewModel.CancelStatusText = ex.Message;
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ViewModel.IsMutationInFlight = false;
        }

        await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// Stops <b>every</b> pump this process is hosting — the desktop's Stop button and window-close
    /// handler, where there is only ever one, and daemon shutdown, where stopping all is the intent.
    /// To stop one named session, use <see cref="RequestHostStop(string)"/>.
    /// </summary>
    public void RequestHostStop()
    {
        if (_isClientMode && CurrentRoomDirectoryPath != null && _activeDaemonUrl != null)
        {
            _ = _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/rooms/cancel", new CancelRoomRequest(CurrentRoomDirectoryPath, null));
            return;
        }

        foreach (var hostedRun in _hostedRuns.Values)
        {
            hostedRun.HostStopSource.Cancel();
        }
    }

    /// <summary>
    /// Stops the pump hosting <paramref name="roomDirectoryPath"/>, leaving every other hosted
    /// session running. Returns whether one was found.
    /// </summary>
    /// <remarks>
    /// The parameterless overload used to cancel a single shared token source, which with two runs
    /// in flight was whichever started last — so a daemon client asking to stop session A stopped
    /// session B and left A running (#335). Any caller that knows which session it means must call
    /// this one.
    /// </remarks>
    public bool RequestHostStop(string roomDirectoryPath)
    {
        if (_isClientMode && _activeDaemonUrl != null)
        {
            _ = _httpClient.PostAsJsonAsync($"{_activeDaemonUrl}/api/rooms/cancel", new CancelRoomRequest(roomDirectoryPath, null));
            return true;
        }

        if (HostedRunFor(roomDirectoryPath) is not { } hostedRun)
        {
            return false;
        }

        hostedRun.HostStopSource.Cancel();
        return true;
    }

    /// <summary>
    /// #350: was an unconditional <c>Clear()</c> + re-add every 2-second live-refresh tick, whether
    /// or not anything changed. <c>ObservableCollection.Clear()</c> raises <c>Reset</c>, so Avalonia
    /// tore down and rebuilt every item container twice a tick — killing hover, keyboard focus, text
    /// selection, and occasionally swallowing a click. Worse for this collection specifically:
    /// <see cref="PausedStepViewModel.RevisionFilePath"/>/<c>SupplementaryWorker</c>/
    /// <c>SupplementaryOutputName</c> are operator-typed text fields a rebuild silently wiped
    /// mid-entry. Reconciled by (<see cref="StepId"/>, <see cref="ExecutionId"/>) instead: an
    /// unchanged pause point keeps its existing instance (and whatever the operator has typed into
    /// it) untouched; only genuinely new/departed pause points touch the collection at all.
    /// <see cref="PausedStepViewModel.IsEnabled"/> is not re-synced here — <c>OnIsMutationInFlightChanged</c>
    /// already keeps every live instance's <c>IsEnabled</c> current independent of this rebuild.
    /// </summary>
    private void RebuildPausedSteps(RoomProjection projection, string roomDirectoryPath)
    {
        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        var desiredKeys = new HashSet<(StepId StepId, ExecutionId ExecutionId)>();
        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Paused || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            desiredKeys.Add((stepState.StepId, executionId));
        }

        foreach (var stale in ViewModel.PausedSteps.Where(step => !desiredKeys.Contains((step.StepId, step.ExecutionId))).ToList())
        {
            ViewModel.PausedSteps.Remove(stale);
        }

        var existingKeys = ViewModel.PausedSteps.Select(step => (step.StepId, step.ExecutionId)).ToHashSet();
        foreach (var (stepId, executionId) in desiredKeys)
        {
            if (existingKeys.Contains((stepId, executionId)))
            {
                continue;
            }

            var supersedeTargets = stepDefinitionByStepId[stepId].PausePoint!.SupersedeTargets;

            ViewModel.PausedSteps.Add(new PausedStepViewModel(
                stepId,
                executionId,
                supersedeTargets,
                (decidedStepId, decidedExecutionId, decisionType, targetStepId, revisionFilePath, supplementaryWorker, supplementaryOutputName) =>
                    DecideAsync(
                        roomDirectoryPath, decidedStepId, decidedExecutionId, decisionType, targetStepId,
                        revisionFilePath, supplementaryWorker, supplementaryOutputName))
            {
                IsEnabled = !ViewModel.IsMutationInFlight,
            });
        }

        ViewModel.Chat.SyncPendingDecisions(ViewModel.PausedSteps);
    }

    /// <summary>#350: same reconciliation as <see cref="RebuildPausedSteps"/>, and for the same reason — see its remarks.</summary>
    private void RebuildRunningExecutions(RoomProjection projection, string roomDirectoryPath)
    {
        var isLocallyHostedRoom = HostedRunFor(roomDirectoryPath) is not null;

        var desired = new List<(StepId? StepId, ExecutionId ExecutionId, bool IsLocallyHosted)>();
        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            desired.Add((stepState.StepId, executionId, isLocallyHostedRoom || _isClientMode));
        }

        foreach (var stepLessExecution in projection.State.StepLessExecutions)
        {
            desired.Add((null, stepLessExecution.ExecutionId, false));
        }

        var desiredKeys = desired.Select(d => (d.StepId, d.ExecutionId)).ToHashSet();
        foreach (var stale in ViewModel.RunningExecutions.Where(e => !desiredKeys.Contains((e.StepId, e.ExecutionId))).ToList())
        {
            ViewModel.RunningExecutions.Remove(stale);
        }

        var existingByKey = ViewModel.RunningExecutions.ToDictionary(e => (e.StepId, e.ExecutionId));
        foreach (var (stepId, executionId, isLocallyHosted) in desired)
        {
            if (existingByKey.TryGetValue((stepId, executionId), out var existing))
            {
                existing.CancellationRequested = projection.State.CancellationRequestedExecutionIds.Contains(executionId);
                continue;
            }

            AddRunningExecution(stepId, executionId, isLocallyHosted, projection.State, roomDirectoryPath);
        }
    }

    private void AddRunningExecution(
        StepId? stepId, ExecutionId executionId, bool isLocallyHosted, FlowState state, string roomDirectoryPath)
    {
        var cancellationRequested = state.CancellationRequestedExecutionIds.Contains(executionId);

        ViewModel.RunningExecutions.Add(new RunningExecutionViewModel(
            stepId,
            executionId,
            isLocallyHosted,
            cancellationRequested,
            targetExecutionId => CancelExecutionAsync(roomDirectoryPath, targetExecutionId))
        {
            IsEnabled = isLocallyHosted || !ViewModel.IsMutationInFlight,
        });
    }
}
