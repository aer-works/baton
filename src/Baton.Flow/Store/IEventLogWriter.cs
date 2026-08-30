using Baton.Flow.Domain;

namespace Baton.Flow.Store;

/// <summary>
/// Appends events to Flow's exclusive half of the Event Store. Reading and
/// projection are out of scope — see <c>Baton.Flow</c> Phase 4 (State Projector).
/// </summary>
public interface IEventLogWriter
{
    /// <summary>
    /// Appends <paramref name="flowEvent"/> durably. Lifecycle events are fsync'd
    /// before this method returns, so a caller that awaits it before taking the next
    /// write-sequence step (e.g. dispatching to Core) can never act on an intent that isn't yet
    /// durable.
    /// </summary>
    Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default);
}
