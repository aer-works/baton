using Baton.Domain;
using Baton.Status;

namespace Baton.Mutation;

/// <summary>
/// #1623 ruling addendum (2026-09-01 night, "we have to stop letting agy run away with token
/// consumption"), corrected by #1682 ("the token budget cannot arrest the burn that costs money"):
/// accumulates a live execution's own usage from complete stdout lines — via
/// <see cref="IWorkerUsageParser.TryParseIncrementalUsage"/>, the same per-vendor shape
/// <c>ExecutionUsageProjector</c> reads post-hoc, but read as each line arrives rather than only after
/// exit — and requests cancellation the moment either of two independent triggers fires: the running
/// Σ of billed tokens crosses <paramref name="budget"/>, or the running tool-step count crosses
/// <paramref name="maxToolSteps"/>. Why both exist, and what each catches that the other misses, is
/// spec/baton.md §3's own evidence-backed case, not restated here.
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

    private readonly long? _budget;
    private readonly int? _maxToolSteps;
    private readonly IWorkerUsageParser _usageParser;
    private readonly CancellationTokenSource _arrestSource = new();
    private readonly Lock _lock = new();
    private readonly List<string> _lastToolNames = [];
    private long _inputLevel;
    private long? _latestTokensIn;
    private long? _latestCacheRead;
    private long? _latestCacheCreation;
    private long _tokensOut;
    private long? _billedTokens;
    private long _cacheReadSum;
    private int _toolStepCount;
    private bool _arrested;
    private ArrestReason? _arrestReason;

    /// <param name="budget">
    /// #1682: the per-execution ceiling <see cref="WorkerUsage.BilledTokens"/> arrests on. Null enforces
    /// no token-side trigger (a role/dispatch with a tool-step cap but no budget still watches, unlike
    /// before this issue where a monitor required a budget to exist at all).
    /// </param>
    /// <param name="maxToolSteps">
    /// #1682: the per-execution ceiling on Σ<see cref="IWorkerUsageParser.CountToolSteps"/>. Fires the
    /// instant the running count exceeds it, regardless of whether usage ever parses on this stream at
    /// all. Null enforces no tool-step trigger.
    /// </param>
    public TokenBudgetMonitor(long? budget, int? maxToolSteps, IWorkerUsageParser usageParser)
    {
        _budget = budget;
        _maxToolSteps = maxToolSteps;
        _usageParser = usageParser ?? throw new ArgumentNullException(nameof(usageParser));
    }

    /// <summary>Cancelled exactly once, the instant either trigger first fires.</summary>
    public CancellationToken ArrestRequested => _arrestSource.Token;

    /// <summary>Whether this monitor itself requested the arrest — see this type's own remarks for why
    /// the caller must check this rather than inferring arrest from cancellation alone (an operator's
    /// own <c>dispatchCancellationToken</c> firing first must never be misread as a budget arrest).</summary>
    public bool Arrested
    {
        get { lock (_lock) { return _arrested; } }
    }

    /// <summary>Which trigger fired — null until <see cref="Arrested"/> is true.</summary>
    public ArrestReason? ArrestReasonValue
    {
        get { lock (_lock) { return _arrestReason; } }
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

        // #1682: the tool-step count is read off EVERY line, independent of whether usage parses on
        // it — the cap's whole reason for existing is to arrest a stream with malformed or absent
        // usage lines, the same pattern the incremental usage parse cannot see at all.
        var toolStepDelta = _usageParser.CountToolSteps(line);
        var usageParsed = _usageParser.TryParseIncrementalUsage(line, out var usage) && usage is not null;

        ArrestReason? newlyArmed = null;
        lock (_lock)
        {
            if (toolStepDelta > 0)
            {
                _toolStepCount += toolStepDelta;
            }

            if (usageParsed)
            {
                if (usage!.TokensIn.HasValue || usage.CacheReadTokens.HasValue || usage.CacheCreationTokens.HasValue)
                {
                    _latestTokensIn = usage.TokensIn;
                    _latestCacheRead = usage.CacheReadTokens;
                    _latestCacheCreation = usage.CacheCreationTokens;
                    _inputLevel = (usage.TokensIn ?? 0) + (usage.CacheReadTokens ?? 0) + (usage.CacheCreationTokens ?? 0);
                }

                _tokensOut += usage.TokensOut ?? 0;
                _cacheReadSum += usage.CacheReadTokens ?? 0;
                // #1682: per-line input + output + cache_creation, summed -- WorkerUsage.BilledTokens
                // has the full arithmetic case for the shape and the thinking-tokens exclusion. Stays
                // null (never a fabricated 0) until a usage line actually parses.
                _billedTokens = (_billedTokens ?? 0) + (usage.TokensIn ?? 0) + (usage.TokensOut ?? 0) + (usage.CacheCreationTokens ?? 0);
            }

            if (!_arrested && _budget is { } budget && _billedTokens is { } billedSoFar && billedSoFar >= budget)
            {
                newlyArmed = ArrestReason.TokenBudget;
            }
            else if (!_arrested && _maxToolSteps is { } cap && _toolStepCount > cap)
            {
                newlyArmed = ArrestReason.ToolStepCap;
            }

            if (newlyArmed is { } reason)
            {
                _arrested = true;
                _arrestReason = reason;
            }
        }

        if (newlyArmed is not null)
        {
            _arrestSource.Cancel();
        }
    }

    /// <summary>
    /// The measured usage at the moment of the snapshot — <see cref="FlowEvent.ExecutionArrested.Usage"/>.
    /// #1623 re-review N6: <see cref="WorkerUsage.TokensIn"/> stays the vendor-raw latest reading
    /// (never fabricated, per <see cref="WorkerUsage"/>'s own doc); the accumulated level this monitor
    /// displays (never arrests on, since #1682) goes on <see cref="WorkerUsage.ContextLevelTokens"/>
    /// instead, so a reader summing the three raw fields does not silently double-count it.
    /// <see cref="WorkerUsage.CacheReadTokens"/> here is the running Σ (display-only, #1682), not the
    /// latest reading. <see cref="WorkerUsage.BilledTokens"/> is the quantity actually compared to the
    /// budget.
    /// </summary>
    public WorkerUsage SnapshotUsage()
    {
        lock (_lock)
        {
            return new WorkerUsage(
                TokensIn: _latestTokensIn,
                TokensOut: _tokensOut,
                CacheReadTokens: _cacheReadSum,
                CacheCreationTokens: _latestCacheCreation,
                ContextLevelTokens: _inputLevel,
                BilledTokens: _billedTokens);
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

    /// <summary>The tool-step count at snapshot time — <see cref="FlowEvent.ExecutionArrested.ToolStepCount"/>.</summary>
    public int SnapshotToolStepCount()
    {
        lock (_lock) { return _toolStepCount; }
    }
}
