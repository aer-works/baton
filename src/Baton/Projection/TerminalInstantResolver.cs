using Baton.Domain;

namespace Baton.Projection;

/// <summary>
/// #1157: the workflow-run terminal instant — <b>when this room's run ended</b> — derived from the
/// journal alone. The one derivation; <see cref="Baton.Store.WorkflowTerminalProbe"/> (the disk-facing
/// reader) and <see cref="Baton.Status.WorkflowStatusProjector"/> (the view-facing one) both call this
/// rather than each computing their own.
/// <para>
/// <b>No <see cref="FlowEvent"/> field was added for this, because none was needed.</b> Every journal
/// line is already stamped by its writer — <see cref="LogEntry.FlowLogEntry.WriterUtcTimestamp"/>,
/// set to <c>DateTime.UtcNow</c> in <see cref="Baton.Store.FlowEventLogWriter"/> — so a durable,
/// engine-written instant already sits on the terminal event's own envelope. Exposing it is
/// projection work, not model work. (There is also deliberately no workflow-level transition event to
/// hang one on: see <see cref="FlowEvent"/>'s own remarks.)
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The instant is the LAST transition into <see cref="WorkflowStatus.Terminal"/>, not the last
/// line's timestamp.</b> Those two differ, and only the former is a terminal instant rather than a
/// fresher proxy: anything appended after the run ended — a <see cref="FlowEvent.CaptureResolved"/>
/// settlement, a late Core lifecycle line, a diagnostic — moves the last line's timestamp without the
/// run having ended any later. The retention grace window keying on a value a later append can move is
/// exactly the defect #1157 exists to close, so a definition that merely swaps <c>flow.jsonl</c>'s
/// mtime for its last line's stamp would leave that defect in place wearing a new hat.
/// </para>
/// <para>
/// <b>Why LAST and not first.</b> Terminality is not monotone: a
/// <see cref="FlowEvent.CaptureResolved"/> rejection re-admits the step to
/// <see cref="Scheduling.RetryEngine.MayRetry"/>'s ordinary predicate, and a fresh
/// <see cref="FlowEvent.ExecutionRequestAccepted"/> reopens a foreclosed or indeterminate step
/// outright (<see cref="StateProjector"/>). A room can therefore go terminal, be reopened, and go
/// terminal again — the answer a reader wants is when it ended <em>this</em> time.
/// </para>
/// <para>
/// <b>Cost.</b> One projection to establish terminality, then one more per journal line that follows
/// the transition, walking backwards. In the ordinary shape — the terminal event is the last line —
/// that is two projections total; it degrades only in proportion to how much was appended after the
/// run ended, which is bounded by the settlement verbs.
/// </para>
/// </remarks>
public static class TerminalInstantResolver
{
    /// <summary>
    /// The instant <paramref name="entries"/> last transitioned to <see cref="WorkflowStatus.Terminal"/>,
    /// or <c>null</c> when there is no honest answer. <c>null</c> means one of three things, and a
    /// caller must never collapse them into a fabricated instant (spec/baton.md §3):
    /// <list type="bullet">
    /// <item><description>the run is <b>not terminal</b> — including the crash window, where a journal
    /// whose terminal event was never written is simply a room that has not ended;</description></item>
    /// <item><description>the run is terminal but no line ever made it so (a zero-step snapshot, whose
    /// empty journal already projects terminal);</description></item>
    /// <item><description>the transition line predates writer stamping (#745) and carries no
    /// <see cref="LogEntry.FlowLogEntry.WriterUtcTimestamp"/> — the legacy-journal arm each caller
    /// falls back for on its own terms.</description></item>
    /// </list>
    /// A truncated final line is not a case here at all: <see cref="Baton.Store.FlowEventLogReader"/>
    /// hands back only <c>\n</c>-terminated lines, so a half-written terminal event is not yet
    /// observable and this reads exactly like the first case above.
    /// </summary>
    /// <param name="entries">
    /// The room's journal entries in append order, timestamps included
    /// (<see cref="Baton.Store.FlowEventLogReader.ReadAllEntriesWithTimestampsAsync"/>). Core- and
    /// room-owned lines are ignored: only Flow's own half drives <see cref="StateProjector"/>, so only
    /// a Flow line can be the transition.
    /// </param>
    /// <param name="snapshot">The same bound definition the caller projected against.</param>
    public static DateTime? Resolve(IReadOnlyList<LogEntry> entries, WorkflowDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(snapshot);

        var flowEvents = new List<FlowEvent>(entries.Count);
        var stamps = new List<DateTime?>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry flowEntry)
            {
                flowEvents.Add(flowEntry.Event);
                stamps.Add(flowEntry.WriterUtcTimestamp);
            }
        }

        var events = flowEvents.ToArray();

        // Deliberately no ProjectionCheckpoint on any of these calls, including the full one: a
        // checkpoint's EventOffset is only valid against the full list it was taken from, and feeding
        // a shorter prefix one trips StateProjector's loud "EventOffset exceeds log event count"
        // full-replay fallback -- which is correct behaviour, but would print that line once per
        // prefix probed. The projections here are already prefix replays by construction.
        if (!IsTerminal(events, events.Length, snapshot))
        {
            return null;
        }

        var transitionIndex = events.Length;
        while (transitionIndex > 0 && IsTerminal(events, transitionIndex - 1, snapshot))
        {
            transitionIndex--;
        }

        // Zero-step snapshots project terminal on an empty journal, so no line ever transitioned it.
        // Absent, never fabricated.
        if (transitionIndex == 0)
        {
            return null;
        }

        return Normalize(stamps[transitionIndex - 1]);
    }

    private static bool IsTerminal(FlowEvent[] events, int prefixLength, WorkflowDefinitionSnapshot snapshot) =>
        StateProjector.Project(new ArraySegment<FlowEvent>(events, 0, prefixLength), snapshot).Status
            == WorkflowStatus.Terminal;

    /// <summary>
    /// The writer stamps <c>DateTime.UtcNow</c>, so a line round-tripped through
    /// <see cref="Baton.Store.FlowEventLogJson"/> comes back <see cref="DateTimeKind.Utc"/> already.
    /// A hand-built entry (a test, a fixture) can carry <see cref="DateTimeKind.Unspecified"/>; read it
    /// as UTC rather than letting a later <c>ToUniversalTime</c> silently shift it by the host's
    /// offset. Never <c>ToUniversalTime</c> on an Unspecified value: that treats it as local time,
    /// which is the one interpretation the journal never means.
    /// </summary>
    private static DateTime? Normalize(DateTime? stamp) => stamp switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } value => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        { Kind: DateTimeKind.Local } value => value.ToUniversalTime(),
        var value => value,
    };
}
