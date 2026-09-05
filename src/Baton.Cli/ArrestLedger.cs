using Baton.Concurrency;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli;

/// <summary>
/// The append-only room-fact half of <c>cancel.request</c>. A live pump keeps <c>flow.lock</c>
/// for its whole run, so this uses the room journal's independent writer rather than attempting a
/// second Flow writer; readers therefore see the request even before the pump can deliver it.
/// </summary>
internal static class ArrestLedger
{
    private static readonly TimeSpan LockContentionBudget = TimeSpan.FromSeconds(2);

    public static Task RecordRequestedAsync(
        string roomDirectoryPath,
        string requestId,
        string target,
        string requestedBy,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken) =>
        RecordAsync(
            roomDirectoryPath,
            requestId,
            target,
            requestedBy,
            requestedAt,
            executionId: null,
            terminalState: null,
            reason: null,
            cancellationToken);

    public static Task RecordDeliveredAsync(
        string roomDirectoryPath,
        string requestId,
        string target,
        string requestedBy,
        DateTimeOffset requestedAt,
        ExecutionId executionId,
        CancellationToken cancellationToken) =>
        RecordAsync(
            roomDirectoryPath,
            requestId,
            target,
            requestedBy,
            requestedAt,
            executionId,
            ArrestLedgerStates.Delivered,
            reason: null,
            cancellationToken);

    public static Task RecordRejectedAsync(
        string roomDirectoryPath,
        string requestId,
        string target,
        string requestedBy,
        DateTimeOffset requestedAt,
        ExecutionId? executionId,
        string reason,
        CancellationToken cancellationToken) =>
        RecordAsync(
            roomDirectoryPath,
            requestId,
            target,
            requestedBy,
            requestedAt,
            executionId,
            ArrestLedgerStates.Rejected,
            reason,
            cancellationToken);

    public static Task RecordExpiredAsync(
        string roomDirectoryPath,
        string requestId,
        string target,
        string requestedBy,
        DateTimeOffset requestedAt,
        ExecutionId? executionId,
        string reason,
        CancellationToken cancellationToken) =>
        RecordAsync(
            roomDirectoryPath,
            requestId,
            target,
            requestedBy,
            requestedAt,
            executionId,
            ArrestLedgerStates.Expired,
            reason,
            cancellationToken);

    private static async Task RecordAsync(
        string roomDirectoryPath,
        string requestId,
        string target,
        string requestedBy,
        DateTimeOffset requestedAt,
        ExecutionId? executionId,
        string? terminalState,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(requestedBy);

        using var guard = ConcurrencyGuard.AcquireRoomEventsWithin(
            roomDirectoryPath,
            LockContentionBudget,
            "cancel.request arrest ledger");

        var roomLogPath = Path.Combine(roomDirectoryPath, BatonPaths.RoomLogFileName);
        var events = await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var existing = RoomProjector.Project(events).Arrests.FirstOrDefault(record => record.RequestId == requestId);

        await using var writer = new RoomEventLogWriter(roomLogPath);
        if (existing is null)
        {
            await writer.AppendAsync(
                    new RoomEvent.ArrestRequested(requestId, target, requestedBy, requestedAt),
                    cancellationToken)
                .ConfigureAwait(false);
            existing = new ArrestRecord(requestId, target, requestedBy, requestedAt);
        }

        if (terminalState is null || ArrestLedgerStates.IsTerminal(existing.State))
        {
            return;
        }

        RoomEvent terminalEvent = terminalState switch
        {
            ArrestLedgerStates.Delivered when executionId is { } deliveredExecutionId =>
                new RoomEvent.ArrestDelivered(requestId, deliveredExecutionId, DateTimeOffset.UtcNow),
            ArrestLedgerStates.Rejected =>
                new RoomEvent.ArrestRejected(requestId, executionId, reason ?? "arrest request rejected", DateTimeOffset.UtcNow),
            ArrestLedgerStates.Expired =>
                new RoomEvent.ArrestExpired(requestId, executionId, reason ?? "arrest request expired", DateTimeOffset.UtcNow),
            _ => throw new ArgumentOutOfRangeException(nameof(terminalState), terminalState, "Unknown arrest terminal state."),
        };

        await writer.AppendAsync(terminalEvent, cancellationToken).ConfigureAwait(false);
    }
}
