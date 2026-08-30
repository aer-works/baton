using Baton.Vendors;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
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
/// </summary>
public static class CancelCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
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
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// record-once-ok: #443 src/Baton.Cli/RunCommand.cs
    /// Another Flow instance already holds this room directory's lock; see that exception's message
    /// for which holders are possible and how to reach an in-flight execution instead. (#857: this
    /// used to paraphrase the message as "most likely a live <c>baton run</c> pump" — a single cause
    /// the message itself no longer asserts, and the paraphrase is what would have gone stale.)
    /// </exception>
    /// <exception cref="Baton.Store.FlowJournalHeldException">
    /// #816, shared with every other command building a <c>FlowEventLogWriter</c> — see that
    /// type's own docs.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        CancelOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, LogFileName);
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
        var (provisionedConfig, provisionedWorktrees, _) =
            WorktreeWorkspaces.ProvisionLazily(bindingConfig, options.RoomDirectoryPath);
        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        // Lazy (#662): cancel targets a room 'baton run' already started — it does not need to know how
        // to dispatch a worker it will never dispatch, so a bindings file naming an unresolvable one
        // must not block cancelling a different, already-dispatched execution.
        var workerBindings = WorkerBindingResolver.ResolveLazily(
            provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath));

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);
        var targetExecutionId = new ExecutionId(options.ExecutionId);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        var state = await MutationInterface.RequestCancellationAsync(
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

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(state.Status, provisionedWorktrees);

        return new CommandResult(state, snapshot, RoomDirectoryPath: options.RoomDirectoryPath, WorktreeTeardowns: worktreeTeardowns);
    }
}
