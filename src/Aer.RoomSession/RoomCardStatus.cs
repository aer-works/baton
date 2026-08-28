using Aer.Flow.Domain;
using Aer.Flow.Projection;

namespace Aer.RoomSession;

/// <summary>
/// The shared room-status derivation (originally Home's card read model, #187). Home's recents cards
/// retired with #1071, and the instance side (the card object and its Open command) went with them;
/// what remains is static — the one place a <see cref="RoomProjection"/> becomes a plain status line
/// and a <see cref="RoomCardStatus"/>, consumed by the switcher rows, the fleet loader, and mobile.
/// </summary>
public static class RoomCardViewModel
{
    /// <summary>The room's handle — the directory's leaf name (ux-principles §3), via the one canonical <see cref="RoomProjectionLoader.FriendlyNameFor"/> derivation, so the switcher, the inbox items, and the chat header can never show a room three different names (#461/#976).</summary>
    public static string TitleFor(string roomDirectoryPath)
        => RoomProjectionLoader.FriendlyNameFor(roomDirectoryPath);

    /// <summary>
    /// The one place a <see cref="RoomProjection"/> becomes a human status line and a
    /// <see cref="RoomCardStatus"/>. Shared with the #336 switcher's rows rather than duplicated:
    /// the same surfaces that made #458's marks disagree across toolkits would make two copies of
    /// this disagree across views — Home would say "Cancelled" while the switcher said "Finished",
    /// which is the exact defect #461 had just fixed in one place.
    /// Extended (#1112): receives <paramref name="pendingPermission"/> projected from <c>room.jsonl</c>
    /// so a live permission ask derives <see cref="RoomCardStatus.NeedsYou"/> and "Permission requested".
    /// Extended (#1116, 0026 §1/§3/§5): an exhausted step must NOT rank the room NeedsYou (0026 §1).
    /// A room whose ONLY failures are exhaustion carries the 0026 sentence shape ("Out of plan —
    /// resumes ...", latest instant across exhausted steps, honest unknown if any is unknown) as
    /// <see cref="RoomCardStatus.OutOfPlan"/>, muted (0018 band 4). Note the arm only ever fires
    /// while Running: an unresolved exhausted step keeps CanStillDeliver alive, so Terminal is
    /// unreachable for it — and a mixed room (exhausted + genuinely failed/rejected) says "Failed",
    /// because that sibling would otherwise hide behind an eternal "Working".
    /// <para>
    /// Extended (#1219) with <paramref name="isFlowLockHeld"/> — the room's §15 flow lock, from
    /// <see cref="Aer.Flow.Concurrency.ConcurrencyGuard.IsHeld"/>. This is the one input here that is
    /// not in the projection, and it has to be: <see cref="WorkflowStatus.Running"/> means, by its own
    /// definition, either a live attempt <em>or</em> a crash before the outcome was recorded, so no
    /// predicate over the journal can separate them. The lock can, because the OS drops it the instant
    /// its holder exits. Callers pass it rather than this method reading it, so this stays pure and
    /// testable in both polarities, and so the probe's cost sits where the caller can see it.
    /// </para>
    /// </summary>
    /// <param name="isFlowLockHeld">
    /// Whether a live pump currently holds this room's flow lock. Callers that genuinely cannot
    /// answer should pass <c>true</c>: that yields today's behaviour exactly (a `Running` room reads
    /// as working), so an unknown never invents a Stopped room out of nothing.
    /// </param>
    /// <param name="isWaitingToStart">
    /// Whether <see cref="Aer.Flow.Concurrency.ConcurrencySlotGate"/> currently holds this room's
    /// turn dispatch queued. Not defaulted, same reasoning as <paramref name="isFlowLockHeld"/>'s own
    /// doc note. See decisions/0020's 2026-08-16 amendment for the full rationale, including why this
    /// arm runs first.
    /// </param>
    public static (string StatusText, RoomCardStatus Status) DeriveStatus(
        RoomProjection projection, PendingPermission? pendingPermission, bool isFlowLockHeld,
        bool isWaitingToStart)
    {
        if (isWaitingToStart)
        {
            return ("Waiting to start", RoomCardStatus.WaitingToStart);
        }

        var failedOrRejectedSteps = projection.State.Steps
            .Where(s => s.Status is StepStatus.Failed or StepStatus.Rejected)
            .ToList();

        var exhaustedSteps = failedOrRejectedSteps
            .Where(s => s.Status == StepStatus.Failed && s.LatestFailureClassification == FailureClassification.ExhaustedUntil)
            .ToList();

        var isOnlyBlockerExhaustion = failedOrRejectedSteps.Count > 0 &&
            exhaustedSteps.Count == failedOrRejectedSteps.Count;

        return projection.State.Status switch
        {
            // Running-scoped on purpose (#1112 review): a live answerable gate only exists while a
            // turn is executing. Revocation (#1102) is best-effort and reconcile is a single startup
            // pass (#1113), so an orphaned ask CAN sit in room.jsonl beside a Paused/Terminal flow
            // state — and headlining "Permission requested" there would mask the room's true status
            // with a gate no worker is left to be released by.
            // #1219, and deliberately the FIRST Running arm — every one below it assumes something is
            // actually running, and for a room whose process died none of them is true:
            //
            //  - It beats the permission arm below, whose own comment already names this hazard for
            //    the Paused/Terminal case: an orphaned ask must not "mask the room's true status with
            //    a gate no worker is left to be released by". A dead room is exactly that, and until
            //    the lock was consulted there was no way to know. A LIVE gate still wins, because the
            //    lock is held while its turn executes, so this arm cannot fire.
            //  - It beats the out-of-plan arms, which would otherwise promise "resumes 14:32" for a
            //    room where nothing is left to do the resuming — the misleading-optimistic timestamp
            //    0026 §5 is written against, arrived at from the other direction.
            //
            // The paused scan is a step scan, not `Status == Paused`: one step still Running forces
            // the whole workflow Running, so a crashed room with a live gate on a sibling branch would
            // slip past a status test and get a Stopped label beside a decision the person can answer.
            //
            // A genuine failure still outranks it, which is the third consequence of this ordering and
            // the one a second reader had to find (the two above were reasoned about; this was not).
            // The two above each replace an optimistic "still in progress" reading with an honest one.
            // This one would replace an already-conclusive verdict: a room mixing an exhausted step
            // with a separately, permanently failed one reaches WorkflowStatus.Running with nothing
            // actually Running, so a crash mid-park frees the lock and "Stopped" would drop the
            // sibling's recorded failure off the headline — and invite a Resume straight back into it.
            // Exhaustion alone is not that: it is a wait, so a room blocked only on quota does fall
            // through to Stopped once nothing is left to serve the wait.
            WorkflowStatus.Running when !isFlowLockHeld
                && !projection.State.Steps.Any(s => s.Status == StepStatus.Paused)
                && (failedOrRejectedSteps.Count == 0 || isOnlyBlockerExhaustion)
                => ("Stopped", RoomCardStatus.Stopped),
            WorkflowStatus.Running when pendingPermission != null => ("Permission requested", RoomCardStatus.NeedsYou),
            WorkflowStatus.Paused => (PausedCardStatusText(projection), RoomCardStatus.NeedsYou),
            WorkflowStatus.Running when projection.State.Steps.FirstOrDefault(s => s.Status == StepStatus.Running) is { } runningStep
                => ($"Working — {runningStep.StepId.Value}", RoomCardStatus.Running),
            // #1299: isFlowLockHeld is true but nothing in the journal explains it (no step
            // Running, nothing failed/rejected) — a foreign process holds the lock, never another
            // room (identity is directory-keyed, #495/#1296). Rationale for the Running-only scope
            // is decision 0020's own amendment record.
            WorkflowStatus.Running when isFlowLockHeld
                && !projection.State.Steps.Any(s => s.Status == StepStatus.Running)
                && failedOrRejectedSteps.Count == 0
                => ("Waiting on another process's lock", RoomCardStatus.WaitingOnLock),
            _ when isOnlyBlockerExhaustion => FormatExhaustedRoomStatus(exhaustedSteps),
            // #1116 review must-fix: an unresolved ExhaustedUntil step keeps CanStillDeliver — and
            // so WorkflowStatus.Running — alive FOREVER (RetryEngine.MayRetry bypasses attempts for
            // it), so a genuinely failed/rejected sibling would otherwise hide behind "Working"
            // indefinitely, never reaching Terminal's "Failed" arm. Scoped to rooms that HAVE an
            // exhausted step: a plain transient-retry room (no exhaustion) keeps today's "Working"
            // while its backoff is genuinely in flight.
            _ when exhaustedSteps.Count > 0 && !isOnlyBlockerExhaustion => ("Failed", RoomCardStatus.Failed),
            WorkflowStatus.Running => ("Working", RoomCardStatus.Running),
            _ when failedOrRejectedSteps.Count > 0
                => ("Failed", RoomCardStatus.Failed),
            // #461: a cancelled run has no WorkflowStatus of its own — it reaches Terminal like any
            // other, which is exactly why it used to fall through to "Finished" and tell you a room
            // you had just stopped had completed. Cancellation is only visible in the steps. Ordered
            // after Failed on purpose: if something failed *and* something was cancelled, the
            // failure is the more important truth about the run.
            _ when projection.State.Steps.Any(s => s.Status == StepStatus.Cancelled)
                => ("Cancelled", RoomCardStatus.Cancelled),
            _ => ("Finished", RoomCardStatus.Finished),
        };
    }

    private static (string StatusText, RoomCardStatus Status) FormatExhaustedRoomStatus(IReadOnlyList<StepState> exhaustedSteps)
    {
        // #1116 review: the room cannot fully resume until EVERY exhausted step clears, so the
        // honest instant is the LATEST across them — declaration order was arbitrary, and showing
        // an earlier step's sooner reset understates the wait (the misleading-optimistic timestamp
        // 0026 §5 is written against). Any step with an unknown instant makes the whole room's
        // answer unknown, for the same reason.
        var instants = exhaustedSteps
            .Select(s => s.RetryNotBefore ?? s.LatestExecutionFailedRetryNotBefore)
            .ToList();

        var text = instants.Count > 0 && instants.All(i => i is not null)
            ? $"Out of plan — resumes {instants.Max()!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
            : "Out of plan — reset unknown";
        return (text, RoomCardStatus.OutOfPlan);
    }

    // #334: a paused chat turn is "your turn to reply", not an approval gate. A card whose only
    // paused steps are NeedsInput says so; any genuine ReadyForReview gate among them keeps the
    // established approval wording (and its exact string, which NavigationShellTests pins).
    private static string PausedCardStatusText(RoomProjection projection)
        => projection.State.Steps.Any(step =>
               step.Status == StepStatus.Paused &&
               PauseKind.ForStep(projection, step.StepId) == PausePointKind.ReadyForReview)
            ? "Waiting for your review"
            : "Waiting for your reply";
}

/// <summary>
/// Resolves a paused step's declared <see cref="PausePointKind"/> from the bound snapshot (#334) —
/// the single lookup every Home surface shares. Defaults to <see cref="PausePointKind.ReadyForReview"/>
/// for any step lacking a pause point, so a pause persisted before the kind existed keeps the
/// approval-gate meaning every pause historically carried.
/// </summary>
internal static class PauseKind
{
    public static PausePointKind ForStep(RoomProjection projection, StepId stepId)
        => projection.Snapshot.Steps.FirstOrDefault(step => step.StepId == stepId)?.PausePoint?.Kind
           ?? PausePointKind.ReadyForReview;
}

/// <summary>The one status system's card-level states — carried as data so the skin styles them consistently (color + icon + word, never color alone).</summary>
public enum RoomCardStatus
{
    Running,
    NeedsYou,
    Finished,
    Failed,

    /// <summary>
    /// The run was stopped on purpose (#461). Previously absent, which meant a cancelled room fell
    /// through to <see cref="Finished"/> — the UI told you a room you had just stopped had finished.
    /// Deliberately distinct from <see cref="Failed"/>: "you stopped it" is not "it broke", and a
    /// list that renders them alike reads far more alarming than reality.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The tenth canonical state (#1219). What it means, why it could not be read off the journal,
    /// and why it is separate from <see cref="Cancelled"/> are all in 0020's 2026-08-14 amendment;
    /// this member is the value that record defines, and <see cref="DeriveStatus"/> is where it is
    /// produced.
    /// </summary>
    Stopped,

    /// <summary>§3's stale list state: recorded in Local UI Configuration but no longer loadable — greyed, never an error.</summary>
    Unavailable,

    /// <summary>
    /// 0026 (#1116): the room's only blocker is vendor-plan exhaustion — waiting on a reset, not
    /// broken and not stopped. Deliberately its own member: <see cref="Cancelled"/> claims "you
    /// stopped it" and <see cref="Failed"/> claims "it broke", and this state is neither. Styled
    /// muted (0018 band 4 for a background room); the status text carries the reset instant or an
    /// honest "reset unknown" (0026 §5).
    /// </summary>
    OutOfPlan,

    /// <summary>
    /// #1296: the daemon's global/per-vendor concurrency cap refused this room's turn a slot, and it
    /// sits FIFO-queued in memory — not started, not failed, not stopped. Ephemeral by design: a
    /// daemon restart drops the in-memory queue entirely and the room reverts to its true not-started
    /// state, which is correct (nothing durable would ever have started it). See 0020's 2026-08-16
    /// amendment for why this cannot be read off the journal and must be an explicit caller input.
    /// </summary>
    WaitingToStart,

    /// <summary>
    /// #1299: a foreign process holds this room's flow lock, unaccounted for in the journal. See
    /// <see href="../../docs/decisions/0020-one-state-machine.md">0020's 2026-08-16 amendment</see>
    /// for the full rationale, including why this can never mean another room.
    /// </summary>
    WaitingOnLock,
}
