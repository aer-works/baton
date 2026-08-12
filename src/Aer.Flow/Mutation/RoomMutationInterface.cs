using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// The single mutation interface for holding-room journal changes (<c>room.jsonl</c>).
/// Enforces single-writer discipline and §15 concurrency locking.
/// </summary>
public static class RoomMutationInterface
{
    public static async Task<RoomState> DispatchHeldWorkAsync(
        string roomDirectoryPath,
        HeldWorkRef @ref,
        string shape,
        TimeSpan budget,
        string deciderIdentity,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(shape);
        ArgumentException.ThrowIfNullOrEmpty(deciderIdentity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        // Rooted only: the ref is read later by processes with different working directories
        // (the daemon's watch set, a status reader), and a relative path silently resolves
        // against whichever one is reading -- the same class of bug dispatch.py's own header
        // records for relative room dirs.
        if (!Path.IsPathRooted(@ref.AsWorkflowDirectoryPath()))
        {
            throw new InvalidRoomMutationException(
                $"HeldWorkRef '{@ref}' is not an absolute path; a relative workflow directory would resolve against the reading process's working directory.");
        }

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        if (currentState.HeldWork.ContainsKey(@ref))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' has already been dispatched in this room.");
        }

        var roomEvent = new RoomEvent.HeldWorkDispatched(@ref, shape, budget, deciderIdentity);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> EscalateHeldWorkAsync(
        string roomDirectoryPath,
        HeldWorkRef what,
        string toWhom,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(toWhom);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        if (!currentState.HeldWork.TryGetValue(what, out var item))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{what}' was not found in this room.");
        }

        if (item.Status == HeldWorkStatus.Resolved)
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{what}' is already resolved and cannot be escalated.");
        }

        var roomEvent = new RoomEvent.HeldWorkEscalated(what, toWhom);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> ResolveHeldWorkAsync(
        string roomDirectoryPath,
        HeldWorkRef @ref,
        HeldWorkCitation citation,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        return await ResolveHeldWorkLockedAsync(@ref, citation, existingEvents, currentState, writer, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The validate-and-append half of <see cref="ResolveHeldWorkAsync"/>, split out so
    /// <see cref="MemoryProposalResolution"/> can hold the SAME <see cref="ConcurrencyGuard"/>
    /// across its own "is this already resolved" check, its <c>memory/</c> file write, and this
    /// append -- three steps that must not interleave with a concurrent resolver (#672 review: a
    /// caller that checked status, released the lock, then separately called
    /// <see cref="ResolveHeldWorkAsync"/> left a window where a second resolve could apply a
    /// memory-proposal write before this method's own already-resolved check ever ran). Internal:
    /// callers outside this project acquire the lock via <see cref="ResolveHeldWorkAsync"/>
    /// instead, which still exists for every resolver that has no extra locked work to do.
    /// </summary>
    internal static async Task<RoomState> ResolveHeldWorkLockedAsync(
        HeldWorkRef @ref,
        HeldWorkCitation citation,
        IReadOnlyList<RoomEvent> existingEvents,
        RoomState currentState,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        if (!currentState.HeldWork.TryGetValue(@ref, out var item))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' was not found in this room.");
        }

        if (item.Status == HeldWorkStatus.Resolved)
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' is already resolved.");
        }

        var roomEvent = new RoomEvent.HeldWorkResolved(@ref, citation);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> RecordGrantAsync(
        string roomDirectoryPath,
        GrantId grantId,
        WorkerId workerId,
        GrantLevel level,
        GrantScope scope,
        SpendBounds spendBounds,
        string grantor,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(grantor);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(spendBounds);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        if (currentState.ActiveGrants.ContainsKey(grantId))
        {
            throw new InvalidRoomMutationException($"GrantId '{grantId}' already exists in active grants.");
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.GrantRecorded(grantId, workerId, level, scope, spendBounds, grantor, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> AmendGrantAsync(
        string roomDirectoryPath,
        GrantId grantId,
        GrantId amendsGrantId,
        WorkerId workerId,
        GrantLevel level,
        GrantScope scope,
        SpendBounds spendBounds,
        string grantor,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(grantor);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(spendBounds);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        if (!currentState.ActiveGrants.ContainsKey(amendsGrantId))
        {
            throw new InvalidRoomMutationException($"GrantId '{amendsGrantId}' to amend was not found in active grants.");
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.GrantAmended(grantId, amendsGrantId, workerId, level, scope, spendBounds, grantor, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> RevokeGrantAsync(
        string roomDirectoryPath,
        GrantId grantId,
        string revoker,
        string reason,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(revoker);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        var isDirectActive = currentState.ActiveGrants.ContainsKey(grantId);
        var isBaseActive = currentState.ActiveGrants.Any(kv => kv.Value.BaseGrantId == grantId);

        if (!isDirectActive && !isBaseActive)
        {
            throw new InvalidRoomMutationException($"GrantId '{grantId}' to revoke was not found in active grants.");
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.GrantRevoked(grantId, revoker, ts, reason);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> RaiseEscalationAsync(
        string roomDirectoryPath,
        WorkerId fromWorkerId,
        EscalationTrigger trigger,
        EscalationSubject subject,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.EscalationRaised(fromWorkerId, trigger, subject, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> EnterTurnHostDormancyAsync(
        string roomDirectoryPath,
        int consecutiveFailures,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.TurnHostDormancyEntered(consecutiveFailures, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> ClearTurnHostDormancyAsync(
        string roomDirectoryPath,
        string clearedBy,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(clearedBy);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.TurnHostDormancyCleared(clearedBy, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> RaisePermissionAsync(
        string roomDirectoryPath,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        string permissionRequestId,
        ExecutionId executionId,
        StepId stepId,
        string workerId,
        string vendorTag,
        string vendorCorrelationId,
        string toolName,
        string toolInputJson,
        string category,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(permissionRequestId);
        ArgumentException.ThrowIfNullOrEmpty(workerId);
        ArgumentException.ThrowIfNullOrEmpty(vendorTag);
        // Empty is allowed: the correlation id is the vendor RESUME hint (0015), not the answer route
        // (that goes via the answer file keyed by PermissionRequestId). It is frequently unknown at raise
        // time -- crash reconciliation recovers only the ask file, and a live turn may not yet hold a
        // vendor session id -- so requiring it non-empty would break both the doorbell and reconcile.
        ArgumentNullException.ThrowIfNull(vendorCorrelationId);
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentException.ThrowIfNullOrEmpty(toolInputJson);
        ArgumentException.ThrowIfNullOrEmpty(category);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        bool alreadyPresent = existingEvents.Any(e => e switch
        {
            RoomEvent.RuntimePermissionAsked asked => asked.PermissionRequestId == permissionRequestId,
            RoomEvent.RuntimePermissionAnswered answered => answered.PermissionRequestId == permissionRequestId,
            RoomEvent.RuntimePermissionRevoked revoked => revoked.PermissionRequestId == permissionRequestId,
            _ => false
        });

        if (alreadyPresent)
        {
            return currentState;
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.RuntimePermissionAsked(
            permissionRequestId,
            executionId,
            stepId,
            workerId,
            vendorTag,
            vendorCorrelationId,
            toolName,
            toolInputJson,
            category,
            ts);

        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);
        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> AnswerPermissionAsync(
        string roomDirectoryPath,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        string permissionRequestId,
        string decisionKind,
        string? updatedInputJson,
        string? reason,
        string deciderIdentity,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(permissionRequestId);
        ArgumentException.ThrowIfNullOrEmpty(decisionKind);
        ArgumentException.ThrowIfNullOrEmpty(deciderIdentity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        bool alreadyAnswered = existingEvents.Any(e => e switch
        {
            RoomEvent.RuntimePermissionAnswered answered => answered.PermissionRequestId == permissionRequestId,
            RoomEvent.RuntimePermissionRevoked revoked => revoked.PermissionRequestId == permissionRequestId,
            _ => false
        });

        if (alreadyAnswered)
        {
            return currentState;
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.RuntimePermissionAnswered(
            permissionRequestId,
            decisionKind,
            updatedInputJson,
            reason,
            deciderIdentity,
            ts);

        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);
        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    public static async Task<RoomState> RevokePermissionAsync(
        string roomDirectoryPath,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        string permissionRequestId,
        string reason,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(permissionRequestId);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var currentState = RoomProjector.Project(existingEvents);

        bool alreadyResolved = existingEvents.Any(e => e switch
        {
            RoomEvent.RuntimePermissionAnswered answered => answered.PermissionRequestId == permissionRequestId,
            RoomEvent.RuntimePermissionRevoked revoked => revoked.PermissionRequestId == permissionRequestId,
            _ => false
        });

        if (alreadyResolved)
        {
            return currentState;
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.RuntimePermissionRevoked(
            permissionRequestId,
            reason,
            ts);

        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);
        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }
}

