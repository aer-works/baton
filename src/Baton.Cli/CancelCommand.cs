using Baton.Vendors;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
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
/// <c>--execution</c> resolves "the running lane" via <see cref="RunningExecutionResolver"/>, fail
/// closed on zero or more than one candidate) and a live-pump fall-through — catching
/// <see cref="WorkflowLockedException"/> from the guarded call above and writing
/// <see cref="CancelRequestFile"/> instead (writing <c>latest</c> for room-level targeting, which
/// re-resolves at poll time to arrest whatever is running then, whereas the direct path cancels
/// the execution resolved at command time), so a room whose <c>baton run</c> is genuinely still live
/// is reachable too, not just the idle-room path the rest of this type's doc still describes
/// accurately on its own.
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
    /// the room's own projected state has zero or more than one <see cref="StepStatus.Running"/> step —
    /// fail closed rather than guess; the message names every Running candidate found.
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

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);
        var reader = new FlowEventLogReader(logPath);

        // #1495: room-level targeting when --execution is omitted — resolve "the running lane" from
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
    /// <see cref="CliArgumentException"/> when the room state does not contain exactly one running step.
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
                $"No --execution given, and room '{roomDirectoryPath}' has no currently-Running step to "
                + "target — 'baton cancel' refuses to guess.",
                $"pass --execution explicitly, or check `baton status {roomDirectoryPath}`.");
        }

        throw new CliArgumentException(
            $"No --execution given, and room '{roomDirectoryPath}' has {resolved.RunningExecutionIds.Count} "
            + $"currently-Running steps ({string.Join(", ", resolved.RunningExecutionIds.Select(id => id.Value))}) "
            + "— 'baton cancel' refuses to guess which one.",
            "pass --execution explicitly, naming the one to cancel.");
    }
}
