using System.Collections.Concurrent;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.RoomSession;

public record OpenRoomRequest(string DirectoryPath);
// #1184: deliberately no settle-on-exhaustion field — RunAsync takes it as a direct parameter for
// the one in-process caller (the daemon's own session turn); the wire request never carries it,
// since nothing on the far side of an HTTP POST can tell whether an operator is attended (0026 §4).
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

/// <summary>
/// Issue #1412 Part 1: the daemon's own room orchestration, extracted from
/// <c>Aer.Ui.Core.RoomClient</c> — specifically from the in-process-fallback branches that class's
/// own <c>EnsureDaemonConnectedAsync</c> gate always takes for the daemon's singleton instance
/// (that gate needs a configured <c>daemonUrl</c>, which <c>Aer.Daemon</c>'s own construction never
/// passes). No HTTP client-mode branch, no WebSocket listening, no ViewModel: this is exactly the
/// surface <c>Aer.Daemon</c>'s ~35 call sites actually exercise. <c>Aer.Ui.Core.RoomClient</c> and
/// its <c>Connection</c>/<c>Fleet</c>/<c>Sessions</c>/<c>Remote</c>/<c>Permissions</c>/<c>TurnHost</c>
/// siblings are untouched by this extraction — they keep serving <c>Aer.Ui</c> until #1396 retires it.
/// </summary>
public sealed class RoomClient
{
    /// <summary>The outcome one load produces: exactly one of the two is non-null (§3's honest-error rule — an invalid directory is a rendered message, never a crash).</summary>
    public sealed record LoadOutcome(RoomProjection? Projection, string? ErrorMessage);

    /// <summary>Null on success; the in-window message otherwise.</summary>
    public sealed record MutationOutcome(string? ErrorMessage);

    private readonly LocalUiConfigurationStore _configurationStore;
    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters;
    private readonly Func<string?> _bindingsFilePathProvider;
    private readonly Func<string, CancellationToken, Task> _reopenRoomAsync;

    /// <summary>
    /// One in-flight pump this process is hosting, and everything needed to reach it: the
    /// caller-retained delivery point for a targeted cancel, the host-stop source that is the Ctrl+C
    /// equivalent <c>Aer.Cli</c> wires to <c>Console.CancelKeyPress</c>, and the pump task itself so a
    /// caller can wait for a durable fixed point rather than abandoning it mid-write.
    /// </summary>
    internal sealed class HostedRun(InFlightExecutionRegistry inFlightExecutions, CancellationTokenSource hostStopSource)
    {
        public InFlightExecutionRegistry InFlightExecutions { get; } = inFlightExecutions;

        public CancellationTokenSource HostStopSource { get; } = hostStopSource;

        public Task? PumpTask { get; set; }
    }

    /// <summary>Every pump this process is hosting, keyed by session directory (#335).</summary>
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

    /// <summary>Which room directory this instance is currently viewing — set only by this session's own actions.</summary>
    public string? CurrentRoomDirectoryPath { get; private set; }

    public bool LastLoadSucceeded { get; private set; }

    private WorkflowStatus? LastWorkflowStatus { get; set; }

    /// <summary>Whether the poller should keep observing: a successfully opened room that has not reached §12's terminal fixed point.</summary>
    public bool ShouldLiveRefresh => LastLoadSucceeded && LastWorkflowStatus != WorkflowStatus.Terminal;

    public RoomClient(
        LocalUiConfigurationStore configurationStore,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        Func<string?> bindingsFilePathProvider,
        Func<string, CancellationToken, Task> reopenRoomAsync)
    {
        _configurationStore = configurationStore;
        _adapters = adapters;
        _bindingsFilePathProvider = bindingsFilePathProvider;
        _reopenRoomAsync = reopenRoomAsync;
    }

    /// <summary>Points the session at <paramref name="roomDirectoryPath"/> without loading — <c>OpenAsync</c>'s bookkeeping half; the load itself goes through <see cref="LoadAsync"/>.</summary>
    public void SetCurrentRoomDirectory(string? roomDirectoryPath) => CurrentRoomDirectoryPath = roomDirectoryPath;

    /// <summary>Loads <paramref name="roomDirectoryPath"/> through <see cref="RoomProjectionLoader"/>.</summary>
    public async Task<LoadOutcome> LoadAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var projection = await RoomProjectionLoader.LoadAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);

            LastLoadSucceeded = true;
            LastWorkflowStatus = projection.State.Status;
            return new LoadOutcome(projection, null);
        }
        catch (AerFlowException ex)
        {
            LastLoadSucceeded = false;
            LastWorkflowStatus = null;
            return new LoadOutcome(null, ex.Message);
        }
    }

    /// <summary>The Run mutation: executes in-process.</summary>
    /// <param name="onWorkerStdoutLine">M24 Phase 1's live in-turn streaming, forwarded to <see cref="RunCommand.ExecuteAsync"/>.</param>
    public async Task<MutationOutcome> RunAsync(
        string roomDirectoryPath, string? workflowTemplateFilePath, string bindingsFilePath, CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null,
        bool settleOnVendorExhaustion = false)
    {
        CurrentRoomDirectoryPath = roomDirectoryPath;

        var options = new RunOptions(
            string.IsNullOrWhiteSpace(workflowTemplateFilePath) ? null : workflowTemplateFilePath,
            bindingsFilePath,
            roomDirectoryPath,
            SettleOnVendorExhaustion: settleOnVendorExhaustion);

        var inFlightExecutions = new InFlightExecutionRegistry();
        var hostStopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostedRun = RegisterHostedRun(roomDirectoryPath, inFlightExecutions, hostStopSource);

        try
        {
            var pumpTask = Task.Run(
                () => RunCommand.ExecuteAsync(options, _adapters, inFlightExecutions, hostStopSource.Token, onWorkerStdoutLine), hostStopSource.Token);
            hostedRun.PumpTask = pumpTask;
            await pumpTask.ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(workflowTemplateFilePath))
            {
                await _configurationStore.RecordWorkflowTemplateFilePathAsync(workflowTemplateFilePath, cancellationToken).ConfigureAwait(true);
            }

            await _configurationStore.RecordBindingsFilePathAsync(bindingsFilePath, cancellationToken).ConfigureAwait(true);
            await RecordRoomPathMetadataAsync(roomDirectoryPath, workflowTemplateFilePath, bindingsFilePath, cancellationToken).ConfigureAwait(true);
        }
        catch (AerFlowException ex)
        {
            // #330: a failed pump used to return here without ever calling _reopenRoomAsync, so
            // Aer.Daemon's own wiring of that hook (reopenRoomAsync -> BroadcastStateAsync,
            // Program.cs) never fired for this run at all -- a connected phone watching this
            // directory saw nothing, permanently, instead of learning the run stopped.
            await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(ex.Message);
        }
        finally
        {
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

    /// <summary>The paused-step decision mutation: executes in-process.</summary>
    /// <param name="onWorkerStdoutLine">M24 Phase 1's live in-turn streaming — see <see cref="RunAsync"/>'s remarks on the same parameter.</param>
    public async Task<MutationOutcome> DecideAsync(
        string roomDirectoryPath,
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
        }
        catch (Exception ex) when (ex is AerFlowException or FileNotFoundException)
        {
            return new MutationOutcome(ex.Message);
        }
        finally
        {
            ReleaseHostedRun(roomDirectoryPath, hostedRun);
            hostStopSource.Dispose();
        }

        await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>The targeted-Cancel surface: executes in-process.</summary>
    public async Task<MutationOutcome> CancelExecutionAsync(
        string roomDirectoryPath, ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        // Resolved by the directory the caller named, never by whichever session happens to be
        // "current" (#335): with two runs in flight the current one is simply the more recent, so
        // cancelling the other used to miss its live registry entirely and fall through to the
        // out-of-process path below.
        if (HostedRunFor(roomDirectoryPath) is { } hostedRun)
        {
            await hostedRun.InFlightExecutions.RequestCancellationAsync(executionId, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }

        try
        {
            var options = new CancelOptions(roomDirectoryPath, executionId.Value, _bindingsFilePathProvider() ?? string.Empty);
            await Task.Run(() => CancelCommand.ExecuteAsync(options, _adapters, cancellationToken: cancellationToken), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (AerFlowException ex)
        {
            return new MutationOutcome(ex.Message);
        }

        await _reopenRoomAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(true);
        return new MutationOutcome(null);
    }

    /// <summary>
    /// Stops the pump hosting <paramref name="roomDirectoryPath"/>, leaving every other hosted
    /// session running. Returns whether one was found.
    /// </summary>
    public bool RequestHostStop(string roomDirectoryPath)
    {
        if (HostedRunFor(roomDirectoryPath) is not { } hostedRun)
        {
            return false;
        }

        hostedRun.HostStopSource.Cancel();
        return true;
    }

    public Task RecordOpenedAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
        => _configurationStore.RecordOpenedAsync(roomDirectoryPath, cancellationToken);

    public Task<IReadOnlyList<string>> LoadRecentRoomDirectoriesAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadRecentRoomDirectoriesAsync(cancellationToken);

    public Task<string?> LoadLastBindingsFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastBindingsFilePathAsync(cancellationToken);
}
