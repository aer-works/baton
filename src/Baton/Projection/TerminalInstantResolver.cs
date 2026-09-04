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
/// <b>The instant is the LAST transition into <see cref="WorkflowStatus.Terminal"/>.</b> Why that,
/// rather than the last line's stamp or the file's mtime, is stated once in spec/baton.md §3 and
/// deliberately not restated here — this is the code implementing that ruling, not a second copy of
/// it. What the code needs on the page is the mechanics: the two events that make terminality
/// non-monotone, and so make "last" a different answer from "first", are
/// <see cref="FlowEvent.CaptureResolved"/> (a rejection re-admits the step to
/// <see cref="Scheduling.RetryEngine.MayRetry"/>'s ordinary predicate) and
/// <see cref="FlowEvent.ExecutionRequestAccepted"/> (a fresh dispatch reopens a foreclosed or
/// indeterminate step outright) — both in <see cref="StateProjector"/>.
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
    /// paired with <see cref="TerminalInstantAbsence"/> naming why when there is none. The pairing is
    /// the point: a caller must never fabricate an instant (spec/baton.md §3), and the three ways of
    /// having none call for three different responses — so returning a bare <c>null</c> would leave
    /// every caller to guess which one it was holding. The first version of this method did exactly
    /// that, and its one caller attributed all three to the legacy-journal case, printing a false
    /// operator diagnostic on a room whose journal had been written minutes earlier.
    /// <para>
    /// A truncated final line is not a case here at all: <see cref="Baton.Store.FlowEventLogReader"/>
    /// hands back only <c>\n</c>-terminated lines, so a half-written terminal event is not yet
    /// observable and this reads exactly like <see cref="TerminalInstantAbsence.NotTerminal"/>.
    /// </para>
    /// </summary>
    /// <param name="entries">
    /// The room's journal entries in append order, timestamps included
    /// (<see cref="Baton.Store.FlowEventLogReader.ReadAllEntriesWithTimestampsAsync"/>). Core- and
    /// room-owned lines are ignored: only Flow's own half drives <see cref="StateProjector"/>, so only
    /// a Flow line can be the transition.
    /// </param>
    /// <param name="snapshot">The same bound definition the caller projected against.</param>
    public static TerminalInstant Resolve(IReadOnlyList<LogEntry> entries, WorkflowDefinitionSnapshot snapshot)
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
            return new TerminalInstant(null, TerminalInstantAbsence.NotTerminal);
        }

        var transitionIndex = events.Length;
        while (transitionIndex > 0 && IsTerminal(events, transitionIndex - 1, snapshot))
        {
            transitionIndex--;
        }

        if (transitionIndex == 0)
        {
            return new TerminalInstant(null, TerminalInstantAbsence.NoTransitionEntry);
        }

        // Kind: the writer stamps DateTime.UtcNow and FlowEventLogJson round-trips that back as Utc, so
        // this is a no-op on every line the engine wrote. It exists for a hand-built entry carrying
        // Unspecified -- read as UTC, which is the only thing the journal ever means. Deliberately NOT
        // ToUniversalTime: on an Unspecified value that reinterprets it as local time and shifts it by
        // the host's offset. There is no Local arm to go with this one, because a Local stamp cannot
        // reach here and a branch whose only guard would pass on a UTC host (which CI is) is a branch
        // with no instrument.
        return stamps[transitionIndex - 1] is { } stamp
            ? new TerminalInstant(DateTime.SpecifyKind(stamp, DateTimeKind.Utc), TerminalInstantAbsence.None)
            : new TerminalInstant(null, TerminalInstantAbsence.TransitionEntryUnstamped);
    }

    private static bool IsTerminal(FlowEvent[] events, int prefixLength, WorkflowDefinitionSnapshot snapshot) =>
        StateProjector.Project(new ArraySegment<FlowEvent>(events, 0, prefixLength), snapshot).Status
            == WorkflowStatus.Terminal;
}

/// <summary>#1157: <see cref="TerminalInstantResolver.Resolve"/>'s answer, and why it is absent when it is.</summary>
/// <param name="AtUtc">Non-null exactly when <paramref name="Absence"/> is <see cref="TerminalInstantAbsence.None"/>.</param>
public readonly record struct TerminalInstant(DateTime? AtUtc, TerminalInstantAbsence Absence);

/// <summary>
/// #1157: why a run has no terminal instant. Never collapsed into a single "unknown" — the retention
/// sweep responds differently to each, and telling an operator their journal predates #745 when it
/// does not is worse than saying nothing (spec/baton.md §3).
/// </summary>
public enum TerminalInstantAbsence
{
    /// <summary>An instant was resolved; there is no absence.</summary>
    None,

    /// <summary>
    /// The run has not ended. Includes the crash window: a journal whose terminal event was never
    /// written, or was written only as a torn final line, is a room that has not ended.
    /// </summary>
    NotTerminal,

    /// <summary>
    /// The run is terminal but no journal line made it so — a zero-step snapshot, whose empty prefix
    /// already projects terminal. Nothing was mis-recorded here; there is genuinely no transition to
    /// date.
    /// </summary>
    NoTransitionEntry,

    /// <summary>
    /// The transition line predates writer stamping (#745) and carries no
    /// <see cref="LogEntry.FlowLogEntry.WriterUtcTimestamp"/>. The only case that says anything about
    /// the age of a room's journal, and so the only one worth telling an operator about.
    /// </summary>
    TransitionEntryUnstamped,
}
