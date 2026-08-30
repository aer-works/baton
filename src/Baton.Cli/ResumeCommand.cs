using Baton.Vendors;
using Baton.Flow.Artifacts;
using Baton.Flow.Dispatch;
using Baton.Flow.Domain;
using Baton.Flow.Mutation;
using Baton.Flow.Store;
using Baton.Flow.Templates;
using Baton.Flow.Workspaces;

namespace Baton.Cli;

/// <summary>
/// The CLI surface for <see cref="ResumeOptions"/> (see that type's own doc for what <c>baton resume</c>
/// is) — exposes <see cref="MutationInterface.RecordResumeAsync"/>. Like <see cref="DecideCommand"/>
/// and <see cref="SupplyCommand"/>, this never binds a fresh snapshot — a mutation command only ever
/// acts against a room <c>baton run</c> has already started.
/// <para>
/// Runs the SAME two-call sequence <see cref="SupplyCommand"/> established for a single-execution
/// mutation: <see cref="MutationInterface.RecordResumeAsync"/> mints and dispatches the one linked
/// execution, then <see cref="MutationInterface.StartWorkflowAsync"/> settles any downstream
/// consequence (a sibling step this one's outcome unblocks, a pause obligation) to the next fixed
/// point — so this command blocks and reports exactly like <c>baton run</c>.
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
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
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
    /// See that type's own doc for the closed set of state-based refusals this can mean (missing
    /// on-disk workspace included).
    /// </exception>
    /// <exception cref="Baton.Flow.Workspaces.InvalidWorkspaceSpecException">
    /// A bad worktree spec, per <see cref="Baton.Vendors.WorktreeWorkspaces.Provision"/>'s documented
    /// checks.
    /// </exception>
    /// <exception cref="Baton.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this room directory's lock.
    /// </exception>
    /// <exception cref="Baton.Flow.Store.FlowJournalHeldException">See that type's own docs for why (#816).</exception>
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
                throw new CliArgumentException(
                    $"Message file '{messageFilePath}' does not exist.",
                    "create the file, or pass --message with the text inline instead.");
            }

            message = await File.ReadAllTextAsync(messageFilePath, cancellationToken).ConfigureAwait(false);
        }

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton resume' " +
                "targets a room 'baton run' has already started, and never binds one fresh.")
            {
                TryInvocation = $"run `baton run` (or `baton dispatch`) against '{options.RoomDirectoryPath}' first, then resume it.",
            };
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);

        if (!bindingConfig.TryGetValue(options.Worker, out var entry))
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
                "baton resume cannot continue a session it has no id for.",
                $"add \"SessionId\": \"<the vendor's session id>\" to worker '{options.Worker}''s entry in " +
                $"'{options.BindingsFilePath}' (captured from a prior invocation), then retry.");
        }

        // F1 (#1388 review): baton resume never provisions a fresh worktree for the worker it is
        // continuing — it reuses the exact one that execution already ran in, or refuses if that
        // workspace is gone. This is the only worktree touched before the resume itself is validated.
        var resumeEntry = WorktreeWorkspaces.ReuseForResume(entry, options.Worker, options.RoomDirectoryPath);

        var overrideEntry = resumeEntry with { PromptTemplate = message, ResumeSession = true };
        var profiles = await BatonProfileStore.LoadAsync(BatonProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var bindingsFileDirectory = Path.GetDirectoryName(options.BindingsFilePath);

        var resolvedOverride = WorkerBindingResolver.Resolve(
            new Dictionary<string, WorkerBindingConfigEntry> { [options.Worker] = overrideEntry },
            adapters, profiles, bindingsFileDirectory);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        await using var writer = new FlowEventLogWriter(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        // F5 (#1388 review): RecordResumeAsync only ever looks up the worker actually being resumed —
        // it never touches any other entry in the bindings file — so this is the ONLY binding it
        // needs. Provisioning (or even resolving) the rest of the file waits until after this call
        // succeeds, so a refusal here (never ran, still running, ambiguous, no recorded execution,
        // non-process) leaves every other worker's workspace untouched, the same as any other refusal
        // in this verb.
        var resumeOnlyBindings = new Dictionary<string, WorkerBinding> { [options.Worker] = resolvedOverride[options.Worker] };

        await MutationInterface.RecordResumeAsync(
                workflowId, options.RoomDirectoryPath, snapshot, resumeOnlyBindings, artifactsRootPath,
                options.Worker, reader, writer, dispatcher, cancellationToken,
                // F6: the bindings file's own SessionId for this worker, so RecordResumeAsync can
                // refuse if it disagrees with the session the execution being resumed already
                // recorded, rather than recording a continuity nothing backs.
                sessionId: entry.SessionId)
            .ConfigureAwait(false);

        // Only now, with the resume itself validated and dispatched, provision workspaces for
        // whatever OTHER steps the settling pump below might newly make ready — ordinary `baton
        // run`/`baton dispatch` provisioning, unrelated to F1's reuse-or-refuse rule above (which
        // applies only to the worker being resumed, already reflected in resolvedOverride).
        var (provisionedConfig, provisionedWorktrees) =
            WorktreeWorkspaces.Provision(bindingConfig, options.RoomDirectoryPath);

        // Lazy for every OTHER worker (#662, the same reasoning SupplyCommand/CancelCommand already
        // rest on): a resume targets one already-dispatched worker — a bindings file naming an
        // unrelated, unresolvable adapter for a step this call never touches must not block it.
        var lazyBaseBindings = WorkerBindingResolver.ResolveLazily(
            provisionedConfig, adapters, profiles, bindingsFileDirectory);
        var workerBindings = new WorkerBindingOverride(lazyBaseBindings, options.Worker, resolvedOverride[options.Worker]);

        var settledState = await MutationInterface.StartWorkflowAsync(
                workflowId, options.RoomDirectoryPath, snapshot, workerBindings, artifactsRootPath,
                reader, writer, dispatcher, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(settledState.Status, provisionedWorktrees);

        return new CommandResult(settledState, snapshot, RoomDirectoryPath: options.RoomDirectoryPath, WorktreeTeardowns: worktreeTeardowns);
    }
}
