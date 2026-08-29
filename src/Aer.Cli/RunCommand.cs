using Aer.Adapters;
using Aer.Flow.Artifacts;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Status;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Flow.Workspaces;

namespace Aer.Cli;

/// <summary>
/// <c>aer run</c>, the "pump" §21 designates as v1's execution driver (M11 Phase 3): the exact
/// project → resolve → dispatch → await loop <c>WorkflowEndToEndTests</c> has exercised since M7,
/// now reached through a real <see cref="IWorkerAdapter"/> and a real host process instead of a
/// test fixture constructing <see cref="WorkerBinding"/>s by hand.
/// </summary>
public static class RunCommand
{
    private const string SnapshotFileName = "snapshot.json";
    private const string LogFileName = "flow.jsonl";
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <summary>
    /// Parses the workflow template and worker-binding config (binding from the already-persisted
    /// snapshot instead, when <paramref name="options"/>'s room directory already has one — a
    /// resumed run, not a fresh one), resolves <paramref name="adapters"/> into
    /// <see cref="WorkerBinding"/>s, and runs the single mutation surface to a terminal state.
    /// A resumed run still reads the named template file when one exists, to refuse a directory
    /// bound to different work (#628); it never binds from it.
    /// </summary>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>RoomDirectoryPath</c> has no persisted snapshot yet (a fresh
    /// start) and no <c>WorkflowFilePath</c> was given to bind one from.
    /// </exception>
    /// <exception cref="WorkflowDefinitionValidationException">The workflow template is malformed or invalid.</exception>
    /// <exception cref="SnapshotLoadException">The room directory's persisted snapshot is malformed.</exception>
    /// <exception cref="WorkerBindingConfigException">The worker-binding config is malformed.</exception>
    /// <exception cref="UnknownWorkerAdapterException">
    /// The worker-binding config names an adapter not present in <paramref name="adapters"/>.
    /// </exception>
    /// <exception cref="ResumedTemplateMismatchException">
    /// <paramref name="options"/>'s room directory is already bound to a snapshot, and the workflow
    /// file named is a different template (#628).
    /// </exception>
    /// <exception cref="Aer.Flow.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this room directory's lock.
    /// </exception>
    /// <exception cref="Aer.Flow.Store.FlowJournalHeldException">See that type's own docs for why (#816).</exception>
    /// <param name="inFlightExecutions">
    /// M15 Phase 4's (issue #140) additive caller-retained delivery point — forwarded, unchanged, to
    /// <see cref="MutationInterface.StartWorkflowAsync"/>. <c>null</c> for every caller (the CLI
    /// included) that has no need to reach a live execution mid-pump; a caller that retains one can
    /// signal a targeted Cancel to a specific in-flight execution this same call dispatched, without
    /// a second mutation-surface call racing §15's guard (originally <c>Aer.RoomSession</c>'s
    /// <c>RoomClient</c>, itself since deleted, #1420).
    /// </param>
    /// <param name="onWorkerStdoutLine">
    /// M24 Phase 1's live in-turn streaming — forwarded verbatim to <see cref="WorkerBindingResolver.Resolve"/>.
    /// Null for <c>aer run</c> by default unless <c>--echo-worker</c> is set (#882). <c>Aer.Daemon</c>'s
    /// in-process session-turn path supplies one only for <c>StreamJson</c> turns (see
    /// <c>Program.ExecuteSessionTurnAsync</c>) — the invariant that keeps the flag from ever
    /// double-echoing there is narrower: no daemon/UI path constructs <see cref="RunOptions"/> with
    /// <c>EchoWorker</c> set, and an explicit callback always wins over the flag below.
    /// </param>
    public static async Task<CommandResult> ExecuteAsync(
        RunOptions options,
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        Action<string, string>? onWorkerStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapters);

        Directory.CreateDirectory(options.RoomDirectoryPath);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, LogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        var resumedFromSnapshot = File.Exists(snapshotPath);
        WorkflowDefinitionSnapshot snapshot;
        if (resumedFromSnapshot)
        {
            snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            await RefuseIfTheNamedTemplateIsNotTheBoundOneAsync(options, snapshot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            snapshot = await BindAndPersistAsync(RequireWorkflowFilePath(options), snapshotPath, cancellationToken).ConfigureAwait(false);
        }

        var bindingConfig = await WorkerBindingConfigParser.LoadFromFileAsync(options.BindingsFilePath, cancellationToken)
            .ConfigureAwait(false);

        // #669: a binding declaring a worktree workspace is provisioned here, before resolution, and its
        // WorkingDirectory rewritten to the provisioned tree — so everything below (and the worker) sees
        // an ordinary directory. Idempotent across resume; torn down after the pump reaches Terminal.
        var (provisionedConfig, provisionedWorktrees) =
            WorktreeWorkspaces.Provision(bindingConfig, options.RoomDirectoryPath);

        // #882: CoreDispatcher only dispatches AerTaskEventKind.StdoutChunk to OnStdoutLine.
        // Stderr chunks write to artifacts/stderrTail but are NOT passed to this callback.
        Action<string, string>? effectiveOnWorkerStdoutLine = onWorkerStdoutLine ?? (options.EchoWorker ? (_, line) => Console.Out.WriteLine(line) : null);

        var profiles = await AerProfileStore.LoadAsync(AerProfileStore.DefaultPath, cancellationToken).ConfigureAwait(false);
        var workerBindings = WorkerBindingResolver.Resolve(
            provisionedConfig, adapters, profiles, Path.GetDirectoryName(options.BindingsFilePath), effectiveOnWorkerStdoutLine);

        var workflowId = new WorkflowId(options.WorkflowId ?? snapshot.WorkflowTemplateId.Value);

        // #1356: invalidate a stale sentinel from a PRIOR terminal attempt against this same room
        // (a pre-ledger failure now being retried with corrected bindings) before this attempt's
        // own pump can run for any length of time. Left in place, it would read as "already done"
        // to a file-watcher for the whole duration of a genuinely fresh run.
        //
        // #1374 F1: skipped when the room's OWN ledger already says Terminal. Deleting a still-valid
        // terminal record before knowing this attempt will produce a new one means a Ctrl-C (or any
        // interruption) landing right after the delete leaves a genuinely-Terminal room with no
        // sentinel at all -- "absence means not terminal yet" (spec/aer-room-spec-v1.0.md) would then
        // be false. A room that is not yet Terminal never has a sentinel to lose, so the probe costs
        // nothing there and the delete still runs.
        var priorProbe = await WorkflowTerminalProbe.ProbeAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!priorProbe.IsTerminal)
        {
            TerminalSentinelWriter.DeleteStaleSentinel(options.RoomDirectoryPath);
        }

        FlowState state;
        {
            // Scoped, not the method-wide `await using` this used to be: a Paused return must
            // release the journal handle before the --wait poll loop below can start, or it holds
            // the exact lock a separate `aer decide` process needs to record the very decision
            // being waited on (FlowJournalHeldException — measured by this PR's own --wait test).
            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var dispatcher = new CoreDispatcher(writer);

            state = await MutationInterface.StartWorkflowAsync(
                    workflowId,
                    options.RoomDirectoryPath,
                    snapshot,
                    workerBindings,
                    artifactsRootPath,
                    reader,
                    writer,
                    dispatcher,
                    inFlightExecutions,
                    cancellationToken,
                    holderDescription: $"aer run pump (pid {Environment.ProcessId})",
                    // #1094: a foreground run that quota-parks would otherwise sit silently until the
                    // reset (~a day out); surface it so the paced wait is legible. To stderr — it is a
                    // status notice, not run output.
                    onVendorQuotaPark: resumesAt => Console.Error.WriteLine(FormatVendorQuotaParkNotice(resumesAt)),
                    settleOnVendorExhaustion: options.SettleOnVendorExhaustion)
                .ConfigureAwait(false);
        }

        // See RunOptions.Wait's own doc for the full contract; this just implements it.
        if (options.Wait && state.Status != WorkflowStatus.Terminal && !cancellationToken.IsCancellationRequested)
        {
            state = await WaitForTerminalAsync(options.RoomDirectoryPath, snapshot, logPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var worktreeTeardowns = WorktreeProvisioner.TeardownIfTerminal(state.Status, provisionedWorktrees);

        return new CommandResult(state, snapshot, resumedFromSnapshot, options.RoomDirectoryPath, worktreeTeardowns);
    }

    /// <summary>
    /// #1356's <c>--wait</c> poll loop: re-projects <paramref name="logPath"/> at a fixed interval
    /// (mirroring <c>StatusCommand.FollowAsync</c>'s own poll-on-length-change technique, without the
    /// per-event printing) until <see cref="WorkflowStatus.Terminal"/> or cancellation. Reads only —
    /// this process already returned its own <see cref="FlowEventLogWriter"/>'s lock by the time this
    /// runs, and the state change being waited on is necessarily written by a different process.
    /// </summary>
    private static async Task<FlowState> WaitForTerminalAsync(
        string roomDirectoryPath, WorkflowDefinitionSnapshot snapshot, string logPath, CancellationToken cancellationToken)
    {
        var reader = new FlowEventLogReader(logPath);
        var lastObservedLength = -1L;

        while (true)
        {
            try
            {
                await Task.Delay(StatusPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var logFile = new FileInfo(logPath);
            var currentLength = logFile.Exists ? logFile.Length : 0;
            if (currentLength == lastObservedLength)
            {
                continue;
            }

            lastObservedLength = currentLength;

            var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
            var state = StateProjector.Project(events, snapshot, checkpoint);
            if (state.Status == WorkflowStatus.Terminal)
            {
                return state;
            }
        }

        // Cancelled before reaching Terminal: report the latest state we actually observed rather
        // than a synthetic one, same as the pump itself does on a host stop.
        var finalEvents = await reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        return StateProjector.Project(finalEvents, snapshot, ProjectionCheckpointStore.Load(roomDirectoryPath));
    }

    /// <summary>Matches <c>StatusCommand</c>'s own follow-poll cadence (#1356) — see that constant's doc for why a fixed poll rather than a <see cref="FileSystemWatcher"/>.</summary>
    private const int StatusPollIntervalMs = 500;

    /// <summary>
    /// #628: resuming the bound snapshot instead of the named file is intended (M15 Phase 1, #137),
    /// but doing it when the two name different templates ran another room's workflow and reported
    /// its result — the measured case replayed a prior terminal run's declared outputs, timeout and
    /// failure reason, wrote no events, and exited non-zero, which is indistinguishable from a
    /// genuine fresh failure.
    ///
    /// <para>
    /// The named file is parsed in full rather than scraped for its id, so there is one parser for
    /// workflow files and no second reading of the same format to drift. A resume naming a file that
    /// exists but is malformed therefore now fails on it, with the same typed
    /// <see cref="WorkflowDefinitionValidationException"/> a fresh bind would raise; a resume naming
    /// something that is not a readable file is left alone entirely, for the reason below.
    /// </para>
    /// </summary>
    /// <summary>
    /// #1094: the foreground quota-park notice. Local time (the operator's own clock, matching
    /// <c>aer status</c>'s <c>FormatParkedStatus</c>), and it names both what happens next (auto-resume
    /// at the 0026-paced instant) and the escape hatch (Ctrl-C, which records a resumable stop) so a
    /// day-long wait is a legible state rather than a hang.
    /// </summary>
    public static string FormatVendorQuotaParkNotice(DateTimeOffset resumesAt)
    {
        var local = resumesAt.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return $"Parked on vendor quota — the run resumes automatically at {local} (local). "
            + "Progress is saved; press Ctrl-C to stop now and resume later by re-running.";
    }

    private static async Task RefuseIfTheNamedTemplateIsNotTheBoundOneAsync(
        RunOptions options, WorkflowDefinitionSnapshot snapshot, CancellationToken cancellationToken)
    {
        // WorkflowFilePath is nullable so an in-process caller resuming a known room directory need
        // not produce one. If a path IS supplied, we check it against the bound snapshot (#653).
        // WorkflowDefinitionParser.LoadFromFileAsync translates missing files into a typed
        // WorkflowDefinitionValidationException (an AerFlowException) — but an EMPTY path would
        // throw an untyped ArgumentException from the BCL, so whitespace means "not supplied" here
        // rather than trusting every caller to normalize before RunOptions is built.
        if (options.WorkflowFilePath is not { } workflowFilePath || string.IsNullOrWhiteSpace(workflowFilePath))
        {
            return;
        }

        var named = await WorkflowDefinitionParser.LoadFromFileAsync(workflowFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (named.WorkflowTemplateId == snapshot.WorkflowTemplateId)
        {
            return;
        }

        throw new ResumedTemplateMismatchException(
            snapshot.WorkflowTemplateId.Value, named.WorkflowTemplateId.Value, options.RoomDirectoryPath);
    }

    /// <summary>
    /// A fresh start with no <see cref="RunOptions.WorkflowFilePath"/> is a caller error, not a
    /// silent no-op — there is nothing to bind a snapshot from (M15 Phase 1, issue #137).
    /// </summary>
    private static string RequireWorkflowFilePath(RunOptions options) => options.WorkflowFilePath
        ?? throw new CliArgumentException(
            $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot yet, and no workflow " +
            "template file was given to start one fresh.");

    private static async Task<WorkflowDefinitionSnapshot> BindAndPersistAsync(
        string workflowFilePath, string snapshotPath, CancellationToken cancellationToken)
    {
        var definition = await WorkflowDefinitionParser.LoadFromFileAsync(workflowFilePath, cancellationToken).ConfigureAwait(false);
        var snapshot = SnapshotBinder.Bind(definition);
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }
}
