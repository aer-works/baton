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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.TurnHostDormancyCleared(clearedBy, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    /// <summary>
    /// Switches the room's workflow on or off (#1216) — a durable room-level fact, so it survives a
    /// restart. Nothing is deleted: the shape, the journal, and every worker stay exactly as they
    /// were, which is what the design corpus means by calling the toggle a "non-event"
    /// (<c>docs/design/02-screens.md</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused while the room has work in flight</b>, with a reason, rather than silently mutating
    /// a room something is still driving — the same shape as the DAG dependency check at
    /// <c>02-screens.md:616-621</c>, which refuses rather than repairing the graph. The escape hatch
    /// there ("Stop Workflow &amp; Remove") is explicitly destructive and confirmed; the bare switch is
    /// not, so it refuses and the person stops the room first.
    /// </para>
    /// <para>
    /// "In flight" is <b>not</b> <c>StepStatus.Running</c>. <see cref="Domain.WorkflowStatus.Running"/>
    /// is defined as a live attempt <em>or</em> a crash before the outcome was recorded (§6), so a
    /// room whose process died days ago is indistinguishable from a live one by the journal alone —
    /// testing it would leave such a room permanently unable to switch off. The honest test, the one
    /// #1219 established for the same reason, is the pair of primitives underneath: the room's §15
    /// flow lock (held only by a live pump, dropped by the OS the instant its holder exits) and any
    /// step actually <see cref="Domain.StepStatus.Paused"/> awaiting a person.
    /// </para>
    /// <para>
    /// This is not a second copy of the UI's <c>RoomCardViewModel.DeriveStatus</c> — <c>Aer.Flow</c>
    /// cannot depend on <c>Aer.Ui.Core</c> (UI spec §2) and does not need to, since both read the same
    /// two primitives. In that vocabulary the refusal is exactly "Running or NeedsYou": a room parked
    /// on a vendor quota is refused because its pump is alive and holding the lock, while a dead one
    /// is permitted.
    /// </para>
    /// <para>
    /// <paramref name="flowReader"/> and <paramref name="snapshot"/> are required rather than
    /// defaulted: a caller that could omit them would silently skip half the rule, which is the exact
    /// defect #1219's review found in a defaulted lock argument. The flow log is read <em>inside</em>
    /// the room-events guard so the paused reading is taken fresh, not handed in already stale.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidRoomMutationException">The room has work in flight.</exception>
    public static async Task<RoomState> SetWorkflowSwitchAsync(
        string roomDirectoryPath,
        bool isOn,
        string switchedBy,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        IEventLogReader flowReader,
        WorkflowDefinitionSnapshot snapshot,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(switchedBy);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(flowReader);
        ArgumentNullException.ThrowIfNull(snapshot);

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

        if (ConcurrencyGuard.IsHeld(roomDirectoryPath))
        {
            throw new InvalidRoomMutationException(
                "This room is running. Stop it before switching its workflow off or on.");
        }

        var flowEvents = await flowReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var flowState = StateProjector.Project(flowEvents, snapshot);
        var pausedStep = flowState.Steps.FirstOrDefault(s => s.Status == StepStatus.Paused);
        if (pausedStep is not null)
        {
            throw new InvalidRoomMutationException(
                $"Step '{pausedStep.StepId}' is waiting on a decision. Answer it, or stop the room, before switching its workflow off or on.");
        }

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.WorkflowSwitched(isOn, switchedBy, ts);
        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);

        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }

    /// <summary>
    /// Journals the room's orchestrator reassignment (0054 §6, #592) — the journal half only. The
    /// <c>SessionMetadata.Participants</c> half (which participant's <c>IsOrchestrator</c> reads
    /// true) is a room.json read-modify-write under a different lock entirely
    /// (<c>SessionTurnLockFor</c>), so it is the caller's job — see
    /// <c>Aer.Daemon.Program</c>'s <c>/api/rooms/orchestrator/reassign</c>, which does both halves in
    /// the order the write-order ruling settled: validate, then metadata, then this journal append,
    /// best-effort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused while the room has work in flight</b>, the same shape and the same reason as
    /// <see cref="SetWorkflowSwitchAsync"/> above — reassigning the room's addressee while a turn is
    /// actually driving it is not a state change that can be reasoned about.
    /// </para>
    /// <para>
    /// <b>The target is validated against this room's own journal</b>, not against room.json —
    /// this method never opens it (the journal-half/metadata-half split above). A worker becomes
    /// "known" the moment its <see cref="RoomEvent.WorkerJoined"/> lands, so scanning the existing
    /// room events for that fact is honestly journal-only, unlike reading <c>Participants</c> would
    /// be.
    /// </para>
    /// <para>
    /// <b>Reassigning to the current holder is a no-op</b> (ruling 3): the current holder is the
    /// most recent <see cref="RoomEvent.OrchestratorAssigned"/> in the journal — every room has one
    /// the moment it exists, appended for the first participant at materialization — and when the
    /// target already matches it, nothing is appended. No new fact occurred, and a no-op journal
    /// line is exactly the noise a later slice would have to filter back out.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidRoomMutationException">The room has work in flight, or <paramref name="workerId"/> never joined this room.</exception>
    public static async Task<RoomState> ReassignOrchestratorAsync(
        string roomDirectoryPath,
        WorkerId workerId,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

        if (ConcurrencyGuard.IsHeld(roomDirectoryPath))
        {
            throw new InvalidRoomMutationException(
                "This room is running. Stop it before reassigning its orchestrator.");
        }

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var knownWorkerIds = existingEvents
            .OfType<RoomEvent.WorkerJoined>()
            .Select(e => e.WorkerId)
            .ToHashSet();
        if (!knownWorkerIds.Contains(workerId))
        {
            throw new InvalidRoomMutationException($"'{workerId.Value}' is not a participant in this room.");
        }

        var currentHolder = existingEvents
            .OfType<RoomEvent.OrchestratorAssigned>()
            .Select(e => (WorkerId?)e.WorkerId)
            .LastOrDefault();
        if (currentHolder == workerId)
        {
            return RoomProjector.Project(existingEvents);
        }

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.OrchestratorAssigned(workerId, ts);
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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

        using var guard = ConcurrencyGuard.AcquireRoomEvents(roomDirectoryPath);

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

    /// <summary>
    /// Journals that a worker's standing permission was withdrawn (#1251). Call only after
    /// <c>bindings.json</c> has actually been rewritten to reflect the withdrawal — this records
    /// the fact, it does not perform it.
    /// <para>
    /// Deliberately does NOT acquire the room-events lock itself, unlike every sibling method
    /// above. The bindings write and this journal append must land under one continuous hold of
    /// that lock (Fable's ruling on #1251: "journal and register can't tell different stories"),
    /// and the lock is a kernel-held <see cref="FileShare.None"/> file handle — not reentrant, so a
    /// second acquire from the same holder fails immediately rather than nesting. The daemon's
    /// revoke route already holds the room-events lock for the whole bindings write; call this
    /// while still inside that same <c>using</c> scope, never standalone.
    /// </para>
    /// <para>
    /// No dedup check against prior events: unlike the ask/answer pair above, a standing permission
    /// has no single id to key one against, and the caller only reaches here on
    /// <c>PermissionRevokeOutcome.Revoked</c>, which is itself idempotent-at-the-register — a
    /// repeat revoke of an already-gone permission resolves to <c>NothingToRevoke</c> before this
    /// is ever called.
    /// </para>
    /// </summary>
    public static async Task<RoomState> RecordStandingPermissionRevokedAsync(
        string roomDirectoryPath,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        string workerName,
        string revokeKind,
        string? shellCommandPattern,
        string revokedBy,
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(workerName);
        ArgumentException.ThrowIfNullOrEmpty(revokeKind);
        ArgumentException.ThrowIfNullOrEmpty(revokedBy);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var roomEvent = new RoomEvent.StandingPermissionRevoked(
            workerName,
            revokeKind,
            shellCommandPattern,
            revokedBy,
            ts);

        await writer.AppendAsync(roomEvent, cancellationToken).ConfigureAwait(false);
        return RoomProjector.Project([.. existingEvents, roomEvent]);
    }
}

