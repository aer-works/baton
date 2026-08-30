using Baton.Flow.Domain;
using Baton.Flow.Store;

namespace Baton.Flow.Mutation;

/// <summary>
/// M10 Phase 2's delivery mechanism: a caller-owned, in-process handle to
/// whichever process-bound <see cref="ExecutionId"/>s a single <see cref="MutationInterface"/> pump
/// call currently has dispatched to Core. The pump's concurrency guard is held for that call's entire duration, so no
/// second mutation-surface call can ever reach a live execution (a concurrent
/// <see cref="MutationInterface.RequestCancellationAsync"/> targeting it would just fail to acquire
/// the guard) — this registry is the pump's own host process offering an in-process alternative,
/// exactly the "pump's host process is the delivery point" answer M10's plan settled on. The pump
/// registers an entry the instant a <see cref="WorkerBinding.Process"/> dispatch starts and removes
/// it the instant that dispatch settles, so a caller holding this instance can signal one specific
/// live execution — via <see cref="RequestCancellationAsync"/> — without touching any sibling
/// dispatched by the same call.
/// </summary>
public sealed class InFlightExecutionRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ExecutionId, CancellationTokenSource> _entries = new();
    private IEventLogWriter? _eventLogWriter;

    /// <summary>
    /// Durably records a cancellation intent for <paramref name="targetExecutionId"/> (intent-first
    /// ordering) and signals its dispatch — the same <see cref="FlowEvent.CancellationRequested"/>
    /// append <see cref="MutationInterface.RequestCancellationAsync"/> would make, but delivered
    /// in-process to a dispatch this same call already has in flight, instead of waiting on the
    /// concurrency guard. Returns <c>true</c> if <paramref name="targetExecutionId"/> was registered in-flight and
    /// cancellation was recorded and signalled; <c>false</c> if it was not registered (already
    /// settled, not yet registered by <c>MutationInterface</c>, or a non-process target).
    /// </summary>
    public async Task<bool> RequestCancellationAsync(ExecutionId targetExecutionId, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancellationTokenSource;
        IEventLogWriter? eventLogWriter;
        lock (_lock)
        {
            _entries.TryGetValue(targetExecutionId, out cancellationTokenSource);
            eventLogWriter = _eventLogWriter;
        }

        if (cancellationTokenSource is null || eventLogWriter is null)
        {
            return false;
        }

        await eventLogWriter.AppendAsync(new FlowEvent.CancellationRequested(targetExecutionId), cancellationToken)
            .ConfigureAwait(false);
        TryCancel(cancellationTokenSource);
        return true;
    }

    /// <summary>
    /// A host-initiated stop ("no workflow-level stop operation" resolution: simply an intent
    /// minted for every currently in-flight <see cref="ExecutionId"/>): records
    /// <see cref="FlowEvent.CancellationRequested"/> for every entry still registered — fsync'd,
    /// sequentially, in registration order, all before any is signalled — then cancels every one of
    /// them. Called once the pump's own host <see cref="CancellationToken"/> fires.
    /// </summary>
    internal async Task RequestStopAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<ExecutionId, CancellationTokenSource>> snapshot;
        IEventLogWriter? eventLogWriter;
        lock (_lock)
        {
            snapshot = _entries.ToList();
            eventLogWriter = _eventLogWriter;
        }

        if (eventLogWriter is null)
        {
            return;
        }

        foreach (var (executionId, _) in snapshot)
        {
            await eventLogWriter.AppendAsync(new FlowEvent.CancellationRequested(executionId), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var (_, cancellationTokenSource) in snapshot)
        {
            TryCancel(cancellationTokenSource);
        }
    }

    /// <summary>
    /// The dispatch this <see cref="CancellationTokenSource"/> belongs to can settle — and
    /// <see cref="Unregister"/> can dispose it — at any point between this instance being snapshotted
    /// under <see cref="_lock"/> above and this call, including naturally (unrelated to the
    /// cancellation being delivered here). A disposed source has already done its job: the execution
    /// it governed is no longer in flight, so there is nothing left to signal.
    /// </summary>
    private static void TryCancel(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Binds this instance to the log writer the owning pump call is using for its whole duration.</summary>
    internal void Bind(IEventLogWriter eventLogWriter)
    {
        lock (_lock)
        {
            _eventLogWriter = eventLogWriter;
        }
    }

    /// <summary>
    /// Registers <paramref name="executionId"/> as in flight and returns the token its dispatch must
    /// observe — deliberately not linked to the pump's host token: a host stop is delivered only
    /// through <see cref="RequestStopAsync"/>, which records intent before it ever signals, rather
    /// than letting cancellation reach Core passively with nothing recorded.
    /// </summary>
    internal CancellationToken Register(ExecutionId executionId)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        lock (_lock)
        {
            _entries[executionId] = cancellationTokenSource;
        }

        return cancellationTokenSource.Token;
    }

    /// <summary>
    /// A snapshot of every <see cref="ExecutionId"/> this call currently has dispatched and not yet
    /// unregistered — what M10 Phase 3's crash reconciliation must exclude from consideration, since
    /// a dispatch this same call is still genuinely awaiting is not an orphan (its pump did not die;
    /// it is this pump) even though Core has already recorded its <see cref="Domain.CoreEvent.ExecutionStarted"/>
    /// with no matching <see cref="Domain.CoreEvent.ExecutionExited"/> yet.
    /// </summary>
    internal IReadOnlySet<ExecutionId> RegisteredExecutionIds()
    {
        lock (_lock)
        {
            return _entries.Keys.ToHashSet();
        }
    }

    /// <summary>Removes a settled dispatch so neither <see cref="RequestCancellationAsync"/> nor a host stop can reach it.</summary>
    internal void Unregister(ExecutionId executionId)
    {
        CancellationTokenSource? cancellationTokenSource;
        lock (_lock)
        {
            _entries.Remove(executionId, out cancellationTokenSource);
        }

        cancellationTokenSource?.Dispose();
    }
}
