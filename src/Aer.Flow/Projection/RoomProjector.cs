using Aer.Flow.Domain;

namespace Aer.Flow.Projection;

/// <summary>
/// Reconstructs <see cref="RoomState"/> from room event history (<c>room.jsonl</c>):
/// <c>RoomState = RoomProjector.Project(events)</c>. A pure function — no I/O, no filesystem
/// access — so identical event lists produce byte-identical projection output.
/// </summary>
public static class RoomProjector
{
    public static RoomState Project(IReadOnlyList<RoomEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var heldWork = new Dictionary<HeldWorkRef, HeldWorkState>();
        var activeGrants = new Dictionary<GrantId, GrantState>();
        var openEscalations = new List<RoomEvent.EscalationRaised>();
        var unmatchedEntries = new List<string>();
        var isDormant = false;
        PendingPermission? pendingPermission = null;
        var askedPermissions = new Dictionary<string, (string ToolName, string Category)>(StringComparer.Ordinal);
        var permissionAnswers = new List<PermissionAnswer>();
        var dormancyTransitions = new List<DormancyTransition>();
        // Ids already answered or revoked. A permission ask can be journaled AFTER its resolution — the
        // MCP host writes the ask file and the daemon appends `Asked` asynchronously, while the answer
        // path appends `Answered` directly, so an automated/fast answer (or crash reconciliation) can
        // invert the order. Without this set a late `Asked` would set a gate that is already closed and
        // it would hang open forever (advisor-caught). The projector must be order-robust.
        var resolvedPermissionIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var roomEvent in events)
        {
            switch (roomEvent)
            {
                case RoomEvent.HeldWorkDispatched dispatched:
                    heldWork[dispatched.Ref] = new HeldWorkState(
                        dispatched.Ref,
                        dispatched.Shape,
                        dispatched.Budget,
                        dispatched.DeciderIdentity,
                        HeldWorkStatus.Dispatched);
                    break;

                case RoomEvent.HeldWorkEscalated escalated:
                    if (heldWork.TryGetValue(escalated.Ref, out var existingEscalated))
                    {
                        heldWork[escalated.Ref] = existingEscalated with
                        {
                            Status = HeldWorkStatus.Escalated,
                            EscalatedTo = escalated.ToWhom
                        };
                    }
                    else
                    {
                        unmatchedEntries.Add($"heldWorkEscalated for unknown ref '{escalated.Ref}'");
                    }

                    break;

                case RoomEvent.HeldWorkResolved resolved:
                    if (heldWork.TryGetValue(resolved.Ref, out var existingResolved))
                    {
                        heldWork[resolved.Ref] = existingResolved with
                        {
                            Status = HeldWorkStatus.Resolved,
                            Citation = resolved.Citation
                        };
                    }
                    else
                    {
                        unmatchedEntries.Add($"heldWorkResolved for unknown ref '{resolved.Ref}'");
                    }

                    break;

                case RoomEvent.GrantRecorded recorded:
                    activeGrants[recorded.GrantId] = new GrantState(
                        recorded.GrantId,
                        null,
                        recorded.WorkerId,
                        recorded.Level,
                        recorded.Scope,
                        recorded.SpendBounds,
                        recorded.Grantor,
                        recorded.Timestamp);
                    break;

                case RoomEvent.GrantAmended amended:
                    if (activeGrants.TryGetValue(amended.AmendsGrantId, out var targetGrant))
                    {
                        var baseId = targetGrant.BaseGrantId ?? amended.AmendsGrantId;
                        activeGrants.Remove(amended.AmendsGrantId);
                        activeGrants[amended.GrantId] = new GrantState(
                            amended.GrantId,
                            baseId,
                            amended.WorkerId,
                            amended.Level,
                            amended.Scope,
                            amended.SpendBounds,
                            amended.Grantor,
                            amended.Timestamp);
                    }
                    else
                    {
                        unmatchedEntries.Add($"grantAmended for unknown amendsGrantId '{amended.AmendsGrantId}'");
                    }

                    break;

                case RoomEvent.GrantRevoked revoked:
                    if (activeGrants.Remove(revoked.GrantId))
                    {
                        // Removed directly by active GrantId.
                    }
                    else
                    {
                        var keyToRemove = activeGrants.FirstOrDefault(kv => kv.Value.BaseGrantId == revoked.GrantId).Key;
                        if (keyToRemove != default && activeGrants.Remove(keyToRemove))
                        {
                            // Removed by base GrantId.
                        }
                        else
                        {
                            unmatchedEntries.Add($"grantRevoked for unknown grantId '{revoked.GrantId}'");
                        }
                    }

                    break;

                case RoomEvent.EscalationRaised escalation:
                    openEscalations.Add(escalation);

                    // #1178: the breaker's escalation is journaled AFTER its TurnHostDormancyEntered
                    // event (RoomTurnHost.ExecuteSingleTickAsync writes entered first, then raises the
                    // escalation on the same writer), so the entered transition starts with a null
                    // Detail and the matching escalation backfills it here. Gating on the LAST
                    // transition being an entered one with no detail yet keeps the pairing within the
                    // current dormancy episode — after a cleared event the last transition is the
                    // cleared one, so a stale escalation from an earlier episode can never attach to a
                    // later entry (the cross-episode misattribution the #1178 review caught in the
                    // original backward-looking pairing).
                    if (escalation.Subject is EscalationSubject.HostCondition hostCondition
                        && hostCondition.Condition == RoomEvent.TurnHostDormancyEntered.DormancyConditionName
                        && dormancyTransitions.Count > 0
                        && dormancyTransitions[^1] is { IsEntered: true, Detail: null } enteredAwaitingDetail)
                    {
                        dormancyTransitions[^1] = enteredAwaitingDetail with { Detail = hostCondition.Detail };
                    }

                    break;

                case RoomEvent.TurnHostDormancyEntered entered:
                    isDormant = true;
                    dormancyTransitions.Add(new DormancyTransition(
                        IsEntered: true,
                        ConsecutiveFailures: entered.ConsecutiveFailures,
                        Detail: null,
                        ClearedBy: null,
                        Timestamp: entered.Timestamp));
                    break;

                case RoomEvent.TurnHostDormancyCleared cleared:
                    isDormant = false;
                    dormancyTransitions.Add(new DormancyTransition(
                        IsEntered: false,
                        ConsecutiveFailures: 0,
                        Detail: null,
                        ClearedBy: cleared.ClearedBy,
                        Timestamp: cleared.Timestamp));
                    break;

                case RoomEvent.RuntimePermissionAsked asked:
                    askedPermissions[asked.PermissionRequestId] = (asked.ToolName, asked.Category);

                    // A gate already resolved (in any order) never re-opens.
                    if (!resolvedPermissionIds.Contains(asked.PermissionRequestId))
                    {
                        pendingPermission = new PendingPermission(
                            asked.PermissionRequestId,
                            asked.WorkerId,
                            asked.VendorTag,
                            asked.ToolName,
                            asked.ToolInputJson,
                            asked.Category,
                            asked.AskedAt);
                    }

                    break;

                case RoomEvent.RuntimePermissionAnswered answered:
                    resolvedPermissionIds.Add(answered.PermissionRequestId);
                    if (pendingPermission != null && pendingPermission.PermissionRequestId == answered.PermissionRequestId)
                    {
                        pendingPermission = null;
                    }

                    var (toolName, category) = askedPermissions.TryGetValue(answered.PermissionRequestId, out var askedInfo)
                        ? askedInfo
                        : ("(unknown)", "");

                    permissionAnswers.Add(new PermissionAnswer(
                        answered.PermissionRequestId,
                        toolName,
                        category,
                        answered.DecisionKind,
                        answered.Reason,
                        answered.DeciderIdentity,
                        answered.AnsweredAt,
                        WasRevoked: false));

                    if (permissionAnswers.Count > 50)
                    {
                        permissionAnswers.RemoveAt(0);
                    }

                    break;

                case RoomEvent.RuntimePermissionRevoked revoked:
                    resolvedPermissionIds.Add(revoked.PermissionRequestId);
                    if (pendingPermission != null && pendingPermission.PermissionRequestId == revoked.PermissionRequestId)
                    {
                        pendingPermission = null;
                    }

                    var (revokedToolName, revokedCategory) = askedPermissions.TryGetValue(revoked.PermissionRequestId, out var revokedAskedInfo)
                        ? revokedAskedInfo
                        : ("(unknown)", "");

                    permissionAnswers.Add(new PermissionAnswer(
                        revoked.PermissionRequestId,
                        revokedToolName,
                        revokedCategory,
                        "",
                        revoked.Reason,
                        "",
                        revoked.RevokedAt,
                        WasRevoked: true));

                    if (permissionAnswers.Count > 50)
                    {
                        permissionAnswers.RemoveAt(0);
                    }

                    break;
            }
        }

        return new RoomState(heldWork, unmatchedEntries, activeGrants, openEscalations, isDormant, pendingPermission, permissionAnswers, dormancyTransitions);

    }
}
