using Baton.Domain;
using Baton.Status;

namespace Baton.Mutation;

/// <summary>
/// #1623 ruling addendum (2026-09-01 night, "we have to stop letting agy run away with token
/// consumption"): accumulates a live execution's own usage from complete stdout lines — via
/// <see cref="IWorkerUsageParser.TryParseIncrementalUsage"/>, the same per-vendor shape
/// <c>ExecutionUsageProjector</c> reads post-hoc, but read as each line arrives rather than only after
/// exit — and requests cancellation the moment the running total crosses <paramref name="budget"/>.
/// </summary>
/// <remarks>
/// Wired at <c>MutationInterface.DispatchAndRecordOutcomeAsync</c>; <c>spec/baton.md</c> §3 states the
/// composition rule this follows — in one clause, it wraps a caller's existing
/// <c>CoreDispatchTarget.OnStdoutLine</c> sink and never replaces one.
/// <see cref="OnStdoutLine"/> runs on <c>BatonTask</c>'s single event-delivery thread per
/// its own documented contract, but every member here is still locked — a monitor instance is
/// constructed once per execution and its snapshot methods are read from the awaiting async
/// continuation on a different thread once <see cref="ArrestRequested"/> fires, so this is a genuine
/// cross-thread handoff, not defensive-only locking.
/// </remarks>
public sealed class TokenBudgetMonitor
{
    /// <summary>
    /// How many of the most recent tool names <see cref="SnapshotLastToolNames"/> keeps — enough for a
    /// conductor to see the pattern (e.g. a poll loop) without the room fact growing unbounded over a
    /// long-running arrest.
    /// </summary>
    private const int MaxLastToolNames = 10;

    private readonly long _budget;
    private readonly IWorkerUsageParser _usageParser;
    private readonly CancellationTokenSource _arrestSource = new();
    private readonly Lock _lock = new();
    private readonly List<string> _lastToolNames = [];
    private long _inputLevel;
    private long? _latestTokensIn;
    private long? _latestCacheRead;
    private long? _latestCacheCreation;
    private long _tokensOut;
    private bool _arrested;

    public TokenBudgetMonitor(long budget, IWorkerUsageParser usageParser)
    {
        _budget = budget;
        _usageParser = usageParser ?? throw new ArgumentNullException(nameof(usageParser));
    }

    /// <summary>Cancelled exactly once, the instant the running total first crosses the budget.</summary>
    public CancellationToken ArrestRequested => _arrestSource.Token;

    /// <summary>Whether this monitor itself requested the arrest — see this type's own remarks for why
    /// the caller must check this rather than inferring arrest from cancellation alone (an operator's
    /// own <c>dispatchCancellationToken</c> firing first must never be misread as a budget arrest).</summary>
    public bool Arrested
    {
        get { lock (_lock) { return _arrested; } }
    }

    /// <summary>
    /// Feeds one complete stdout line. Cheap for the overwhelming majority of lines (neither parse
    /// matches), and safe to call after <see cref="Arrested"/> is already true — a line or two can
    /// still arrive while the process is being torn down.
    /// </summary>
    public void OnStdoutLine(string line)
    {
        if (_usageParser.TryParseToolName(line) is { Length: > 0 } toolName)
        {
            lock (_lock)
            {
                _lastToolNames.Add(toolName);
                if (_lastToolNames.Count > MaxLastToolNames)
                {
                    _lastToolNames.RemoveAt(0);
                }
            }
        }

        if (!_usageParser.TryParseIncrementalUsage(line, out var usage) || usage is null)
        {
            return;
        }

        bool crossed;
        lock (_lock)
        {
            if (usage.TokensIn.HasValue || usage.CacheReadTokens.HasValue || usage.CacheCreationTokens.HasValue)
            {
                _latestTokensIn = usage.TokensIn;
                _latestCacheRead = usage.CacheReadTokens;
                _latestCacheCreation = usage.CacheCreationTokens;
                _inputLevel = (usage.TokensIn ?? 0) + (usage.CacheReadTokens ?? 0) + (usage.CacheCreationTokens ?? 0);
            }

            _tokensOut += usage.TokensOut ?? 0;
            crossed = !_arrested && (_inputLevel + _tokensOut >= _budget);
            _arrested = _arrested || crossed;
        }

        if (crossed)
        {
            _arrestSource.Cancel();
        }
    }

    /// <summary>
    /// The measured usage at the moment of the snapshot — <see cref="FlowEvent.ExecutionArrested.Usage"/>.
    /// #1623 re-review N6: <see cref="WorkerUsage.TokensIn"/> stays the vendor-raw latest reading
    /// (never fabricated, per <see cref="WorkerUsage"/>'s own doc); the accumulated level this monitor
    /// actually budgets against — already <c>TokensIn + CacheReadTokens + CacheCreationTokens</c> —
    /// goes on <see cref="WorkerUsage.ContextLevelTokens"/> instead, so a reader summing the three raw
    /// fields does not silently double-count it.
    /// </summary>
    public WorkerUsage SnapshotUsage()
    {
        lock (_lock)
        {
            return new WorkerUsage(
                TokensIn: _latestTokensIn,
                TokensOut: _tokensOut,
                CacheReadTokens: _latestCacheRead,
                CacheCreationTokens: _latestCacheCreation,
                ContextLevelTokens: _inputLevel);
        }
    }

    /// <summary>The last few observed tool names — <see cref="FlowEvent.ExecutionArrested.LastToolNames"/>.</summary>
    public IReadOnlyList<string> SnapshotLastToolNames()
    {
        lock (_lock)
        {
            return [.. _lastToolNames];
        }
    }
}
