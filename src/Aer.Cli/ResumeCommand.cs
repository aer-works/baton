using Aer.Adapters;
using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Flow.Workspaces;

namespace Aer.Cli;

/// <summary>
/// The CLI surface for <see cref="ResumeOptions"/> (see that type's own doc for what <c>aer resume</c>
/// is) — exposes <see cref="MutationInterface.RecordResumeAsync"/>. Like <see cref="DecideCommand"/>
/// and <see cref="SupplyCommand"/>, this never binds a fresh snapshot — a mutation command only ever
/// acts against a room <c>aer run</c> has already started (§11.2).
/// <para>
/// Runs the SAME two-call sequence <see cref="SupplyCommand"/> established for a single-execution
/// mutation: <see cref="MutationInterface.RecordResumeAsync"/> mints and dispatches the one linked
/// execution, then <see cref="MutationInterface.StartWorkflowAsync"/> settles any downstream
/// consequence (a sibling step this one's outcome unblocks, a pause obligation) to the next fixed
/// point — so this command blocks and reports exactly like <c>aer run</c>.
/// </para>
/// </summary>
public static class ResumeCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <exception cref="CliArgumentException">
    /// <see cref="ResumeOptions.MessageFilePath"/> does not exist, or the bindings file has no entry
    /// for <see cref="ResumeOptions.Worker"/>.
    /// </exception>
    /// <exception cref="SnapshotLoadException">
    /// The room directory has no persisted snapshot yet (never started via <c>aer run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>, for the
    /// worker this call actually resumes.
    /// </exception>
    /// <exception cref="WorkerCannotResumeException">
    /// The bindings entry for <see cref="ResumeOptions.Worker"/> has no <c>SessionId</c> recorded —
    /// #1359's design ruling: refuse loudly rather than silently starting cold.
    /// </exception>
    /// <exception cref="InvalidResumeException">
    /// See that type's own doc for the closed set of state-based refusals this can mean.
    /// </exception>
    /// <exception cref="Aer.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this room directory's lock.
    /// </exception>
    /// <exception cref="Aer.Flow.Store.FlowJournalHeldException">
    /// Another process — most likely a live <c>aer run</c> engine — already holds this room's
    /// <c>flow.jsonl</c> open (#816).
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        ResumeOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        string message;
        if (options.Message is { } literalMessage)
        {
            message = literalMessage;
        }
        else
        {
            var messageFilePath = options.MessageFilePath!;
            if (!File.Exists(messageFilePath))
            {
                throw new CliArgumentException($"Message file '{messageFilePath}' does not exist.");
            }

            message = await File.ReadAllTextAsync(messageFilePath, cancellationToken).ConfigureAwait(false);
        }

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'aer resume' " +
                "targets a room 'aer run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);
        var (provisionedConfig, provisionedWorktrees) =
            WorktreeWorkspaces.Provision(bindingConfig, options.RoomDirectoryPath);

        if (!provisionedConfig.TryGetValue(options.Worker, out var entry))
        {
            throw new CliArgumentException(
                $"No bindings entry for worker '{options.Worker}' in '{options.BindingsFilePath}'.",
                "pass --worker naming a key present in the bindings file.");
        }

        // See WorkerCannotResumeException's own doc for why this is the refusal today rather than a
        // captured-automatically session id.
        if (entry.SessionId is null)
        {
            throw new WorkerCannotResumeException(
                $"Worker '{options.Worker}' has no SessionId recorded in '{options.BindingsFilePath}' — " +
                "aer resume cannot continue a session it has no id for.",
                $"add \"SessionId\": \"<the vendor's session id>\" to worker '{options.Worker}''s entry in " +
                $"'{options.BindingsFilePath}' (captured from a prior invocation), then retry.");
        }

        var overrideEntry = entry with { PromptTemplate = message, ResumeSession = true };
        var profiles = await AerProfileStore.LoadAsync(AerProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var bindingsFileDirectory = Path.GetDirectoryName(options.BindingsFilePath);

        var resolvedOverride = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { [options.Worker] = overrideEntry },
            adapters, profiles, bindingsFileDirectory);

        // Lazy for every OTHER worker (#662, the same reasoning SupplyCommand/CancelCommand already
        // rest on): a resume targets one already-dispatched worker — a bindings file naming an
        // unrelated, unresolvable adapter for a step this call never touches must not block it.
        var lazyBaseBindings = WorkerBindingResolver.ResolveLazily(
            provisionedConfig, adapters, profiles, bindingsFileDirectory);
        var workerBindings = new WorkerBindingOverride(lazyBaseBindings, options.Worker, resolvedOverride[options.Worker]);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        await MutationInterface.RecordResumeAsync(
                workflowId, options.RoomDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                options.Worker, reader, writer, dispatcher, cancellationToken)
            .ConfigureAwait(false);

        var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, options.RoomDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                reader, writer, dispatcher, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(settledState.Status, provisionedWorktrees);

        return new CommandResult(settledState, snapshot, RoomDirectoryPath: options.RoomDirectoryPath, WorktreeTeardowns: worktreeTeardowns);
    }
}
