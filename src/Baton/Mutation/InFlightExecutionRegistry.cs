using Baton.Domain;
using Baton.Store;

namespace Baton.Mutation;

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
    private readonly Dictionary<ExecutionId, string> _arrestIntents = new();
    private TaskCompletionSource _arrestWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IEventLogWriter? _eventLogWriter;

    /// <summary>
    /// Durably records a cancellation intent for <paramref name="targetExecutionId"/> (intent-first
    /// ordering) and signals its dispatch — the same <see cref="FlowEvent.CancellationRequested"/>
    /// append <see cref="MutationInterface.RequestCancellationAsync"/> would make, but delivered
    /// in-process to a dispatch this same call already has in flight, instead of waiting on the
    /// concurrency guard. Returns <c>true</c> if <paramref name="targetExecutionId"/> was registered in-flight and
    /// cancellation was recorded and signalled; <c>false</c> if it was not registered (already
    /// settled, not yet registered by <c>MutationInterface</c>, or a non-process target). Records
    /// <see cref="CancellationOrigin.Operator"/> (#1762) — this is always an operator naming a
    /// specific execution, never a wind-down.
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

        await eventLogWriter.AppendAsync(
                new FlowEvent.CancellationRequested(targetExecutionId, CancellationOrigin.Operator), cancellationToken)
            .ConfigureAwait(false);

        // #1549: distinct from the CancellationRequested append above, which only records that Flow
        // forwarded the intent -- this records that the signal actually reached a live token, not
        // merely that a (possibly since-disposed) entry was found under the lock above. TryCancel's own
        // remarks explain the disposal race this guards: the dispatch can settle and dispose its token
        // between the snapshot above and the Cancel() call below, in which case there is nothing left
        // to deliver to even though a request was genuinely forwarded moments earlier.
        if (TryCancel(cancellationTokenSource))
        {
            await eventLogWriter.AppendAsync(new FlowEvent.CancellationDelivered(targetExecutionId), cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// A host-initiated stop ("no workflow-level stop operation" resolution: simply an intent
    /// minted for every currently in-flight <see cref="ExecutionId"/>): records
    /// <see cref="FlowEvent.CancellationRequested"/> for every entry still registered — fsync'd,
    /// sequentially, in registration order, all before any is signalled — then cancels every one of
    /// them. Called once the pump's own host <see cref="CancellationToken"/> fires. Records
    /// <see cref="CancellationOrigin.HostStop"/> (#1762): this mints one per still-registered
    /// execution regardless of whether any of them is the one an operator actually meant to stop, so
    /// it must never be read as an operator naming that step (spec/baton.md §2).
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
            await eventLogWriter.AppendAsync(
                    new FlowEvent.CancellationRequested(executionId, CancellationOrigin.HostStop), cancellationToken)
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
    /// <returns><c>true</c> if the signal was actually delivered to a live token; <c>false</c> if the source was already disposed.</returns>
    private static bool TryCancel(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
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
    /// #1549: records that <c>Baton.Cli.CancelRequestPoller</c> gave up delivering a
    /// <c>cancel.request</c> against <paramref name="targetExecutionId"/> — its bounded retry
    /// exhausted, the file-channel rejection <c>CancelRequestFile.Reject</c> already writes. Reuses
    /// this instance's own bound writer (<see cref="Bind"/>) rather than adding a second writer
    /// parameter to the poller, the same way <see cref="RequestCancellationAsync"/> already does for
    /// the successful-delivery half of the same flow. A no-op if this instance was never bound (no
    /// live pump call, e.g. a unit test exercising the poller directly).
    /// </summary>
    /// <param name="reason">
    /// #1530: the same string the caller already hands <c>CancelRequestFile.Reject</c>, carried onto
    /// <see cref="FlowEvent.CancellationRejected"/> so the durable journal — not just the ephemeral
    /// <c>.rejected</c> file a later <c>cancel.request</c> write can overwrite — records why.
    /// </param>
    public async Task RecordCancellationRejectedAsync(ExecutionId targetExecutionId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        IEventLogWriter? eventLogWriter;
        lock (_lock)
        {
            eventLogWriter = _eventLogWriter;
        }

        if (eventLogWriter is null)
        {
            return;
        }

        await eventLogWriter.AppendAsync(new FlowEvent.CancellationRejected(targetExecutionId, reason), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// #1556 (folding #1563's narrower <c>MarkParkedCancelIntent</c> into this general seam, per that
    /// method's own follow-up note): marks an arrest intent for a target this pump has no live
    /// PROCESS dispatch for — a step bound to a <see cref="WorkerBinding.NonProcess"/> worker, a
    /// step-less supplementary execution, or a step Failed with a future
    /// <see cref="Domain.StepState.RetryNotBefore"/> (a quota park; the worker process behind it
    /// already exited) — and wakes whichever of the pump's two waits is currently parked (the
    /// idle-deferral wait when nothing else is in flight, or the busy wait when a DIFFERENT step's
    /// dispatch still is) so the next round can validate and record it from projected state, the same
    /// intent-first discipline <see cref="RequestCancellationAsync"/> already follows for a live
    /// process. Idempotent: re-marking the same id before the pump drains it is a no-op (the reason is
    /// simply overwritten with the latest). <paramref name="reason"/> is diagnostic only — surfaced
    /// verbatim if the pump ultimately cannot settle this intent (already settled by the time it
    /// drains, or the id was never accepted) — never itself part of the settle decision, which is
    /// re-derived from projected state alone.
    /// </summary>
    public void MarkArrestIntent(ExecutionId targetExecutionId, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        lock (_lock)
        {
            _arrestIntents[targetExecutionId] = reason;
            _arrestWake.TrySetResult();
        }
    }

    /// <summary>Every arrest intent marked since the last drain, with the reason it was marked, and clears them.</summary>
    internal IReadOnlyList<(ExecutionId ExecutionId, string Reason)> DrainArrestIntents()
    {
        lock (_lock)
        {
            var drained = _arrestIntents.Select(kv => (kv.Key, kv.Value)).ToList();
            _arrestIntents.Clear();
            return drained;
        }
    }

    /// <summary>
    /// True while at least one arrest intent is still undrained — the fixed-point guard: a round
    /// heading for the pump's idle return must recheck this immediately before returning, since a
    /// mark landing after this round's own drain would otherwise be silently dropped when the pump
    /// exits and the poller that could re-offer it is cancelled in the same instant.
    /// </summary>
    internal bool HasPendingArrestIntents()
    {
        lock (_lock)
        {
            return _arrestIntents.Count > 0;
        }
    }

    /// <summary>
    /// The awaitable the deferral wait parks on alongside its delay and host-stop watchers. Captured
    /// fresh each time the wait is entered — never reused across rounds without going through
    /// <see cref="ResetArrestWake"/> — so a mark that lands anywhere before this is captured is
    /// never lost: the returned task is already complete, and the caller's own <c>WhenAny</c> resolves
    /// it immediately.
    /// </summary>
    internal Task NextArrestWake()
    {
        lock (_lock)
        {
            return _arrestWake.Task;
        }
    }

    /// <summary>Swaps in a fresh wake latch, but only if <paramref name="observed"/> is still the current one.</summary>
    internal void ResetArrestWake(Task observed)
    {
        lock (_lock)
        {
            if (_arrestWake.Task == observed)
            {
                _arrestWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
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
