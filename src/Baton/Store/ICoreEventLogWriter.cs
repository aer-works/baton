using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// Appends Core-originated lifecycle events to the combined log (the <c>events.jsonl</c>
/// half — M7 Phase 6 shares one physical file with Flow's own events, permitted because the
/// storage backend is implementation-defined). Only the Core Dispatcher calls this; Flow's own
/// mutation logic writes through <see cref="IEventLogWriter"/> instead — separate interfaces are
/// what enforce, in the type system, which half of the log a given caller may write to.
/// </summary>
public interface ICoreEventLogWriter
{
    /// <summary>
    /// Appends <paramref name="coreEvent"/> durably, with the same fsync-before-return guarantee
    /// <see cref="IEventLogWriter.AppendAsync"/> gives Flow's own events.
    /// </summary>
    Task AppendAsync(CoreEvent coreEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends the one diagnostic Flow fact Core is responsible for: a stream logger's declared loss.
    /// The logger itself only reports its in-memory latch; <c>CoreDispatcher</c> owns translating that
    /// report into this room-ledger event (#1885).
    /// </summary>
    Task AppendStreamLogLossAsync(
        FlowEvent.StreamLogLossDeclared streamLogLoss,
        CancellationToken cancellationToken = default);
}
