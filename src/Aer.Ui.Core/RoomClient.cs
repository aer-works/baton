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
    ArtifactReference? ArtifactReference = null);

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

    /// <summary>The outcome one template load produces — <see cref="LoadOutcome"/>'s counterpart for a raw, not-yet-instantiated template file (M14 Phase 3).</summary>
    public sealed record TemplateLoadOutcome(Aer.Flow.Domain.WorkflowDefinition? Definition, string? ErrorMessage);

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
        bool spawnDaemonOnDemand = true)
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
            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            LastSnapshot = projection.Snapshot;
        }
    }

    /// <summary>
    /// #618: sets or clears the waiting-on-lock banner from a local probe. Mode-independent by
    /// design — <see cref="ConcurrencyGuard.IsHeld"/> and the holder sidecar are filesystem facts
    /// the desktop can read whether the daemon or this process answers the load, and clearing on
    /// the not-held arm is what stops a released lock leaving a stale banner behind.
    /// </summary>
    private void RefreshWaitingOnLockBanner(string roomDirectoryPath)
    {
        if (ConcurrencyGuard.IsHeld(roomDirectoryPath))
        {
            var (holderDescription, _) = ConcurrencyGuard.ReadHolderInfo(roomDirectoryPath);
            ViewModel.WaitingOnLockBanner = new WaitingOnLockBannerViewModel(holderDescription, () => LoadAsync(roomDirectoryPath));
        }
        else
        {
            ViewModel.WaitingOnLockBanner = null;
        }
    }

    /// <summary>
    /// Loads <paramref name="roomDirectoryPath"/> through <see cref="RoomProjectionLoader"/> and
    /// rebuilds the ViewModel's mutation surfaces (<see cref="MainWindowViewModel.PausedSteps"/>,
    /// <see cref="MainWindowViewModel.RunningExecutions"/>) from the projected facts.
    /// </summary>
    public async Task<LoadOutcome> LoadAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        // Before the mode branch, deliberately: the second reader found the first draft probed
        // only in the in-process fallback, so the primary (daemon-connected) desktop never showed
        // the waiting-on-lock state at all — and the daemon's own locked answer is a plain string
        // this method's caller drops. The lock is a local filesystem fact the desktop can read in
        // either mode; which process answers the load does not change who holds the directory.
        RefreshWaitingOnLockBanner(roomDirectoryPath);
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

            if (ex is WorkflowLockedException wle)
            {
                ViewModel.WaitingOnLockBanner = new WaitingOnLockBannerViewModel(wle.HolderDescription, () => LoadAsync(roomDirectoryPath));
            }

            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new LoadOutcome(null, ex.Message);
        }
    }

    /// <summary>
    /// Loads a raw template file and clears the mutation surfaces.
    /// </summary>
    public async Task<TemplateLoadOutcome> LoadTemplateAsync(string templateFilePath, CancellationToken cancellationToken = default)
    {
        ViewModel.PausedSteps.Clear();
        ViewModel.DecisionStatusText = string.Empty;
        ViewModel.RunningExecutions.Clear();
        ViewModel.CancelStatusText = string.Empty;

        try
        {
            var definition = await TemplateProjectionLoader.LoadAsync(templateFilePath, cancellationToken).ConfigureAwait(true);

            LastLoadSucceeded = true;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new TemplateLoadOutcome(definition, null);
        }
        catch (AerFlowException ex)
        {
            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            LastSnapshot = null;
            return new TemplateLoadOutcome(null, ex.Message);
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
                    RefreshWaitingOnLockBanner(roomDirectoryPath);
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
                ViewModel.WaitingOnLockBanner = new WaitingOnLockBannerViewModel(wle.HolderDescription, () => LoadAsync(roomDirectoryPath));
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

                var request = new DecideRoomRequest(
                    roomDirectoryPath,
                    stepId.Value,
                    executionId.Value,
                    decisionType,
                    targetStepId?.Value,
                    revisionFilePath,
                    supplementaryWorker,
                    supplementaryOutputName);

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
                    RefreshWaitingOnLockBanner(roomDirectoryPath);
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

            if (revisionFilePath is not null)
            {
                var supplyOptions = new SupplyOptions(
                    roomDirectoryPath,
                    supplementaryWorker ?? string.Empty,
                    supplementaryOutputName ?? string.Empty,
                    revisionFilePath,
                    _bindingsFilePathProvider() ?? string.Empty);

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
                _bindingsFilePathProvider() ?? string.Empty,
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
                ViewModel.WaitingOnLockBanner = new WaitingOnLockBannerViewModel(wle.HolderDescription, () => LoadAsync(roomDirectoryPath));
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

    private void RebuildPausedSteps(RoomProjection projection, string roomDirectoryPath)
    {
        ViewModel.PausedSteps.Clear();

        var stepDefinitionByStepId = projection.Snapshot.Steps.ToDictionary(step => step.StepId);

        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Paused || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            var supersedeTargets = stepDefinitionByStepId[stepState.StepId].PausePoint!.SupersedeTargets;

            ViewModel.PausedSteps.Add(new PausedStepViewModel(
                stepState.StepId,
                executionId,
                supersedeTargets,
                (stepId, decidedExecutionId, decisionType, targetStepId, revisionFilePath, supplementaryWorker, supplementaryOutputName) =>
                    DecideAsync(
                        roomDirectoryPath, stepId, decidedExecutionId, decisionType, targetStepId,
                        revisionFilePath, supplementaryWorker, supplementaryOutputName))
            {
                IsEnabled = !ViewModel.IsMutationInFlight,
            });
        }

        ViewModel.Chat.SyncPendingDecisions(ViewModel.PausedSteps);
    }

    private void RebuildRunningExecutions(RoomProjection projection, string roomDirectoryPath)
    {
        ViewModel.RunningExecutions.Clear();

        var isLocallyHostedRoom = HostedRunFor(roomDirectoryPath) is not null;

        foreach (var stepState in projection.State.Steps)
        {
            if (stepState.Status != StepStatus.Running || stepState.LatestExecutionId is not { } executionId)
            {
                continue;
            }

            AddRunningExecution(stepState.StepId, executionId, isLocallyHostedRoom || _isClientMode, projection.State, roomDirectoryPath);
        }

        foreach (var stepLessExecution in projection.State.StepLessExecutions)
        {
            AddRunningExecution(stepId: null, stepLessExecution.ExecutionId, isLocallyHosted: false, projection.State, roomDirectoryPath);
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
