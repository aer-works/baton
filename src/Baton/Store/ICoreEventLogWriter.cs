using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// Appends Core-originated lifecycle events to the combined log (the <c>events.jsonl</c>
/// half — M7 Phase 6 shares one physical file with Flow's own events, permitted because the
/// storage backend is implementation-defined). Only the Core Dispatcher calls this; Flow's own
/// mutation logic writes through <see cref="IEventLogWriter"/> instead — separate interfaces are
/// what enforce, in the type system, which half of the log a given caller may write to.
/// <para>
/// One Flow event is written by <c>CoreDispatcher</c> all the same, since #1885:
/// <c>FlowEvent.StreamLogLossDeclared</c>. The fact it carries is Core's own (its stream logger lost
/// bytes), but it has to reach <c>ExecutionUsageProjector</c> through a writer that is not writing into
/// the obstructed execution output directory, and a reader of the journal is better served by one event
/// type per fact than by a Core event that duplicates a Flow one. That is not a breach of the rule
/// above: the dispatcher holds <see cref="IStreamLogLossJournal"/>, a third interface admitting exactly
/// that one event, so "which part of the log may this caller write" stays a compiler question and never
/// became a doc-comment promise (#1888).
/// </para>
/// </summary>
public interface ICoreEventLogWriter
{
    /// <summary>
    /// Appends <paramref name="coreEvent"/> durably, with the same fsync-before-return guarantee
    /// <see cref="IEventLogWriter.AppendAsync"/> gives Flow's own events.
    /// </summary>
    Task AppendAsync(CoreEvent coreEvent, CancellationToken cancellationToken = default);
}
