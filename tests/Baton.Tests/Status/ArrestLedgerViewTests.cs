using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// #1530: <see cref="ArrestLedgerProjector.Project"/> against fabricated flow.jsonl/room.jsonl
/// entries directly -- <see cref="ArrestLedgerProjector.ProjectFromRoomAsync"/>'s own file-reading
/// half is exercised indirectly by <c>StatusCommandArrestLedgerEndToEndTests</c>.
/// </summary>
public class ArrestLedgerViewTests
{
    private static readonly ExecutionId ExecA = new("exec-a");
    private static readonly DateTime T1 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 9, 1, 10, 0, 2, DateTimeKind.Utc);

    private static LogEntry.FlowLogEntry Flow(FlowEvent e, DateTime at) => new(e, at);

    [Fact]
    public void A_request_followed_by_ExecutionCancelled_reports_Delivered()
    {
        var entries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA, CancellationOrigin.Operator), T1),
            Flow(new FlowEvent.ExecutionCancelled(ExecA), T2),
        };

        var ledger = ArrestLedgerProjector.Project(entries, []);

        var entry = Assert.Single(ledger);
        Assert.Equal(ExecA, entry.ExecutionId);
        Assert.Equal(ArrestOutcome.Delivered, entry.Outcome);
        Assert.Equal("operator", entry.RequestedBy);
        Assert.Equal(T1, entry.RequestedAtUtc.UtcDateTime);
        Assert.Equal(T2, entry.ResolvedAtUtc!.Value.UtcDateTime);
    }

    [Fact]
    public void A_request_followed_by_CancellationRejected_reports_Rejected_with_reason()
    {
        var entries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA), T1),
            Flow(new FlowEvent.CancellationRejected(ExecA, "not yet confirmed settled"), T2),
        };

        var ledger = ArrestLedgerProjector.Project(entries, []);

        var entry = Assert.Single(ledger);
        Assert.Equal(ArrestOutcome.Rejected, entry.Outcome);
        Assert.Equal("not yet confirmed settled", entry.Reason);
    }

    // Polarity control for the two arms above: a request with no terminal follow-up yet must report
    // no Outcome at all, not default to either Delivered or Rejected.
    [Fact]
    public void A_request_with_no_terminal_event_yet_reports_no_outcome()
    {
        var entries = new LogEntry[]
        {
            Flow(new FlowEvent.CancellationRequested(ExecA), T1),
        };

        var ledger = ArrestLedgerProjector.Project(entries, []);

        var entry = Assert.Single(ledger);
        Assert.Null(entry.Outcome);
        Assert.Null(entry.ResolvedAtUtc);
    }

    [Fact]
    public void HostStop_origin_renders_as_host_stop_not_operator()
    {
        var entries = new LogEntry[] { Flow(new FlowEvent.CancellationRequested(ExecA, CancellationOrigin.HostStop), T1) };

        var entry = Assert.Single(ArrestLedgerProjector.Project(entries, []));

        Assert.Equal("host-stop", entry.RequestedBy);
    }

    [Fact]
    public void ArrestRequestUnresolvable_room_event_becomes_a_Rejected_entry_with_no_ExecutionId()
    {
        var roomEvents = new RoomEvent[]
        {
            new RoomEvent.ArrestRequestUnresolvable("latest", "ambiguous — 2 candidates", T1, T2),
        };

        var entry = Assert.Single(ArrestLedgerProjector.Project([], roomEvents));

        Assert.Equal("latest", entry.Target);
        Assert.Null(entry.ExecutionId);
        Assert.Equal(ArrestOutcome.Rejected, entry.Outcome);
        Assert.Equal("ambiguous — 2 candidates", entry.Reason);
    }

    [Fact]
    public void ArrestRequestExpired_room_event_becomes_an_Expired_entry()
    {
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestRequestExpired("exec-x", T1, T2) };

        var entry = Assert.Single(ArrestLedgerProjector.Project([], roomEvents));

        Assert.Equal(ArrestOutcome.Expired, entry.Outcome);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public void Entries_are_ordered_by_RequestedAtUtc_across_both_logs()
    {
        var flowEntries = new LogEntry[] { Flow(new FlowEvent.CancellationRequested(ExecA), T2) };
        var roomEvents = new RoomEvent[] { new RoomEvent.ArrestRequestExpired("exec-x", T1, T1) };

        var ledger = ArrestLedgerProjector.Project(flowEntries, roomEvents);

        Assert.Equal(2, ledger.Count);
        Assert.Equal("exec-x", ledger[0].Target);
        Assert.Equal(ExecA.Value, ledger[1].Target);
    }
}
