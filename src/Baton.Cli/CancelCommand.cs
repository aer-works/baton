using Baton.Vendors;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;
using Baton.Workspaces;

namespace Baton.Cli;

/// <summary>
/// <c>baton cancel</c> (M12 Phase 2): exposes <see cref="MutationInterface.RequestCancellationAsync"/>
/// on the CLI. Unlike <see cref="RunCommand"/>, this never binds a fresh snapshot — mutation commands
/// only ever act against a room <c>baton run</c> has already started — and, like
/// every mutation entry point, is itself a pump: recording the cancellation intent resumes driving
/// the rest of the workflow to its next fixed point.
/// #1495 adds two things this room-idle description does not cover: room-level targeting (no
/// <c>--execution</c> resolves "the target lane" via <see cref="RunningExecutionResolver"/>, fail
/// closed on zero or more than one candidate) and a live-pump fall-through — catching
/// <see cref="WorkflowLockedException"/> from the guarded call above and writing
/// <see cref="CancelRequestFile"/> instead (writing <c>latest</c> to re-resolve the target at poll
/// time per spec/baton.md §2, unlike the idle room's command-time resolution), so a room whose <c>baton run</c>
/// is genuinely still live is reachable too, not just the idle-room path the rest of this type's doc
/// still describes accurately on its own.
/// </para>
/// <para>
/// #1607 widened the <c>--execution</c>-omitted path beyond <see cref="StepStatus.Running"/>:
/// <see cref="RunningExecutionResolver"/> now also resolves a quota-parked step (<c>Failed</c> with a
/// scheduled <c>RetryNotBefore</c>) as a candidate, retiring #802 §"doesn't work alone" objection
/// now that PR #1605 gave a parked mark a real delivery point
/// (<c>InFlightExecutionRegistry.MarkParkedCancelIntent</c>). A parked candidate resolved here still
/// only reaches the pump through the SAME two paths this method already had: the direct call below
/// (only ever reachable against an already-*overdue* park when a live pump is confirmed, since a
/// genuinely still-future one is refused by the dead-holder check above before the resolver ever
/// runs — that check was widened in this same change from Dead-only to "anything but confirmed
/// Alive", specifically because the resolver widening above would otherwise let a room with no
/// holder record at all reach a still-future park directly, which nothing would ever drain), or the
/// <see cref="WorkflowLockedException"/> fall-through, which re-resolves <c>latest</c> at poll time via
/// the identical widened resolver.
/// </para>
/// <para>
/// #1607 review finding (F1): the dead-holder gate below sits before target resolution, so its
/// Unknown-liveness widening is deliberately NOT scoped to room-level targeting — it refuses an
/// explicit <c>--execution &lt;id&gt;</c> just as readily. See spec/baton.md §2 for why (narrowing it
/// would reopen #1586's hang from the explicit path) and what that costs a genuinely-alive pump.
/// </para>
/// </summary>
public static class CancelCommand
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <exception cref="SnapshotLoadException">
    /// record-once-ok: #443 src/Baton.Cli/DecideCommand.cs
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>, for a
    /// worker the pump this call drives actually looks up (<see cref="WorkerBindingResolver.ResolveLazily"/>, #662).
    /// </exception>
    /// <exception cref="Baton.Mutation.UnknownExecutionIdException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> was never admitted for execution.
    /// </exception>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> is <c>null</c> (room-level targeting, #1495) and
    /// the room's own projected state has zero or more than one candidate — a currently
    /// <see cref="StepStatus.Running"/> step or a quota-parked one (#1607) — fail closed rather than
    /// guess; the message names every candidate found.
    /// </exception>
    /// <exception cref="Baton.Store.FlowJournalHeldException">
    /// #816, shared with every other command building a <c>FlowEventLogWriter</c> — see that
    /// type's own docs.
    /// </exception>
    /// <remarks>
    /// #1495: <see cref="Baton.Concurrency.WorkflowLockedException"/> — previously the terminal failure
    /// this command threw whenever a live <c>baton run</c> pump already held this room directory's lock
    /// — is now caught internally and turned into a <see cref="CancelRequestFile"/> write instead, so it
    /// no longer escapes this method at all.
    /// </remarks>
    public static async Task<CommandResult> ExecuteAsync(
        CancelOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton cancel' " +
                "targets a room 'baton run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);

        // #1586: measured against a copy of a real quota-parked room whose engine died — 'baton
        // cancel' acquired flow.lock (the OS releases a crashed holder's lock immediately, so the
        // acquire itself never fails), overwrote flow.lock.holder (destroying the record of which
        // engine died), journalled a CancellationRequested, and then hung: PumpToFixedPointAsync
        // re-enters the identical Task.Delay for the same doomed retry (MutationInterface.cs's
        // pendingDeferrals branch), because the whole premise of this room's shape is that nothing
        // will ever service it. That hang is specifically a FUTURE RetryNotBefore with nothing alive
        // to act on it -- a dead holder whose room has already reached a fixed point (nothing
        // pending, or a deadline already past) would NOT hang; refusing it anyway would send a
        // recoverable-by-cancel room to 'baton run --room-dir' instead, which redispatches the step
        // rather than cancelling it. So both conditions gate the refusal, not the holder alone.
        // Reading the sidecar BEFORE any acquire -- the same EngineLivenessProbe StatusCommand's
        // parked-status line already consults, never a second liveness mechanism -- lets this refuse
        // the hang instead of producing it, without ever touching the holder record.
        //
        // #1604 F1/F3: this used to gate on ConcurrencyGuard.IsHeld(...) first, and fed the probe
        // AcquiredAtUtc (when this lock was won) as if it were the holder's PROCESS start time --
        // EngineLivenessProbe.Probe's second parameter is a ±1s pid-recycling discriminator against
        // the OS process's own StartTime, and a lock is essentially never won within 1s of its own
        // holder's process starting, so that fed value made the Alive arm unreachable: every real pump read
        // Dead, and IsHeld was the only thing standing between that misread and a false refusal.
        // IsHeld itself opened flow.lock with FileShare.None to test it -- a momentary exclusive
        // hold that could itself steal the lock out from under a concurrent, legitimate
        // 'baton run --room-dir' (the exact recovery this refusal points at). Now that the sidecar
        // carries the holder's actual process start time (ConcurrencyGuard.CreateWithSidecar) and
        // Probe is fed that instead, the probe is directly trustworthy on its own -- a live holder's
        // own pid, start time, and HasExited are all consulted -- so IsHeld's lock-stealing
        // pre-check bought nothing further and is deleted. A genuinely live holder is still caught
        // the same way it always was for every other contended acquire: this command's own Acquire
        // call below loses the race, and WorkflowLockedException falls through unchanged to the
        // handling below.
        var (recordedHolderDescription, holderPid, _, holderProcessStartTimeUtc) = ConcurrencyGuard.ReadHolderInfo(options.RoomDirectoryPath);
        var holderProcessStartTime = holderProcessStartTimeUtc is { } startTimeUtc
            ? new DateTimeOffset(DateTime.SpecifyKind(startTimeUtc, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        var liveness = EngineLivenessProbe.Probe(holderPid, holderProcessStartTime);
        // #1607 (second-reader finding): widened from Dead-only to "anything but confirmed Alive".
        // Before #1607, a room whose only step was quota-parked could never reach this far via
        // room-level (--execution-omitted) targeting at all -- RunningExecutionResolver saw no
        // Running step and refused first, so an Unknown-liveness room got an accidental second line
        // of defense against exactly the hang this gate exists to prevent. Widening the resolver to
        // resolve a parked candidate removed that accidental defense: a room with no holder sidecar
        // at all (EngineLivenessProbe.Probe's Unknown, e.g. ConcurrencyGuard.Dispose() deleting it on
        // a clean unwind, a best-effort sidecar write that never landed, or a pre-#1604 sidecar with
        // no ProcessStartTimeUtc) now resolves a still-future park and would otherwise fall straight
        // into MutationInterface's own pump, which nothing ever drains. Fail closed rather than risk
        // that: Unknown is treated the same as Dead here.
        //
        // This gate runs BEFORE targetExecutionId is resolved below, so it applies identically to
        // room-level (--execution omitted) AND explicit (--execution <id>) targeting -- deliberately,
        // not an oversight (#1607 review finding F1). See this type's own class-level doc and
        // spec/baton.md §2 for why, and for what that costs the explicit path.
        if (liveness.Status != EngineLivenessStatus.Alive)
        {
            var preCheckEvents = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var preCheckState = StateProjector.Project(preCheckEvents, snapshot);
            var now = DateTimeOffset.UtcNow;
            var hasFutureDeferral = preCheckState.Steps.Any(s => s.RetryNotBefore is { } retryNotBefore && retryNotBefore > now);
            if (hasFutureDeferral)
            {
                var holderClause = liveness.Status == EngineLivenessStatus.Dead
                    ? $"the last recorded holder (pid {holderPid}) is no longer running"
                    : "its holder record cannot confirm one exists"
                        + (recordedHolderDescription is not null ? $" (last recorded: '{recordedHolderDescription}')" : " (no holder record at all)");
                // F2 (#1607 review): a Dead verdict is confirmed, so re-running against --room-dir is
                // unconditionally the right pointer. An Unknown verdict is not a confirmation of death
                // (see the gate comment above for the causes) -- pointing that case at the SAME
                // instruction tells an operator whose pump is genuinely still running to run a command
                // that only contends its lock. `baton status` is not offered as an alternative here: it
                // reads the identical EngineLivenessProbe (StatusCommand.FormatStepStatus), so it would
                // report the same Unknown rather than resolving it.
                var tryInvocation = liveness.Status == EngineLivenessStatus.Dead
                    ? $"{RecoveryGuidance.RunRoomDirInstruction} (see spec/baton.md §3)."
                    : "if you can independently confirm (Task Manager/`ps`) that no pump is actually " +
                        $"running against this room, {RecoveryGuidance.RunRoomDirInstruction} (see " +
                        "spec/baton.md §3); if a pump IS confirmed still running, retry this command in a " +
                        "moment — a lost or delayed sidecar write is the one Unknown cause that clears on " +
                        "its own, and there is currently no verb that reaches a still-alive pump whose " +
                        "holder record can't be confirmed (see spec/baton.md §2).";
                throw new CliArgumentException(
                    $"Room '{options.RoomDirectoryPath}' has no live pump — {holderClause}, and " +
                    "this room still has a step waiting on a future retry. Acquiring the lock here would " +
                    "journal a cancellation nothing will ever act on, then hang waiting for that retry with " +
                    "nothing confirmed alive to deliver it — and, if a dead engine's holder record is still " +
                    "present, would overwrite it, destroying the record of which engine died. Left " +
                    "untouched. No verb terminates a parked room with no confirmed live pump today — that " +
                    "is #1586's tracked 'baton settle' design — so the pointer below resumes it rather than " +
                    "stopping it, deliberately.",
                    tryInvocation);
            }
        }

        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindingConfig;
        try
        {
            bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkerBindingConfigException ex)
            when (string.Equals(options.BindingsFilePath, BatonPaths.RoomBindingsFile(options.RoomDirectoryPath), StringComparison.Ordinal))
        {
            // F3 (#1607 review): CancelOptionsParser defaults an omitted --bindings to this exact
            // path, so a missing file here is most likely "this room was never dispatched, so it has
            // no bindings.json of its own" rather than a mistyped explicit argument. The generic
            // WorkerBindingConfigException already names the path; this augments it with which
            // command produced that default and that --bindings is still available to point elsewhere
            // — without touching WorkerBindingConfigParser's message for run/decide/supply, which
            // never default this path and so never hit this arm (their --bindings is required, so a
            // missing file there really is an explicit mistake).
            throw new WorkerBindingConfigException(
                $"{ex.Message} This is 'baton cancel's default --bindings path, used because --bindings " +
                "was not given — if this room's bindings file lives elsewhere (e.g. it was started via a " +
                "bare 'baton run --bindings <path>' rather than 'dispatch'/'redispatch'), pass --bindings " +
                "explicitly naming it.",
                ex);
        }
        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        // #1495: room-level targeting when --execution is omitted — resolve "the target lane" from
        // the room's own projected state rather than a caller-named id. A plain read, safe regardless
        // of whether a pump is live (FlowEventLogReader always opens FileShare.ReadWrite) or idle.
        var targetExecutionId = options.ExecutionId is { } explicitExecutionId
            ? new ExecutionId(explicitExecutionId)
            : await ResolveRunningExecutionAsync(reader, snapshot, options.RoomDirectoryPath, cancellationToken)
                .ConfigureAwait(false);

        FlowState state;
        // Defaulted here, not inside the catch below: WorktreeWorkspaces.ProvisionLazily can succeed
        // (assigning a real list) and STILL have the later mutation call below lose the guard race, in
        // which case the catch must not discard what was actually provisioned. Only a throw from
        // ProvisionLazily itself leaves this at its true default of "nothing provisioned yet".
        IReadOnlyList<ProvisionedWorktree> provisionedWorktrees = [];
        try
        {
            // #1495 finding: WorktreeWorkspaces.ProvisionLazily takes the SAME flow.lock guard
            // (WorktreeWorkspaces.Walk, "worktree provisioning" holder) even when no binding declares a
            // worktree — so a live pump contends this call too, not only the mutation call below. Both
            // must share one WorkflowLockedException catch, or the fall-through would only cover half
            // of what actually contends the lock.
            var (provisionedConfig, walkedProvisionedWorktrees, _) =
                WorktreeWorkspaces.ProvisionLazily(bindingConfig, options.RoomDirectoryPath);
            provisionedWorktrees = walkedProvisionedWorktrees;

            // Lazy (#662): cancel targets a room 'baton run' already started — it does not need to know
            // how to dispatch a worker it will never dispatch, so a bindings file naming an unresolvable
            // one must not block cancelling a different, already-dispatched execution.
            var workerBindings = WorkerBindingResolver.ResolveLazily(
                provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath));

            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);

            state = await MutationInterface.RequestCancellationAsync(
                    workflowId,
                    options.RoomDirectoryPath,
                    snapshot,
                    workerBindings,
                    artifactsRootPath,
                    reader,
                    writer,
                    dispatcher,
                    targetExecutionId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkflowLockedException lockedException)
        {
            // #1495: the live-pump fall-through — this room's flow.lock is held by another Flow
            // instance, so nothing above could ever win the guard. Deliver the same intent out-of-band
            // instead: a room-scoped request file the pump's own CancelRequestPoller polls without ever
            // contending flow.lock, consumed the next time that poller ticks.
            //
            // The holder is NOT necessarily a live 'baton run' pump — WorkflowLockedException's own
            // message names a second cause (a background component's brief hold, e.g. a memory-proposal
            // sweep or a concurrent 'baton cancel' contending the SAME worktree-provisioning guard above).
            // Against that case this still writes the request file (matching this method's own doc:
            // "catch that specific case and fall through"), but nothing may ever consume it — named as a
            // known limitation in report-1495.md rather than silently asserted away here. What CAN be
            // done cheaply: report the ACTUAL holder the exception already carries, rather than a blanket
            // claim of "live pump" the exception does not itself make.
            var explicitTarget = options.ExecutionId is not null;
            var fileTarget = explicitTarget ? targetExecutionId.Value : CancelRequestFile.LatestTarget;
            await CancelRequestFile.WriteAsync(options.RoomDirectoryPath, fileTarget, cancellationToken)
                .ConfigureAwait(false);

            var holderDescription = lockedException.HolderDescription ?? "an unnamed holder";
            Console.Out.WriteLine(
                $"Requested — '{options.RoomDirectoryPath}'s {BatonPaths.FlowLockFileName} is currently held by '{holderDescription}'. " +
                "If that is a live pump, it will act on this cancellation the next time its cancel.request poll " +
                "ticks; if the hold is brief and unrelated, this request may sit unconsumed until one starts.");

            var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            state = StateProjector.Project(events, snapshot);
        }

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(state.Status, provisionedWorktrees);

        return new CommandResult(state, snapshot, RoomDirectoryPath: options.RoomDirectoryPath, WorktreeTeardowns: worktreeTeardowns);
    }

    /// <summary>
    /// Room-level target resolution via <see cref="RunningExecutionResolver"/>; throws
    /// <see cref="CliArgumentException"/> when the room state does not contain exactly one candidate —
    /// a currently-<see cref="StepStatus.Running"/> step or a quota-parked one (#1607).
    /// </summary>
    private static async Task<ExecutionId> ResolveRunningExecutionAsync(
        FlowEventLogReader reader, WorkflowDefinitionSnapshot snapshot, string roomDirectoryPath, CancellationToken cancellationToken)
    {
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);
        var resolved = RunningExecutionResolver.Resolve(state);

        if (resolved.Single is { } single)
        {
            return single;
        }

        if (resolved.RunningExecutionIds.Count == 0)
        {
            throw new CliArgumentException(
                $"No --execution given, and room '{roomDirectoryPath}' has no currently-Running or "
                + "quota-parked step to target — 'baton cancel' refuses to guess.",
                $"pass --execution explicitly, naming the execution to cancel — dig it out of "
                + $"`baton status {roomDirectoryPath}`.");
        }

        // F5 (#1607 review): name which candidate is which, not just their ids -- the ambiguity is
        // newly reachable (a Running step plus any sibling in ordinary retry backoff, spec/baton.md
        // §2), so this message is now an operator's first encounter with the widened behaviour rather
        // than a rare edge case, and "Running or quota-parked" alone forces them to go find out which
        // is which some other way.
        var labeledCandidates = resolved.RunningExecutionIds.Select(id =>
        {
            var step = state.Steps.First(s => s.LatestExecutionId == id);
            var label = step.Status == StepStatus.Running ? "Running" : "quota-parked";
            return $"{id.Value} ({label})";
        });

        throw new CliArgumentException(
            $"No --execution given, and room '{roomDirectoryPath}' has {resolved.RunningExecutionIds.Count} "
            + $"currently-Running or quota-parked steps ({string.Join(", ", labeledCandidates)}) "
            + "— 'baton cancel' refuses to guess which one.",
            $"pass --execution explicitly, naming the one to cancel — see `baton status {roomDirectoryPath} --json` "
            + "for each step's current status.");
    }
}
