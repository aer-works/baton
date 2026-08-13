using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Microsoft.Extensions.Hosting;

namespace Aer.Daemon;

public abstract record OccupantTurnResult
{
    private OccupantTurnResult() { }

    public sealed record Completed : OccupantTurnResult;
    public sealed record Failed(string Reason) : OccupantTurnResult;
}

public interface IOccupantTurnRunner
{
    Task<OccupantTurnResult> RunTurnAsync(OrchestratorTurnInput input, TimeSpan budget, CancellationToken ct);
}

/// <summary>
/// Singleton host state for <see cref="RoomTurnHost"/> (#992).
/// Surfaced on status endpoints (#994).
/// </summary>
public sealed class RoomTurnHostState
{
    private volatile string? _roomDirectoryPath;
    private volatile bool _userMessagePending;
    private volatile bool _inFlight;
    private int _consecutiveFailures;
    private volatile string? _lastDecisionReason;
    private volatile string? _throttleLoadError;
    private RoomTurnThrottles _throttles = RoomTurnThrottles.Defaults;
    private readonly List<DateTimeOffset> _machineTurnStarts = [];
    private readonly object _lock = new();

    public string? RoomDirectoryPath
    {
        get => _roomDirectoryPath;
        set => _roomDirectoryPath = value;
    }

    /// <summary>
    /// V1 Seam: user message pending flag. V1 has no room-chat inbox yet, so userMessagePending
    /// comes from this settable flag on RoomTurnHostState — the seam exists, the producer is
    /// future work.
    /// </summary>
    public bool UserMessagePending
    {
        get => _userMessagePending;
        set => _userMessagePending = value;
    }

    public bool InFlight
    {
        get => _inFlight;
        internal set => _inFlight = value;
    }

    /// <summary>Read-only view; mutation goes through <see cref="RecordTurnFailure"/> /
    /// <see cref="ResetConsecutiveFailures"/> so the tick loop's read-modify-write and the
    /// clear-dormancy endpoint's reset cannot interleave into a lost update (second-reader
    /// finding on #992: an operator's clear could be silently undone by an in-flight turn
    /// failing, re-tripping the breaker they had just cleared).</summary>
    public int ConsecutiveFailures
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    public int RecordTurnFailure()
    {
        lock (_lock) return ++_consecutiveFailures;
    }

    public void ResetConsecutiveFailures()
    {
        lock (_lock) _consecutiveFailures = 0;
    }

    public string? LastDecisionReason
    {
        get => _lastDecisionReason;
        internal set => _lastDecisionReason = value;
    }

    public string? ThrottleLoadError
    {
        get => _throttleLoadError;
        internal set => _throttleLoadError = value;
    }

    public RoomTurnThrottles Throttles
    {
        get
        {
            lock (_lock) return _throttles;
        }
        internal set
        {
            lock (_lock) _throttles = value;
        }
    }

    public IReadOnlyList<DateTimeOffset> MachineTurnStarts
    {
        get
        {
            lock (_lock) return _machineTurnStarts.ToList().AsReadOnly();
        }
    }

    public void RecordMachineTurnStart(DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            _machineTurnStarts.Add(timestamp);
            var cutoff = timestamp.AddHours(-24);
            _machineTurnStarts.RemoveAll(t => t < cutoff);
        }
    }
}

/// <summary>
/// Resident room turn host (#992).
/// Runs a 500ms loop consuming wakes and running occupant turns under throttles and watchdog budget.
/// </summary>
public sealed class RoomTurnHost : BackgroundService
{
    private const string RoomLogFileName = "room.jsonl";

    /// <summary>
    /// The <see cref="EscalationSubject.HostCondition"/> condition name the dormancy breaker
    /// raises. The status endpoint (#994) matches on it to surface the tripping escalation.
    /// Canonical value lives on <see cref="RoomEvent.TurnHostDormancyEntered.DormancyConditionName"/>
    /// (#1178: the projector pairs on it too and cannot reference this assembly).
    /// </summary>
    public const string DormancyConditionName = RoomEvent.TurnHostDormancyEntered.DormancyConditionName;

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Turn budget for v1 (constant 10 minutes). Will become grant data in future work.
    /// </summary>
    public static readonly TimeSpan DefaultTurnBudget = TimeSpan.FromMinutes(10);

    private readonly RoomWakeBridgeState _wakeBridgeState;
    private readonly RoomTurnHostState _hostState;
    private readonly IOccupantTurnRunner? _runner;
    private readonly TimeSpan _turnBudget;

    public RoomTurnHost(
        RoomWakeBridgeState wakeBridgeState,
        RoomTurnHostState hostState,
        IOccupantTurnRunner? runner = null,
        TimeSpan? turnBudget = null)
    {
        _wakeBridgeState = wakeBridgeState ?? throw new ArgumentNullException(nameof(wakeBridgeState));
        _hostState = hostState ?? throw new ArgumentNullException(nameof(hostState));
        _runner = runner;
        _turnBudget = turnBudget ?? DefaultTurnBudget;
    }

    public async Task ExecuteSingleTickAsync(CancellationToken cancellationToken = default)
    {
        var roomDirectoryPath = _wakeBridgeState.RoomDirectoryPath;
        _hostState.RoomDirectoryPath = roomDirectoryPath;

        if (string.IsNullOrWhiteSpace(roomDirectoryPath) || _runner is null || !Directory.Exists(roomDirectoryPath))
        {
            return;
        }

        var (throttles, loadError) = RoomTurnThrottles.Load(roomDirectoryPath);
        _hostState.Throttles = throttles;
        _hostState.ThrottleLoadError = loadError;

        var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
        var reader = new RoomEventLogReader(roomLogPath);

        var roomEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var roomState = RoomProjector.Project(roomEvents);

        var now = DateTimeOffset.UtcNow;
        var decision = RoomTurnScheduler.Schedule(
            now: now,
            turnInFlight: _hostState.InFlight,
            userMessagePending: _hostState.UserMessagePending,
            machineWakesPending: _wakeBridgeState.CurrentWakes.Count > 0,
            recentMachineTurnStarts: _hostState.MachineTurnStarts,
            consecutiveUncommittedTurns: _hostState.ConsecutiveFailures,
            throttles: throttles,
            isDormant: roomState.IsDormant);

        switch (decision)
        {
            case RoomTurnDecision.Wait wait:
                _hostState.LastDecisionReason = wait.Reason;
                return;

            case RoomTurnDecision.Dormant:
                _hostState.LastDecisionReason = "Dormant";
                if (!roomState.IsDormant)
                {
                    await using var writer = new RoomEventLogWriter(roomLogPath);
                    await RoomMutationInterface.EnterTurnHostDormancyAsync(
                        roomDirectoryPath, _hostState.ConsecutiveFailures, reader, writer, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    var escalationSubject = new EscalationSubject.HostCondition(
                        DormancyConditionName,
                        $"{_hostState.ConsecutiveFailures} consecutive uncommitted turns tripped the breaker");
                    await RoomMutationInterface.RaiseEscalationAsync(
                        roomDirectoryPath, new WorkerId("turn-host"), EscalationTrigger.Confidence, escalationSubject, reader, writer, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                return;

            case RoomTurnDecision.StartUserTurn:
                _hostState.LastDecisionReason = "StartUserTurn";
                _hostState.UserMessagePending = false;
                await ExecuteTurnCoreAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
                return;

            case RoomTurnDecision.StartMachineTurn:
                _hostState.LastDecisionReason = "StartMachineTurn";
                _hostState.RecordMachineTurnStart(now);
                await ExecuteTurnCoreAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task ExecuteTurnCoreAsync(string roomDirectoryPath, CancellationToken stoppingToken)
    {
        _hostState.InFlight = true;
        try
        {
            var input = await OrchestratorTurnInput.AssembleAsync(
                roomDirectoryPath, _wakeBridgeState.CurrentWakes, stoppingToken)
                .ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(_turnBudget);

            try
            {
                var result = await _runner!.RunTurnAsync(input, _turnBudget, cts.Token).ConfigureAwait(false);
                if (result is OccupantTurnResult.Completed)
                {
                    OrchestratorTurnInput.CommitTurn(roomDirectoryPath, input);
                    _hostState.ResetConsecutiveFailures();
                }
                else
                {
                    _hostState.RecordTurnFailure();
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && cts.IsCancellationRequested)
            {
                // Watchdog timeout
                _hostState.RecordTurnFailure();
                var roomLogPath = Path.Combine(roomDirectoryPath, RoomLogFileName);
                var reader = new RoomEventLogReader(roomLogPath);
                await using var writer = new RoomEventLogWriter(roomLogPath);
                var escalationSubject = new EscalationSubject.HostCondition(
                    "turn-watchdog-timeout",
                    $"turn exceeded its {_turnBudget} budget and was terminated");
                await RoomMutationInterface.RaiseEscalationAsync(
                    roomDirectoryPath, new WorkerId("turn-host"), EscalationTrigger.Confidence, escalationSubject, reader, writer, cancellationToken: stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoomTurnHost: Turn execution threw exception: {ex}");
                _hostState.RecordTurnFailure();
            }
        }
        finally
        {
            _hostState.InFlight = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteSingleTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoomTurnHost tick failed: {ex}");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
