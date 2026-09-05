using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// #1888: the journal half of the two-channel stream-log-loss announcement (<c>spec/baton.md</c> §3),
/// as a handle that can write <em>only</em> that fact. <see cref="Dispatch.CoreDispatcher"/> holds this
/// rather than a general <see cref="IEventLogWriter"/> because the restriction used to live in a doc
/// comment on the constructor parameter — prose no compiler reads, so any later edit could have
/// appended any <see cref="FlowEvent"/> through it with nothing failing. The separation
/// <see cref="ICoreEventLogWriter"/> describes is a type-system rule again, with three writer
/// interfaces instead of two rather than a documented breach of two.
/// </summary>
public interface IStreamLogLossJournal
{
    /// <summary>
    /// Appends <paramref name="loss"/> durably, with the same fsync-before-return guarantee
    /// <see cref="IEventLogWriter.AppendAsync"/> gives Flow's own events.
    /// <para>
    /// <b>An implementation must yield before it does I/O.</b> The only production caller
    /// (<c>CoreDispatcher</c>'s <c>onLossDeclared</c> handler) starts this task and never awaits it,
    /// while <c>ExecutionStreamLogger</c> holds its own lock and on whichever thread declared the loss
    /// — usually <c>BatonTask</c>'s chunk-delivery thread. <see cref="FlowEventLogWriter"/> satisfies
    /// this because its <c>FileStream</c> is opened <c>useAsync: true</c> and its gate is a
    /// <c>SemaphoreSlim.WaitAsync</c>; an implementation that ran synchronous I/O before its first
    /// await would hold the stream logger's lock across an fsync on that thread, stalling capture of
    /// the very stream whose loss is being announced.
    /// </para>
    /// </summary>
    Task AppendAsync(FlowEvent.StreamLogLossDeclared loss, CancellationToken cancellationToken = default);
}
