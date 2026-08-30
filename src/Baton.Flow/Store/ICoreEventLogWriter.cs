using Baton.Flow.Domain;

namespace Baton.Flow.Store;

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
}
