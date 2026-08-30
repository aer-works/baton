using Baton.Flow.Concurrency;
using Baton.Flow.Domain;
using Baton.Flow.Projection;
using Baton.Flow.Store;

namespace Baton.Flow.Mutation;

/// <summary>
/// The operator decision surface for held work (#672 item 1), and the seam where a
/// <see cref="MemoryProposalEscalation.MemoryProposalShape"/> item's approval becomes an actual
/// <c>memory/</c> write (#672 item 2, decision 0044 point 3: nothing else applies a proposal).
/// Every other <see cref="HeldWorkState.Shape"/> resolves through the same two outcomes with no
/// side effect beyond the room-journal entry <see cref="RoomMutationInterface.ResolveHeldWorkAsync"/>
/// already records -- this class does not invent per-shape behaviour for shapes it does not know.
/// <para>
/// <b>Ordering: apply, then resolve -- never the reverse.</b> The two writes (a file under
/// <c>memory/</c>, and a <see cref="RoomEvent.HeldWorkResolved"/> journal append) cannot be one
/// atomic transaction. Resolve-then-apply would let a crash between the two leave a
/// <see cref="HeldWorkStatus.Resolved"/> item whose proposal was never actually applied -- invisible,
/// because "resolved" reads as "done". Apply-then-resolve instead leaves a crash window where the
/// item is still <see cref="HeldWorkStatus.Dispatched"/>/<see cref="HeldWorkStatus.Escalated"/> (so
/// the operator's own tooling still surfaces it as pending) even though the file write already
/// landed -- proven directly by
/// <c>MemoryProposalResolutionTests.A_failure_between_apply_and_resolve_leaves_the_file_applied_but_the_item_still_pending</c>.
/// A retry in that window re-applies <see cref="MemoryProposalApplier.ApplyAsync"/> against a
/// <c>memory/</c> tree that already reflects the first attempt: <c>edit</c> is idempotent (its
/// target already exists, so it overwrites with the identical content again, harmlessly); <c>add</c>
/// and <c>delete</c> are not -- <c>add</c>'s target now already exists (post-apply guard, below) and
/// <c>delete</c>'s target is now already gone, so both fail loudly on the retry rather than silently
/// repeating or silently no-op'ing. Either way the retry's outcome is visible to the operator, never
/// a silent second write. A wedged-looking <c>add</c>/<c>delete</c> retry is not actually stuck:
/// <b>reject is the recovery path</b> -- it skips apply entirely and resolves the item outright, and
/// <c>memory/</c> already reflects the (successful) first attempt regardless.
/// </para>
/// </summary>
public static class MemoryProposalResolution
{
    public const string ApprovedEventType = "operator-approved";
    public const string RejectedEventType = "operator-rejected";

    /// <summary>
    /// #857: how long a resolve waits out a contended room lock before refusing. Sized against the
    /// holders it usually loses to — the plain mutation verbs, each holding for one
    /// read-project-append, measured in milliseconds.
    /// <para>
    /// Two seconds is generous for that and still short enough to surface a stuck holder rather
    /// than hide it. What it is <b>not</b> sized against are the two holders whose work under this
    /// lock grows with journal length: journal compaction, and the workflow-switch verb (which
    /// reads and projects the room's entire <c>flow.jsonl</c> before its own append). A long
    /// enough journal would exhaust this budget — unmeasured, because no such contention has been
    /// observed, and called out here rather than left as an assumption hiding inside a number.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan LockContentionBudget = TimeSpan.FromSeconds(2);

    public static async Task<RoomState> ResolveAsync(
        string roomDirectoryPath,
        HeldWorkRef @ref,
        bool approve,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        // #672 review (blocking finding): the already-resolved check, the memory/ apply, and the
        // resolve append must all happen under the SAME room lock. Checking status, releasing the
        // lock, then applying and resolving separately left a window where a second resolve call
        // (e.g. reject-then-approve, or a retried approve) could apply a memory-proposal write
        // even though the item was already resolved -- the apply ran before
        // RoomMutationInterface's own already-resolved guard ever got a chance to refuse it.
        //
        // #857: acquired WITH A WAIT rather than fail-fast, because this is the operator-facing
        // path. The other mutation verbs take this same room lock — typically for one
        // read-project-append — so a fail-fast acquire here turns any overlap into a refused
        // approve/reject the operator can only answer by clicking again. The typical hold is
        // milliseconds (see LockContentionBudget's remarks for the two holders that are not), so a
        // short wait converts a coin-flip into a certainty. Still bounded: a genuinely stuck
        // holder must surface, not be waited on forever.
        using var guard = ConcurrencyGuard.AcquireRoomEventsWithin(roomDirectoryPath, LockContentionBudget);

        var existingEvents = await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false);
        var state = RoomProjector.Project(existingEvents);
        if (!state.HeldWork.TryGetValue(@ref, out var item))
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' was not found in this room.");
        }

        if (item.Status == HeldWorkStatus.Resolved)
        {
            throw new InvalidRoomMutationException($"HeldWorkRef '{@ref}' is already resolved.");
        }

        if (approve && item.Shape == MemoryProposalEscalation.MemoryProposalShape)
        {
            // Deliberately BEFORE the resolve below -- see this class's own remarks on ordering.
            // Safe to apply here specifically BECAUSE the status check above and this apply share
            // the same lock hold as the append that follows -- no other resolver can interleave.
            var proposer = ExtractProposerFromRef(@ref.Value);
            await MemoryProposalApplier.ApplyAsync(
                roomDirectoryPath, @ref.Value, proposer, item.DeciderIdentity, cancellationToken)
                .ConfigureAwait(false);
        }

        // No workflow and no execution: the subject IS the held-work ref itself (the capture file). Not
        // wrapped in an ExecutionId, which is Core's join key and joins to nothing here (#855).
        var citation = new HeldWorkCitation(
            @ref.Value, approve ? ApprovedEventType : RejectedEventType);

        var resolvedState = await RoomMutationInterface.ResolveHeldWorkLockedAsync(
            @ref, citation, existingEvents, state, writer, cancellationToken).ConfigureAwait(false);

        if (item.Shape == MemoryProposalEscalation.MemoryProposalShape)
        {
            // #1039: consume the capture file now that the proposal is resolved on the journal.
            // MemoryProposalEscalation dedups on HeldWork.ContainsKey(@ref), which the projector only
            // holds while the resolve event is in the journal. Once #1025's retention sweep compacts
            // that event away, an un-consumed proposal-*.json is re-found by the per-wake escalation
            // and re-dispatched -- re-surfacing an already-decided proposal (and re-recording a memory
            // version on re-approval). Deleting the file makes the path-derived ref genuinely one-shot,
            // which is the execution-scoped-ref premise the compaction design rests on, rather than
            // relying on the journal never being compacted.
            //
            // AFTER the resolve append, never before: a crash in the gap leaves the file on disk with
            // the item already Resolved in the journal, so the ContainsKey guard still skips it until
            // compaction -- no worse than the pre-#1039 behaviour.
            TryDeleteResolvedCaptureFile(@ref.Value);
        }

        return resolvedState;
    }

    private static void TryDeleteResolvedCaptureFile(string captureFilePath)
    {
        try
        {
            File.Delete(captureFilePath);
        }
        catch (Exception ex)
        {
            // Deliberate, narrow exception to this repo's "log and rethrow, or map to a structured
            // result" error-handling rule: the resolve already landed on the journal -- the operator's
            // action succeeded and its result is already being returned -- so rethrowing would misreport
            // an applied mutation as failed. A failed delete is degraded-but-safe (the pre-compaction
            // ContainsKey guard still covers re-dispatch), so this one path logs and continues.
            Console.Error.WriteLine(
                $"MemoryProposalResolution: failed to delete resolved capture file '{captureFilePath}': {ex.Message}");
        }
    }

    // Path-derived attribution is an honest stopgap: proposals do not yet carry a producerId
    // (#778's design closure §C records that they must, for producer≠decider to be evaluable) —
    // until then the execution directory in the proposal's ref is the best available identity.
    private static string ExtractProposerFromRef(string refValue)
    {
        var dir = Path.GetDirectoryName(refValue);
        var parent = dir is not null ? Path.GetDirectoryName(dir) : null;
        if (parent is not null && Path.GetFileName(parent).StartsWith("execution_", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(parent);
        }

        return "unknown";
    }
}

