using System.Diagnostics;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Scheduling;
using Baton.Status;
using Baton.Store;

namespace Baton.Mutation;

/// <summary>
/// The single external entry point for all Flow state mutation — no other code path may
/// append to <c>flow.jsonl</c>. <see cref="StartWorkflowAsync"/> is the "pump" design decided on: it
/// blocks until the workflow reaches a fixed point. From M8 Phase 3 on, every step ready in a given
/// scheduling round dispatches concurrently rather than one at a time — a diamond's B and C run
/// simultaneously, and a slow step never delays unrelated ready work.
/// </summary>
public static class MutationInterface
{
    // #1183: the longest single Task.Delay the deferral waits below will ever issue, however far out
    // the deadline they are waiting on actually is -- distinct from MaxExhaustionParkHorizon (the
    // longest reset instant GetRetryObligations will trust), since a change to one must not silently
    // move the other. Task.Delay's TimeSpan overload throws past ~49.7 days; the loop's `continue`
    // after each wait re-checks readiness and re-issues the remainder, so any value safely under that
    // ceiling works here.
    private static readonly TimeSpan MaxParkWaitChunk = TimeSpan.FromDays(1);

    /// <summary>
    /// Acquires the room's concurrency guard, then repeatedly projects <see cref="FlowState"/>,
    /// resolves every ready step (retry-aware), and dispatches all of them to Core
    /// concurrently. Each completion (<c>Task.WhenAny</c>) triggers a fresh round — re-projecting
    /// and dispatching any newly-ready work — while the rest stay in flight. Returns once nothing is
    /// ready and nothing remains in flight.
    /// </summary>
    /// <param name="inFlightExecutions">
    /// M10 Phase 2's live-cancellation delivery point: populated with every
    /// process-bound dispatch this call has in flight, so a caller retaining this instance can
    /// cancel one of them via <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/> while
    /// this call is still running — the only way a live execution is reachable at all, since the
    /// concurrency guard blocks any second mutation-surface call for the same room until this one returns.
    /// Defaults to a fresh, unshared instance when the caller has no need to interact with it.
    /// </param>
    /// <param name="cancellationToken">
    /// A host-initiated stop: when cancelled, every execution this call currently has in flight
    /// gets a <see cref="FlowEvent.CancellationRequested"/> recorded and fsync'd, then is signalled —
    /// never the reverse, and never signalled directly without a recorded intent first.
    /// </param>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    public static async Task<FlowState> StartWorkflowAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null,
        string? holderDescription = null,
        // #1094: fired once when the pump enters a paced wait on a vendor-quota (ExhaustedUntil) park,
        // with the local-time-resolvable reset instant. The foreground CLI prints it so a day-long
        // quota wait never reads as a hang; null (the daemon/default) stays silent. Never touches the
        // 0026 wait itself — surfacing only.
        Action<DateTimeOffset>? onVendorQuotaPark = null,
        // #1184 / 0026 §4: when true (attended session turn), an ExhaustedUntil step settles immediately
        // rather than scheduling a paced retry obligation. Defaults to false (unattended workflow steps).
        bool settleOnVendorExhaustion = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        return await PumpToFixedPointAsync(
                workflowId, roomDirectoryPath, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken,
                timeProvider ?? TimeProvider.System, jitterSource ?? (() => Random.Shared.NextDouble()), onVendorQuotaPark, settleOnVendorExhaustion)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A second mutation-surface entry point: records an external decision
    /// against a currently paused execution, resumes the workflow, and drives the consequences to
    /// the next fixed point through the same pump <see cref="StartWorkflowAsync"/> uses. Validates
    /// every <see cref="DecisionType"/> against projected state (the closed-set rules) before
    /// appending anything — an invalid decision throws and leaves the log untouched.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="InvalidExternalDecisionException">The decision violates one of the validation rules.</exception>
    public static async Task<FlowState> RecordDecisionAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        ExecutionId referencedExecutionId,
        DecisionType decisionType,
        StepId? targetStepId = null,
        ExecutionId? supplementaryExecutionId = null,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null,
        string? holderDescription = null,
        bool settleOnVendorExhaustion = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (state, latestCheckpoint) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);
        var succeededExecutionIds = latestCheckpoint.State.SucceededExecutionIds;

        ExternalDecisionValidator.Validate(
            state, snapshot, succeededExecutionIds, referencedExecutionId, decisionType, targetStepId, supplementaryExecutionId);

        var decisionId = new DecisionId(Guid.NewGuid().ToString("n"));

        // Both fsync'd — lifecycle events, same write-sequence discipline as any other append.
        await eventLogWriter.AppendAsync(
                new FlowEvent.ExternalDecisionRecorded(
                    decisionId, referencedExecutionId, decisionType, targetStepId, supplementaryExecutionId),
                cancellationToken)
            .ConfigureAwait(false);
        await eventLogWriter.AppendAsync(new FlowEvent.WorkflowResumed(decisionId), cancellationToken).ConfigureAwait(false);

        return await PumpToFixedPointAsync(
                workflowId, roomDirectoryPath, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken,
                timeProvider ?? TimeProvider.System, jitterSource ?? (() => Random.Shared.NextDouble()),
                settleOnVendorExhaustion: settleOnVendorExhaustion)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A third mutation-surface entry point: mints a step-less supplementary
    /// execution — a human, or any other non-process party, producing a new artifact outside the
    /// DAG during a pause. Appends <see cref="FlowEvent.ExecutionRequestAccepted"/> with
    /// <c>StepId: null</c> and pre-allocates the output directory exactly like any other worker,
    /// but does not run the pump: minting one changes no step's readiness by itself, and
    /// nothing here needs driving to a fixed point (no daemon). The returned
    /// <see cref="ExecutionId"/> becomes usable as a <see cref="DecisionType.RetryWithRevision"/> or
    /// <see cref="DecisionType.Supersede"/> decision's <c>SupplementaryExecutionId</c> once
    /// completion — <see cref="NonProcessCompletionDetector"/>, consulted by a later
    /// <see cref="StartWorkflowAsync"/> or <see cref="RecordDecisionAsync"/> pump — has recorded it
    /// as <see cref="FlowEvent.ExecutionSucceeded"/>.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="UnresolvedWorkerException">
    /// <paramref name="worker"/> has no corresponding <see cref="WorkerBinding.NonProcess"/> among
    /// <paramref name="workerBindings"/> — a supplementary execution is non-process by definition,
    /// so naming a <see cref="WorkerBinding.Process"/> role (or no role at all) is invalid.
    /// </exception>
    public static async Task<(FlowState State, ExecutionId ExecutionId)> RecordSupplementaryExecutionAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        string worker,
        IReadOnlyList<string> inputs,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        CancellationToken cancellationToken = default,
        string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentException.ThrowIfNullOrEmpty(worker);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        if (!workerBindings.TryGetValue(worker, out var binding) || binding is not WorkerBinding.NonProcess nonProcess)
        {
            throw new UnresolvedWorkerException($"No non-process WorkerBinding registered for Worker '{worker}'.");
        }

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        var environment = ArtifactManager.BuildEnvironment(inputs, outputDirectory, artifactsRootPath);
        var outputs = nonProcess.Contract.ProducedOutputs.Select(output => output.Name).ToList();

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            StepId: null,
            worker,
            inputs,
            outputs,
            Timeout: null,
            environment,
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            GrantAuditMode: nonProcess.GrantAuditMode);


        // The write-sequence discipline still applies: appended and fsync'd before this method
        // returns, even though no Core process ever follows it.
        await eventLogWriter.AppendAsync(CreateExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (state, _) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);

        return (state, executionId);
    }

    /// <summary>
    /// A fourth mutation-surface entry point: records an on-demand
    /// cancellation intent — fsync'd before anything else happens, even when the target has already
    /// reached a terminal outcome (a too-late no-op; intent-first ordering) — then
    /// drives the consequences to the next fixed point through the same pump
    /// <see cref="StartWorkflowAsync"/> uses. Phase 1 finalizes only targets with no live Core
    /// process to signal: a pending non-process execution's obligation is fulfilled directly, in the
    /// same round, by <see cref="NonProcessCancellationDetector"/>. A still-running
    /// <see cref="WorkerBinding.Process"/> target's request is durably recorded here but not yet
    /// delivered — that is Phase 2's machinery.
    /// </summary>
    /// <exception cref="WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="UnknownExecutionIdException">
    /// <paramref name="targetExecutionId"/> was never admitted via <see cref="FlowEvent.ExecutionRequestAccepted"/>.
    /// </exception>
    public static async Task<FlowState> RequestCancellationAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        ExecutionId targetExecutionId,
        InFlightExecutionRegistry? inFlightExecutions = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null,
        string? holderDescription = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (_, latestCheckpoint) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);
        var knownExecutionIds = latestCheckpoint.State.AcceptedRequestByExecutionId.Keys.ToHashSet();

        CancellationValidator.Validate(knownExecutionIds, targetExecutionId);

        // The write-sequence discipline: recorded and fsync'd before anything else, whether the
        // target turns out to be a live process, a pending non-process execution, or already
        // terminal (the record itself is the too-late outcome; nothing else changes).
        await eventLogWriter.AppendAsync(new FlowEvent.CancellationRequested(targetExecutionId), cancellationToken)
            .ConfigureAwait(false);

        return await PumpToFixedPointAsync(
                workflowId, roomDirectoryPath, snapshot, workerBindings, artifactsRootPath, eventLogReader, eventLogWriter, dispatcher,
                inFlightExecutions ?? new InFlightExecutionRegistry(), cancellationToken,
                timeProvider ?? TimeProvider.System, jitterSource ?? (() => Random.Shared.NextDouble()))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A fifth mutation-surface entry point (issue #1359): re-enters an already-dispatched step's
    /// worker with a new message on the same workspace and grants, via the adapter's existing
    /// resume-session plumbing (<c>WorkerInvocation.ResumeSession</c>/<c>SessionId</c>, in
    /// <c>Baton.Vendors</c> — <c>Baton</c> never references that assembly, per Adapter Isolation).
    /// <paramref name="workerBindings"/> must already carry the resume-shaped override for
    /// <paramref name="worker"/> (<c>ResumeSession: true</c>, the operator's message as its
    /// <c>PromptTemplate</c>) — <c>Baton.Cli.ResumeCommand</c>
    /// builds that override the same way <c>SupplyCommand</c> overlays its own single-worker binding;
    /// this method only decides WHICH step that binding dispatches against and links the resulting
    /// execution to the one it continues.
    /// <para>
    /// Unlike <see cref="StartWorkflowAsync"/>'s readiness-driven dispatch, this always dispatches
    /// exactly one execution regardless of <see cref="Scheduling.DependencyResolver"/>'s ordinary
    /// conditions — a resume is an explicit operator override of an already-terminal (or paused)
    /// step, not a step the DAG itself would ever re-offer as ready on its own. Blocks until that one
    /// dispatch completes and its outcome is recorded; unlike every other entry point above, this
    /// does NOT pump to a fixed point on its own (#1359's scope: "one message per resume invocation",
    /// no cascading multi-step orchestration folded in here) — a caller wanting downstream
    /// consequences (a sibling step this one's outcome newly unblocks, or a pause obligation) makes a
    /// separate <see cref="StartWorkflowAsync"/> call afterward, the same two-call sequence
    /// <c>SupplyCommand</c> already uses for its own single-execution mutation.
    /// </para>
    /// </summary>
    /// <param name="worker">
    /// The worker ROLE (<see cref="WorkflowStepDefinition.Worker"/>) to resume — identifies the
    /// target step by which snapshot step declares it, not by step id. Refused as ambiguous if more
    /// than one step in <paramref name="snapshot"/> names the same worker.
    /// </param>
    /// <param name="sessionId">
    /// The vendor session id the caller's bindings file records for <paramref name="worker"/> right
    /// now, stored on <see cref="ExecutionRequest.SessionId"/> (that field's doc owns the why). Here
    /// it is also the refusal input: a resume whose target execution already recorded a DIFFERENT
    /// session id is refused up front instead of silently forking the vendor session. <c>null</c> is
    /// never checked against — the first resume of an ordinary dispatch has nothing to compare.
    /// </param>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds <paramref name="roomDirectoryPath"/>'s lock.
    /// </exception>
    /// <exception cref="InvalidResumeException">
    /// No step names <paramref name="worker"/>, more than one does, the target step has never been
    /// dispatched (<see cref="StepStatus.Pending"/>), its latest attempt is still
    /// <see cref="StepStatus.Running"/> (mid-flight steering is out of #1359's scope),
    /// <paramref name="workerBindings"/> resolves it to a <see cref="WorkerBinding.NonProcess"/>
    /// (nothing to resume a session on), or <paramref name="sessionId"/> disagrees with the session
    /// id the execution being resumed actually recorded (F6).
    /// </exception>
    public static async Task<(FlowState State, ExecutionId ExecutionId)> RecordResumeAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        string worker,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        string? holderDescription = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workerBindings);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);
        ArgumentException.ThrowIfNullOrEmpty(worker);
        ArgumentNullException.ThrowIfNull(eventLogReader);
        ArgumentNullException.ThrowIfNull(eventLogWriter);
        ArgumentNullException.ThrowIfNull(dispatcher);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, holderDescription);

        var checkpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var log = await eventLogReader.ReadSnapshotFromOffsetAsync(checkpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (log.IsFallbackToFull)
        {
            checkpoint = null;
        }
        var (state, resumeCheckpoint) = StateProjector.ProjectAndCheckpoint(log.FlowEvents, snapshot, checkpoint, log.ByteOffset);

        var matchingSteps = snapshot.Steps.Where(s => s.Worker == worker).ToList();
        if (matchingSteps.Count == 0)
        {
            throw new InvalidResumeException($"No step in this workflow names worker '{worker}'.")
            {
                TryInvocation = "pass --worker naming one of this workflow's roles: " +
                    $"{string.Join(", ", snapshot.Steps.Select(s => s.Worker).Distinct())}.",
            };
        }

        if (matchingSteps.Count > 1)
        {
            throw new InvalidResumeException(
                $"Worker '{worker}' is bound to {matchingSteps.Count} steps " +
                $"({string.Join(", ", matchingSteps.Select(s => s.StepId))}) — baton resume needs a single, " +
                "unambiguous target step.")
            {
                TryInvocation = "give each step its own worker name in the workflow definition, so baton " +
                    "resume can target exactly one.",
            };
        }

        var stepDefinition = matchingSteps[0];
        var stepState = state.Steps.Single(s => s.StepId == stepDefinition.StepId);

        if (stepState.Status == StepStatus.Pending)
        {
            throw new InvalidResumeException($"Step '{stepDefinition.StepId}' (worker '{worker}') has never run — nothing to resume.")
            {
                TryInvocation = "dispatch it at least once first (`baton run` or `baton dispatch`), then resume it.",
            };
        }

        if (stepState.Status == StepStatus.Running)
        {
            // #1359 F3: room-says-Running is not the same fact as "the engine dispatching it is still
            // alive" — reuse the same probe `baton status`'s human rendering already consults rather than
            // inventing a second liveness mechanism (StatusCommand.FormatStepStatus).
            var allEvents = await eventLogReader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var accepted = allEvents.OfType<FlowEvent.ExecutionRequestAccepted>()
                .LastOrDefault(e => e.Request.ExecutionId == stepState.LatestExecutionId);
            var liveness = EngineLivenessProbe.Probe(accepted?.EnginePid, accepted?.EngineStartTime);

            if (liveness.Status != EngineLivenessStatus.Dead)
            {
                var unknownSuffix = liveness.Status == EngineLivenessStatus.Unknown ? $" (liveness unknown: {liveness.Why})" : string.Empty;
                throw new InvalidResumeException(
                    $"Step '{stepDefinition.StepId}' (worker '{worker}') is still running{unknownSuffix} — baton resume only " +
                    "continues a terminal or stalled (paused) worker; steering a live one is out of scope for " +
                    "this verb.")
                {
                    TryInvocation = $"wait for the current run to finish, or check `baton status {roomDirectoryPath}` " +
                        "for progress; retry once it reaches a terminal or stalled state.",
                };
            }

            // STALLED (#1359 F3): the room projects Running, but the engine that accepted this
            // execution is provably dead — the crash-recovery case this verb exists to rescue.
            // Record the takeover before dispatching the resume's own linked execution, so the
            // orphaned attempt is never left with an accepted request and no resolution.
            await eventLogWriter.AppendAsync(
                    new FlowEvent.ExecutionFailed(
                        stepState.LatestExecutionId!.Value,
                        FailureClassification.Retryable,
                        "Abandoned: baton resume found the engine behind this execution is no longer alive " +
                        "(a stalled run) and took over the step."),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var previousExecutionId = stepState.LatestExecutionId
            ?? throw new InvalidResumeException($"Step '{stepDefinition.StepId}' (worker '{worker}') has no recorded execution to resume.")
            {
                TryInvocation = "re-run `baton run` (or `baton dispatch`) to dispatch it fresh — there is no recorded execution for baton resume to continue.",
            };

        // F6: the execution being resumed already recorded which session IT continued (null for the
        // first resume of an ordinary dispatch, which never had one). If the bindings file now names
        // a DIFFERENT session, the operator's SessionId edit and the ledger's own history disagree —
        // refuse rather than silently record a continuity nothing actually backs.
        if (resumeCheckpoint.State.AcceptedRequestByExecutionId.TryGetValue(previousExecutionId, out var previousRequest)
            && previousRequest.SessionId is { } previousSessionId
            && sessionId is not null
            && !string.Equals(previousSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidResumeException(
                $"Worker '{worker}''s bindings file records SessionId '{sessionId}', but the execution " +
                $"being resumed ({previousExecutionId}) already recorded session '{previousSessionId}' — " +
                "baton resume refuses rather than silently forking the vendor session under a claimed " +
                "continuity nothing backs.")
            {
                TryInvocation = $"fix the SessionId recorded for '{worker}' in the bindings file back to " +
                    $"'{previousSessionId}' (the session the execution being resumed actually continued), " +
                    "or target the room/worker whose bindings file's SessionId edit was intentional.",
            };
        }

        if (!workerBindings.TryGetValue(worker, out var binding) || binding is not WorkerBinding.Process processBinding)
        {
            throw new InvalidResumeException($"Worker '{worker}' has no dispatchable (process) binding to resume a session on.")
            {
                TryInvocation = $"check the bindings file's entry for '{worker}' — baton resume needs a Process " +
                    "binding (a vendor CLI with a session to continue), not a non-process worker.",
            };
        }

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var inputPaths = ArtifactManager.ResolveInputPaths(stepDefinition, snapshot, state, artifactsRootPath);
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
        var environment = ArtifactManager.BuildEnvironment(inputPaths, outputDirectory, artifactsRootPath);

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            stepDefinition.StepId,
            worker,
            inputPaths,
            stepDefinition.Outputs,
            processBinding.Timeout,
            environment,
            stepState.UpstreamExecutionIds,
            GrantAuditMode: binding.GrantAuditMode,
            LinkedFromExecutionId: previousExecutionId,
            SessionId: sessionId,
            Adapter: processBinding.Adapter,
            Model: processBinding.Model);

        // The write-sequence rule: intent recorded and fsync'd before Core is ever asked to run.
        await eventLogWriter.AppendAsync(CreateExecutionRequestAccepted(request), cancellationToken).ConfigureAwait(false);

        var inFlightExecutions = new InFlightExecutionRegistry();
        inFlightExecutions.Bind(eventLogWriter);
        var dispatchCancellationToken = inFlightExecutions.Register(executionId);
        var prepared = new PreparedExecution(request, outputDirectory);

        // Awaited directly, not fire-and-forget: a resume is a single-shot operation that blocks and
        // reports exactly like the rest of this surface (DecideCommand's own doc comment states the
        // same contract), not a round dispatching arbitrarily many concurrent siblings.
        await DispatchAndRecordOutcomeAsync(
                prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken, timeProvider ?? TimeProvider.System)
            .ConfigureAwait(false);

        var finalCheckpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        var finalLog = await eventLogReader.ReadSnapshotFromOffsetAsync(finalCheckpoint?.ByteOffset ?? 0, cancellationToken).ConfigureAwait(false);
        if (finalLog.IsFallbackToFull)
        {
            finalCheckpoint = null;
        }
        var (finalState, _) = StateProjector.ProjectAndCheckpoint(finalLog.FlowEvents, snapshot, finalCheckpoint, finalLog.ByteOffset);

        return (finalState, executionId);
    }

    /// <summary>
    /// The scheduling pump shared by every mutation-surface entry point that needs one: repeatedly
    /// projects <see cref="FlowState"/>, finalizes any settled non-process execution, finalizes any
    /// non-process execution with an unfulfilled cancellation request, appends any owed
    /// <see cref="FlowEvent.WorkflowPaused"/> obligations, resolves every ready step, and dispatches
    /// all of them concurrently — to Core, or, for a <see cref="WorkerBinding.NonProcess"/> step,
    /// nowhere at all — until nothing is ready and nothing remains in flight. Assumes the caller
    /// already holds the concurrency guard.
    /// </summary>
    /// <remarks>
    /// M10 Phase 2: every process-bound dispatch this loop starts is registered with
    /// <paramref name="inFlightExecutions"/> under its own <see cref="CancellationTokenSource"/> —
    /// never the ambient <paramref name="cancellationToken"/> directly, so a cancellation of that
    /// host token can never reach Core without <see cref="FlowEvent.CancellationRequested"/> being
    /// recorded first. While dispatches are in flight, this loop also races
    /// <paramref name="cancellationToken"/> itself: the instant it is cancelled, every execution
    /// still registered gets its intent recorded and is then signalled via
    /// <see cref="InFlightExecutionRegistry.RequestStopAsync"/> — the host-initiated stop.
    /// </remarks>
    private static async Task<FlowState> PumpToFixedPointAsync(
        WorkflowId workflowId,
        string roomDirectoryPath,
        WorkflowDefinitionSnapshot snapshot,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings,
        string artifactsRootPath,
        IEventLogReader eventLogReader,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        Func<double> jitterSource,
        Action<DateTimeOffset>? onVendorQuotaPark = null,
        bool settleOnVendorExhaustion = false)
    {
        inFlightExecutions.Bind(eventLogWriter);

        var inFlight = new List<Task>();
        var hostStopRequested = false;

        // #1094: dedupes the vendor-quota park notice to the reset instant currently being waited on,
        // so re-projection loops do not reprint it. Surfacing only — see onVendorQuotaPark.
        DateTimeOffset? lastQuotaParkNotified = null;

        // Starts as the caller's own token, but is switched to CancellationToken.None the instant a
        // host stop is detected below (M10 Phase 2): every read/write this loop performs to reach
        // its fixed point must keep completing even after the ambient token has fired, or the pump
        // could never converge to the consistent, fully-classified state a host stop promises.
        var ioCancellationToken = cancellationToken;
        FlowState state;
        ProjectionCheckpoint? currentCheckpoint = ProjectionCheckpointStore.Load(roomDirectoryPath);
        ProjectionCheckpoint? latestCheckpoint = null;

        while (true)
        {
            try
            {
                // Captured before the log read below, not after (issue #81): a sibling dispatch's
                // DispatchAndRecordOutcomeAsync always appends its outcome and fsyncs it before calling
                // Unregister, so if an ExecutionId has already dropped out of this snapshot, the append
                // that preceded its Unregister is guaranteed to already be durable — and therefore
                // visible to the log read started right after. Reading the log first and checking the
                // registry second (the previous order) offered no such guarantee: a sibling could finish
                // its append-then-Unregister sequence in the gap after the read had already started,
                // leaving a Running step that looks unregistered and unstarted-in-Core — indistinguishable
                // from the "safe pre-spawn crash" state — even though it had, in fact, just succeeded.
                var registeredExecutionIds = inFlightExecutions.RegisteredExecutionIds();

                // A single read of the combined log per round — feeding both Flow's own projection and
                // M10 Phase 3's crash reconciliation from one pass, rather than reading and parsing the
                // same file twice for no new information.
                var log = await eventLogReader.ReadSnapshotFromOffsetAsync(currentCheckpoint?.ByteOffset ?? 0, ioCancellationToken).ConfigureAwait(false);
                if (log.IsFallbackToFull)
                {
                    currentCheckpoint = null;
                }
                var events = log.FlowEvents;
                var projection = StateProjector.ProjectAndCheckpoint(events, snapshot, currentCheckpoint, log.ByteOffset);
                state = projection.State;
                latestCheckpoint = projection.Checkpoint;
                currentCheckpoint = latestCheckpoint;

                var acceptedRequestByExecutionId = latestCheckpoint.State.AcceptedRequestByExecutionId;

                // M10 Phase 3 (full robustness): joins Core's half of the log — read back here for
                // the first time since M7 Phase 6 wrote it — to Flow's own intents by ExecutionId,
                // distinguishing a process-bound step's "genuinely still Running" from "a prior pump
                // crashed before recording its outcome" (until now indistinguishable, per StateProjector's
                // own comment). A dispatch this very call still has registered is excluded — that pump is
                // this pump, not a crashed one.
                var (mergedStarted, mergedExited) = CoreEventAggregation.Merge(
                    latestCheckpoint.State.CoreStartedExecutionIds,
                    latestCheckpoint.State.CoreExitedByExecutionId,
                    log.CoreEvents);

                // Folded back into the working checkpoint immediately, not only at the save site:
                // each round reads the log from the previous round's offset, so a later round's
                // merge must start from these aggregates or the earlier tail's core events vanish
                // from its view. Load-bearing whenever one read surfaces obligations in two
                // priority buckets — the bucket blocks below each `continue` after acting, so the
                // lower bucket's execution is handled a round AFTER the read that observed it, and
                // without this carry it would re-derive as ToResubmit: a duplicate live dispatch
                // of a process that may still be running (PumpCheckpointCarryTests' two-bucket
                // fixture is exactly that trace).
                latestCheckpoint = latestCheckpoint with
                {
                    State = latestCheckpoint.State with
                    {
                        CoreStartedExecutionIds = mergedStarted,
                        CoreExitedByExecutionId = mergedExited,
                    },
                };
                currentCheckpoint = latestCheckpoint;

                var crashRecovery = ProcessCrashRecoveryDetector.GetObligations(
                    state, snapshot, workerBindings, mergedStarted, mergedExited, registeredExecutionIds);

                // ToClassify: the recorded exit and the contract on disk decide, exactly as if the
                // completion had just arrived — see ProcessCrashRecoveryDetector's remarks for the
                // obligation taxonomy; an unfulfilled cancellation request simply derives as too late
                // unless the recorded exit reason was itself CancelRequested (the crash clause).
                if (crashRecovery.ToClassify.Count > 0)
                {
                    foreach (var (executionId, exit) in crashRecovery.ToClassify)
                    {
                        var request = acceptedRequestByExecutionId[executionId];
                        var contract = GetContractForClassification(request, workerBindings);
                        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId);
                        // The recorded request is the durable truth: a pre-#901 line carries no
                        // GrantAuditMode, which means no audit was promised for that execution —
                        // falling back to the binding's CURRENT mode would reinterpret history
                        // (and fail-closed against a worktree that may be long gone).
                        var grantAuditMode = request.GrantAuditMode ?? GrantAuditMode.Enforced;
                        string? worktreePath = null;
                        IWorkerResponseParser? responseParser = null;
                        try
                        {
                            if (workerBindings.TryGetValue(request.Worker, out var b) && b is WorkerBinding.Process p)
                            {
                                worktreePath = p.Target.WorkingDirectory;
                                responseParser = p.ResponseParser;
                            }
                        }
                        catch (BatonFlowException)
                        {
                            // A recovery candidate's binding may legitimately refuse to resolve —
                            // the crash clause classifies from recorded facts alone (the test
                            // pinning this: StartWorkflowAsync_classifies_crash_recovery_candidate_
                            // when_its_worker_binding_refuses_to_resolve). The consequence is not a
                            // skip: if the journal promised an audit, Classify fails closed on the
                            // null worktree path.
                        }

                        // #1586 S1: the same recorded-adapter preference ExecutionUsageProjector's own
                        // #1567 comment explains — the durable request, not the binding's current
                        // resolution, since this is the crash-recovery path classifying from recorded
                        // facts alone.
                        var usageParser = request.Adapter is { } recoveryAdapter
                            ? StandardWorkerUsageParsers.Default.GetValueOrDefault(recoveryAdapter)
                            : null;

                        var classification = OutcomeClassifier.Classify(
                            new CoreDispatchResult(exit.ExitCode, exit.Reason, exit.StderrTail), contract, outputDirectory,
                            grantAuditMode: grantAuditMode, worktreePath: worktreePath, responseParser: responseParser,
                            usageParser: usageParser);

                        await eventLogWriter.AppendAsync(ToOutcomeEvent(executionId, classification), ioCancellationToken)
                            .ConfigureAwait(false);
                        await AppendZeroOutputsTripwireIfAnyAsync(eventLogWriter, executionId, classification, ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // No ExecutionStarted was ever recorded for this target (the crash clause): the cancel
                // wins, finalized directly — there was never anything to forward to Core in the first
                // place, and re-dispatching now would race the intent that already decided this attempt
                // is not to run.
                if (crashRecovery.ToFinalizeAsCancelled.Count > 0)
                {
                    foreach (var executionId in crashRecovery.ToFinalizeAsCancelled)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // The orphan (the third crash state): ExecutionStarted with no ExecutionExited, this
                // call's own registry proving it is not still genuinely in flight here. Nothing can
                // re-attach (no daemon; BatonTask is spawn-and-await) and a second
                // execution for the same request is forbidden, so the attempt is finalized from recorded facts alone
                // as abandoned — a real, chargeable failed attempt — regardless of whether a
                // cancellation was also pending for it. There is no live handle left to re-issue a
                // cancellation toward (this pump is not the one that dispatched it); the best-effort
                // re-issue the spec allows for is therefore a documented no-op given BatonTask has no
                // cross-process re-attach capability, not a new mechanism this phase introduces.
                if (crashRecovery.ToFinalizeAsAbandoned.Count > 0)
                {
                    foreach (var executionId in crashRecovery.ToFinalizeAsAbandoned)
                    {
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.ExecutionFailed(
                                    executionId,
                                    FailureClassification.Retryable,
                                    "Abandoned during crash recovery: no ExecutionExited was recorded for this execution before Flow restarted."),
                                ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation, re-evaluated from projected state on every round for
                // the same crash-safety reason the pause obligation below is: the filesystem is read
                // only here, at classification time, and the resulting ExecutionSucceeded is the
                // durable truth from then on. Must run before pause obligations, so a step that
                // just settled this way can still owe a WorkflowPaused append in the same pass.
                var settledNonProcessExecutionIds = NonProcessCompletionDetector.GetSettledExecutions(
                    state, snapshot, workerBindings, artifactsRootPath);
                if (settledNonProcessExecutionIds.Count > 0)
                {
                    foreach (var executionId in settledNonProcessExecutionIds)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation (vacuous with no process), re-evaluated from
                // projected state on every round for the same crash-safety reason as the settlement
                // check above. Must run before pause obligations, so a step just cancelled this way can
                // still owe a WorkflowPaused append in the same pass.
                var cancelledNonProcessExecutionIds = NonProcessCancellationDetector.GetCancelledExecutions(
                    state, snapshot, workerBindings);
                if (cancelledNonProcessExecutionIds.Count > 0)
                {
                    foreach (var executionId in cancelledNonProcessExecutionIds)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation, re-evaluated from projected state on every round rather
                // than welded into the dispatch continuation, so a crash between the outcome event and
                // this append loses nothing. Appending changes a paused step's projected
                // status from its terminal outcome to Paused, which must be reflected before readiness
                // is resolved — re-reading and re-projecting the freshly appended events is simpler than
                // threading that one status change through by hand.
                var pauseObligations = PauseEngine.GetPauseObligations(state, snapshot);
                if (pauseObligations.Count > 0)
                {
                    foreach (var (stepId, executionId) in pauseObligations)
                    {
                        await eventLogWriter.AppendAsync(new FlowEvent.WorkflowPaused(executionId, stepId), ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // A derived obligation (#712), re-evaluated from projected state on every round for
                // the same crash-safety reason the pause obligation above is: evaluated after pause obligations
                // and before readiness.
                var retryObligations = GetRetryObligations(state, snapshot, timeProvider, jitterSource, settleOnVendorExhaustion);
                if (retryObligations.Count > 0)
                {
                    foreach (var obligation in retryObligations)
                    {
                        await eventLogWriter.AppendAsync(
                                new FlowEvent.StepRetryScheduled(
                                    obligation.StepId,
                                    obligation.ForExecutionId,
                                    obligation.RetryNotBefore,
                                    obligation.RetryDelayMs),
                                ioCancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }

                // Once a host stop is underway, no newly-ready step should be dispatched — cancellation
                // is winding this call down, not making room for fresh work. The same applies to a
                // crash-recovery resubmission (M10 Phase 3): it is a brand-new dispatch to Core too.
                var now = timeProvider.GetUtcNow();
                var readyStepIds = hostStopRequested
                    ? (IReadOnlySet<StepId>)new HashSet<StepId>()
                    : DependencyResolver.GetReadySteps(state, snapshot, now);
                var toResubmit = hostStopRequested ? (IReadOnlyList<ExecutionId>)[] : crashRecovery.ToResubmit;

                // Snapshot declaration order, not the ready set's (unordered) iteration order, so a
                // round's intents are always emitted in the same sequence for the same FlowState
                // regardless of how concurrent dispatches later complete.
                foreach (var stepDefinition in snapshot.Steps)
                {
                    if (!readyStepIds.Contains(stepDefinition.StepId))
                    {
                        continue;
                    }

                    if (!workerBindings.TryGetValue(stepDefinition.Worker, out var binding))
                    {
                        throw new UnresolvedWorkerException(
                            $"No WorkerBinding registered for Worker '{stepDefinition.Worker}' (step '{stepDefinition.StepId}').");
                    }

                    // The write-sequence rule, extended to a concurrent round: each intent is appended
                    // and fsync'd here — awaited sequentially, in declaration order — before that step's
                    // own dispatch is even started, and before the next step's intent is written.
                    var prepared = await PrepareExecutionAsync(
                            workflowId, stepDefinition, snapshot, state, binding, artifactsRootPath, eventLogWriter, ioCancellationToken)
                        .ConfigureAwait(false);

                    // A non-process worker is fully handled by the append above: no Core
                    // process to spawn, so nothing joins the in-flight set. The pump reaches its fixed
                    // point with the step awaiting external completion (no daemon); a later round's
                    // NonProcessCompletionDetector call is what eventually finalizes it.
                    if (binding is WorkerBinding.Process processBinding)
                    {
                        // Registered under its own token (M10 Phase 2) — never the ambient
                        // cancellationToken directly — so this specific execution, and only this one, can
                        // be signalled without touching a sibling dispatched in the same round.
                        var executionId = prepared.Request.ExecutionId;
                        var dispatchCancellationToken = inFlightExecutions.Register(executionId);

                        // Not awaited here: starts the dispatch and joins the in-flight set, so a slow
                        // step never blocks this round from dispatching the rest of its ready work.
                        inFlight.Add(DispatchAndRecordOutcomeAsync(
                            prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken, timeProvider));
                    }
                }

                // M10 Phase 3's re-submission crash state: the same attempt, not a retry — the
                // intent is already durably recorded (ExecutionRequestAccepted), so this re-dispatches
                // the existing request as-is rather than calling PrepareExecutionAsync, which would
                // append a new one and charge a fresh ExecutionId against nothing.
                foreach (var executionId in toResubmit)
                {
                    var request = acceptedRequestByExecutionId[executionId];
                    var processBinding = (WorkerBinding.Process)workerBindings[request.Worker];
                    var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);
                    var prepared = new PreparedExecution(request, outputDirectory);

                    var dispatchCancellationToken = inFlightExecutions.Register(executionId);
                    inFlight.Add(DispatchAndRecordOutcomeAsync(
                        prepared, processBinding, eventLogWriter, dispatcher, inFlightExecutions, dispatchCancellationToken, timeProvider));
                }

                if (inFlight.Count == 0)
                {
                    // A round that dispatched only non-process work still changed projected state (new
                    // ExecutionRequestAccepted events) even though nothing joined inFlight — loop back
                    // around to re-project and return the state that actually reflects it, rather than
                    // the stale snapshot read at the top of this iteration.
                    if (readyStepIds.Count > 0)
                    {
                        continue;
                    }

                    // Only a deadline still ahead justifies waiting, measured against the same `now`
                    // the resolver just used. A deferral whose deadline has already passed while its
                    // step stayed un-ready is blocked on something other than time — a dependency
                    // superseded and then terminally failed — and a passed deadline can never become
                    // ready by waiting, so treating it as waitable turns this branch into a zero-delay
                    // spin (delay <= 0, continue, re-project, repeat). With no future deadline, no
                    // ready step and nothing in flight, this state IS the pump's fixed point.
                    var pendingDeferrals = state.Steps
                        .Where(s => s.RetryNotBefore is not null && s.RetryNotBefore.Value > now)
                        .Select(s => s.RetryNotBefore!.Value)
                        .ToList();

                    if (pendingDeferrals.Count > 0 && !hostStopRequested && !state.Steps.Any(s => s.Status == StepStatus.Paused))
                    {
                        var minNotBefore = pendingDeferrals.Min();
                        var nowAtCheck = timeProvider.GetUtcNow();
                        var delay = minNotBefore - nowAtCheck;

                        if (delay > TimeSpan.Zero)
                        {
                            // #1094: surface a vendor-quota park to the foreground before the (possibly
                            // day-long) paced wait, so it does not read as a hang. Ordinary retry backoff
                            // is not a quota park and stays quiet. Notification only — the 0026 wait below
                            // is unchanged.
                            var quotaParkStep = state.Steps.FirstOrDefault(s => s.RetryNotBefore == minNotBefore
                                && s.LatestFailureClassification == FailureClassification.ExhaustedUntil);
                            if (onVendorQuotaPark is not null && quotaParkStep is not null)
                            {
                                // #1183: deduped on the RAW vendor-reported instant
                                // (LatestExecutionFailedRetryNotBefore), not the paced `minNotBefore` —
                                // PastResetInstantRetryFloor recomputes a fresh `now + 1s` obligation on
                                // every retry of a repeating stale instant, so deduping on the paced value
                                // would re-notify (and re-print) once per second forever instead of once
                                // per distinct vendor refusal.
                                var dedupeInstant = quotaParkStep.LatestExecutionFailedRetryNotBefore ?? minNotBefore;
                                if (lastQuotaParkNotified != dedupeInstant)
                                {
                                    lastQuotaParkNotified = dedupeInstant;
                                    onVendorQuotaPark(minNotBefore);
                                }
                            }

                            // #1183: Task.Delay's TimeSpan overload throws past ~49.7 days -- clamp
                            // to a chunk and let the loop's `continue` below re-check readiness and
                            // re-issue the remainder, rather than trust `delay` to already be sane.
                            // GetRetryObligations caps every obligation it schedules, so this is
                            // belt-and-suspenders for the wait itself, not the only guard.
                            var chunkedDelay = delay > MaxParkWaitChunk ? MaxParkWaitChunk : delay;
                            var delayTask = Task.Delay(chunkedDelay, timeProvider, ioCancellationToken);
                            var deferralHostStopWatcher = cancellationToken.CanBeCanceled
                                ? Task.Delay(Timeout.Infinite, cancellationToken)
                                : null;

                            // #1563 (S0 of the quota design, #802): captured fresh on every entry into
                            // this wait, never reused — a cancel.request the poller could not deliver
                            // through the registry above (no live process; the worker already exited)
                            // marks this same latch (also wired into the busy `waitCandidates` wait
                            // below, for the sibling-still-in-flight shape), so a park that would
                            // otherwise sit until `delayTask` — possibly a day out on a vendor quota
                            // reset — wakes on the next round instead. See
                            // InFlightExecutionRegistry.MarkParkedCancelIntent's own doc for the
                            // #1556 follow-up this latch is meant to fold into (F6, #1605 review:
                            // record once, not restated here).
                            var parkedCancelWake = inFlightExecutions.NextParkedCancelWake();

                            var deferralCandidates = new List<Task> { delayTask, parkedCancelWake };
                            if (deferralHostStopWatcher is not null)
                            {
                                deferralCandidates.Add(deferralHostStopWatcher);
                            }

                            var completedWait = await Task.WhenAny(deferralCandidates).ConfigureAwait(false);
                            // The delay task and the watcher cancel off the same host token, so a host
                            // stop can complete the *delay* task first and WhenAny returns it instead of
                            // the watcher. Reaching the token directly closes that race: without it, the
                            // next round's tail read returns synchronously when the log has no new bytes
                            // (no awaited token observation anywhere in the round), both tasks arrive
                            // here already cancelled, WhenAny picks the delay task again, and the loop
                            // spins without ever noticing the stop (Test12's 30s timeout under load).
                            // F1 sub-point (#1605 review): this guard wins the race over
                            // `parkedCancelWake` below whenever both fire around the same instant — a
                            // parked-cancel mark landing in the same tick as a host stop is dropped:
                            // this call returns without ever draining it, RequestStopAsync below only
                            // reaches a live process's CancellationTokenSource (the parked step has
                            // none), and the in-memory mark itself does not survive process exit.
                            // Accepted, not fixed: this pump call is exiting either way, the parked
                            // step was never going to settle through it once a host stop lands, and
                            // CancelRequestFile.DeleteStalePendingRequest sweeps any still-pending
                            // request file on the room's next `baton run` regardless — so the worst
                            // case is the operator re-issuing `baton cancel` once that run starts, not
                            // a request that silently vanishes with no trace.
                            if (completedWait == deferralHostStopWatcher || cancellationToken.IsCancellationRequested)
                            {
                                hostStopRequested = true;
                                ioCancellationToken = CancellationToken.None;
                                await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
                            }
                            else if (completedWait == parkedCancelWake)
                            {
                                // F8 (#1605 review): reset BEFORE drain, load-bearing, not incidental
                                // ordering. A mark landing between the two calls below still lands
                                // safely either way: ResetParkedCancelWake only swaps in a fresh latch
                                // if the one it is given is still current, so a mark racing this reset
                                // either signals the brand-new latch (caught next round instantly,
                                // since it is already complete) or still lands in the set the drain
                                // just below reads. Drain-then-reset would instead let that same mark
                                // signal the OLD, already-fired latch (a no-op — TrySetResult on a
                                // completed TCS does nothing) and then get its own fresh latch swapped
                                // out from under it by the reset that follows, stalling the intent
                                // until some unrelated future mark happens to notice it pending.
                                inFlightExecutions.ResetParkedCancelWake(parkedCancelWake);
                                await SettleParkedCancelIntentsAsync(state, inFlightExecutions, eventLogWriter, ioCancellationToken)
                                    .ConfigureAwait(false);
                            }

                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (latestCheckpoint is not null)
                    {
                        // Write pruned merged core aggregates into the checkpoint before saving.
                        // Invariant note: Carrying core aggregates in the checkpoint removes the reliance on saving
                        // checkpoints only at clean pump return (after in-flight stops record exits) for correctness.
                        // However, saving at clean return remains a performance assumption to avoid unneeded disk writes.
                        var (prunedStarted, prunedExited) = CoreEventAggregation.Prune(mergedStarted, mergedExited, state);
                        latestCheckpoint = latestCheckpoint with
                        {
                            State = latestCheckpoint.State with
                            {
                                CoreStartedExecutionIds = prunedStarted,
                                CoreExitedByExecutionId = prunedExited
                            }
                        };
                        ProjectionCheckpointStore.Save(roomDirectoryPath, latestCheckpoint);
                    }

                    return state;
                }

                // Races the round's in-flight dispatches against the host token itself (M10 Phase 2): a
                // Task.Delay(Timeout.Infinite, ...) never completes on its own, only transitions to
                // Canceled the instant cancellationToken fires, which Task.WhenAny treats as "done" —
                // exactly the wakeup a host-initiated stop needs without polling.
                var hostStopWatcher = !hostStopRequested && cancellationToken.CanBeCanceled
                    ? Task.Delay(Timeout.Infinite, cancellationToken)
                    : null;
                var waitCandidates = new List<Task>(inFlight);
                if (hostStopWatcher is not null)
                {
                    waitCandidates.Add(hostStopWatcher);
                }

                // #1563: the same wake this loop's idle-deferral branch watches, needed here too —
                // a DIFFERENT step sitting quota-parked (Failed, future RetryNotBefore) while THIS
                // step's dispatch is still in flight would otherwise only wake on that dispatch
                // completing, a host stop, or `deferralWakeup` below (which fires at the very
                // deadline the cancel exists to end early) — reachable review finding: a workflow
                // with any sibling step running concurrently reopens the exact bug this issue fixes
                // for the parked one. Captured fresh every entry into this wait, same as the idle
                // branch, so a mark landing anywhere before capture is never lost.
                var waitParkedCancelWake = inFlightExecutions.NextParkedCancelWake();
                waitCandidates.Add(waitParkedCancelWake);

                // A deferral deadline must wake this wait too, not only the idle branch above: a
                // deferred retry whose sibling is still mid-flight would otherwise sleep until that
                // sibling completes, stretching a sub-second backoff to the sibling's full runtime.
                // The timer only wakes the loop — releasing the step stays the resolver's decision on
                // the re-projection after `continue`, same as the idle branch.
                Task? deferralWakeup = null;
                if (!hostStopRequested)
                {
                    var pendingRetryDeadlines = state.Steps
                        .Where(s => s.RetryNotBefore is not null)
                        .Select(s => s.RetryNotBefore!.Value)
                        .ToList();
                    if (pendingRetryDeadlines.Count > 0)
                    {
                        var wakeDelay = pendingRetryDeadlines.Min() - timeProvider.GetUtcNow();
                        if (wakeDelay > TimeSpan.Zero)
                        {
                            // #1183: same clamp as the idle branch's delayTask above -- an early
                            // wakeup here is harmless, `completed == deferralWakeup` below already
                            // just `continue`s to re-check readiness against the real deadline.
                            var chunkedWakeDelay = wakeDelay > MaxParkWaitChunk ? MaxParkWaitChunk : wakeDelay;
                            deferralWakeup = Task.Delay(chunkedWakeDelay, timeProvider, ioCancellationToken);
                            waitCandidates.Add(deferralWakeup);
                        }
                    }
                }

                var completed = await Task.WhenAny(waitCandidates).ConfigureAwait(false);
                // Same shared-token race as the idle branch's wait (see the comment there): the
                // wakeup must not swallow a host stop it lost the WhenAny race to. Unlike there,
                // losing this race is self-recovering (the watcher precedes the wakeup in the
                // candidate list, and a cancelled-token append refuses before any post-stop
                // dispatch could land) — the guard buys symmetry and one round of latency, not a
                // hang fix.
                if (completed == deferralWakeup && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                if (completed == hostStopWatcher || (completed == deferralWakeup && cancellationToken.IsCancellationRequested))
                {
                    hostStopRequested = true;

                    // From here on every read/write this loop performs must survive the now-cancelled
                    // ambient token so the pump can still converge (see ioCancellationToken's own
                    // remarks above).
                    ioCancellationToken = CancellationToken.None;

                    // Intent-first, for every execution still in flight, before any of them is signalled —
                    // RequestStopAsync itself enforces that ordering.
                    await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                if (completed == waitParkedCancelWake)
                {
                    // F8 (#1605 review): reset-before-drain is load-bearing here too — see the same
                    // ordering's full explanation at this loop's idle-deferral branch above
                    // (`parkedCancelWake`'s own ResetParkedCancelWake call).
                    inFlightExecutions.ResetParkedCancelWake(waitParkedCancelWake);
                    await SettleParkedCancelIntentsAsync(state, inFlightExecutions, eventLogWriter, ioCancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                inFlight.Remove(completed);
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!hostStopRequested && cancellationToken.IsCancellationRequested)
            {
                // A host stop is a request to converge, not to crash — but the two parked waits
                // above are the only places that used to translate the ambient token into the
                // graceful path (hostStopRequested → RequestStopAsync → converge on a no-cancel
                // token). A cancel landing anywhere else — the loop-top log read, a dispatch
                // preparation — surfaced as OperationCanceledException and killed the pump with
                // in-flight processes never told to stop (#718). Route every ambient-token
                // cancellation into the same graceful path instead; `inFlight` and the registry
                // live outside the loop, so the next round still owns and awaits everything
                // already running.
                hostStopRequested = true;
                ioCancellationToken = CancellationToken.None;
                await inFlightExecutions.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PreparedExecution> PrepareExecutionAsync(
        WorkflowId workflowId,
        WorkflowStepDefinition step,
        WorkflowDefinitionSnapshot snapshot,
        FlowState state,
        WorkerBinding binding,
        string artifactsRootPath,
        IEventLogWriter eventLogWriter,
        CancellationToken cancellationToken)
    {
        var stateByStepId = state.Steps.ToDictionary(s => s.StepId);

        var executionId = new ExecutionId(Guid.NewGuid().ToString("n"));
        var inputPaths = ArtifactManager.ResolveInputPaths(step, snapshot, state, artifactsRootPath);
        var outputDirectory = ArtifactManager.AllocateOutputDirectory(artifactsRootPath, executionId);

        // A RetryWithRevision/Supersede consequence still owed to this step carries its
        // supplement into this dispatch — a projected fact, so this holds whether this round is the
        // decision's immediate consequence or a replay resuming after a crash between the two.
        var supplementaryInputPath = stateByStepId[step.StepId].PendingSupplementaryExecutionId is { } supplementaryExecutionId
            ? ArtifactManager.ResolveSupplementaryInputPath(artifactsRootPath, supplementaryExecutionId)
            : null;
        var environment = ArtifactManager.BuildEnvironment(inputPaths, outputDirectory, artifactsRootPath, supplementaryInputPath);

        var upstreamExecutionIds = new Dictionary<StepId, ExecutionId>();
        foreach (var dependencyStepId in step.DependsOn)
        {
            // The Dependency Resolver's condition 1 already guarantees every DependsOn entry has a
            // successful execution — LatestExecutionId is never null here.
            upstreamExecutionIds[dependencyStepId] = stateByStepId[dependencyStepId].LatestExecutionId!.Value;
        }

        var request = new ExecutionRequest(
            executionId,
            workflowId,
            step.StepId,
            step.Worker,
            inputPaths,
            step.Outputs,
            binding is WorkerBinding.Process processBinding ? processBinding.Timeout : null,
            environment,
            upstreamExecutionIds,
            GrantAuditMode: binding.GrantAuditMode,
            Adapter: (binding as WorkerBinding.Process)?.Adapter,
            Model: (binding as WorkerBinding.Process)?.Model);


        // The write-sequence rule: intent recorded and fsync'd before Core is ever asked to run.
        await eventLogWriter.AppendAsync(CreateExecutionRequestAccepted(request), cancellationToken)
            .ConfigureAwait(false);

        return new PreparedExecution(request, outputDirectory);
    }

    private static FlowEvent.ExecutionRequestAccepted CreateExecutionRequestAccepted(ExecutionRequest request)
    {
        var pid = Environment.ProcessId;
        var startTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();
        return new FlowEvent.ExecutionRequestAccepted(request, pid, startTime);
    }

    private static async Task DispatchAndRecordOutcomeAsync(
        PreparedExecution prepared,
        WorkerBinding.Process binding,
        IEventLogWriter eventLogWriter,
        ICoreDispatcher dispatcher,
        InFlightExecutionRegistry inFlightExecutions,
        CancellationToken dispatchCancellationToken,
        TimeProvider? timeProvider = null)
    {
        try
        {
            // Rests on ICoreDispatcher's contract that cancellation via dispatchCancellationToken
            // comes back as a normal CoreDispatchResult (CoreExitReason.CancelRequested), never as
            // OperationCanceledException — CoreDispatcher converts BatonCancelException two layers
            // down. If an implementation (or a test double) ever let OCE escape here, the outcome
            // append below would be skipped and, with the ambient token also cancelled, the pump's
            // round-level catch would absorb the evidence. There is deliberately no local catch:
            // that would convert a contract violation into a fabricated outcome.
            var dispatchResult = await dispatcher.DispatchAsync(prepared.Request, binding.Target, dispatchCancellationToken)
                .ConfigureAwait(false);
            // The request's mode was set from this binding at preparation; null can only mean a
            // request shape that predates the mode, and those were never promised an audit.
            var grantAuditMode = prepared.Request.GrantAuditMode ?? GrantAuditMode.Enforced;
            var worktreePath = binding.Target.WorkingDirectory;
            // #1586 S1: the same recorded-adapter preference ExecutionUsageProjector's own #1567
            // comment explains — prepared.Request.Adapter, not the binding, so this site and the
            // crash-recovery site below both read the same source (identical value here, since
            // prepared.Request.Adapter is frozen from this binding at preparation).
            var usageParser = prepared.Request.Adapter is { } liveAdapter
                ? StandardWorkerUsageParsers.Default.GetValueOrDefault(liveAdapter)
                : null;
            var classification = OutcomeClassifier.Classify(
                dispatchResult, binding.Contract, prepared.OutputDirectory, binding.FailureClassifier, timeProvider,
                grantAuditMode, worktreePath, binding.ResponseParser, usageParser);

            // Never gated on dispatchCancellationToken: that token having fired is exactly what
            // produced this outcome (Cancelled) in the first place, so recording it must not itself
            // be cancellable by the same signal — the outcome append always completes once
            // dispatch has returned.
            await eventLogWriter.AppendAsync(ToOutcomeEvent(prepared.Request.ExecutionId, classification), CancellationToken.None)
                .ConfigureAwait(false);
            await AppendZeroOutputsTripwireIfAnyAsync(eventLogWriter, prepared.Request.ExecutionId, classification, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (CommandLineTooLongException ex)
        {
            // A deterministic refusal to spawn: re-submission re-refuses identically, so Permanent
            // (#747; the retry gate in RetryEngine.MayRetry is what makes that stick). Recorded so
            // flow.jsonl is not left stuck at ExecutionRequestAccepted forever.
            await eventLogWriter.AppendAsync(
                new FlowEvent.ExecutionFailed(
                    prepared.Request.ExecutionId,
                    FailureClassification.Permanent,
                    ex.Message),
                CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Baton.Core.BatonException ex)
        {
            // The rest of the refusal family (#747's review): the OS declining the spawn — missing
            // binary, bad working directory, or some other spawn failure the typed guard above cannot
            // pre-empt (#612 measures and refuses an over-long command line up-front; Windows-only,
            // #1405, so its ceiling always resolves) — surfaces as the binding's BatonException, not the
            // typed guard above. Retryable, not Permanent: these are not proven deterministic, and a
            // genuinely stuck cause terminates through RetryPolicy exhaustion instead. Same reason as
            // above for recording at all; OperationCanceledException stays deliberately uncaught either
            // way.
            await eventLogWriter.AppendAsync(
                new FlowEvent.ExecutionFailed(
                    prepared.Request.ExecutionId,
                    FailureClassification.Retryable,
                    $"Spawn refused: {ex.Message}"),
                CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            inFlightExecutions.Unregister(prepared.Request.ExecutionId);
        }
    }

    /// <summary>
    /// Maps a classified outcome to the terminal <see cref="FlowEvent"/> it owes, shared by
    /// a fresh dispatch's own completion (<see cref="DispatchAndRecordOutcomeAsync"/>) and M10 Phase
    /// 3's from-the-log classification of a recorded exit — the same mapping either way.
    /// </summary>
    private static FlowEvent ToOutcomeEvent(ExecutionId executionId, OutcomeClassification classification) =>
        classification.Verdict switch
        {
            OutcomeVerdict.Succeeded => new FlowEvent.ExecutionSucceeded(executionId),
            OutcomeVerdict.Failed => new FlowEvent.ExecutionFailed(
                executionId, classification.FailureClassification, classification.Reason, classification.RetryNotBefore,
                classification.CapturedResponseFile, classification.UnsatisfiedOutputNames),
            OutcomeVerdict.Cancelled => new FlowEvent.ExecutionCancelled(executionId),
            _ => throw new ArgumentOutOfRangeException(nameof(classification), classification.Verdict, "Unknown OutcomeVerdict."),
        };

    /// <summary>
    /// #1586 S1 (the #1594 ruling's tripwire): a no-op unless <paramref name="classification"/> carries
    /// <see cref="OutcomeClassification.SubstantialWorkNoOutputsEvidence"/> — appends
    /// <see cref="FlowEvent.ZeroOutputsDespiteSubstantialWork"/> right alongside the outcome event
    /// <see cref="ToOutcomeEvent"/> mapped, from every caller that classifies an outcome — both the
    /// just-completed live dispatch and the branch that settles a dead pump's recorded exit — so the
    /// tripwire fires identically regardless of which one produced the classification.
    /// <c>spec/baton.md</c> §3 names the two call sites; the same "one seam, every caller of it"
    /// placement #1594's own integration constraint required of the capture arm this mirrors.
    /// </summary>
    private static async Task AppendZeroOutputsTripwireIfAnyAsync(
        IEventLogWriter eventLogWriter, ExecutionId executionId, OutcomeClassification classification, CancellationToken cancellationToken)
    {
        if (classification.SubstantialWorkNoOutputsEvidence is not { } evidence)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(
                $"TRIPWIRE (#1594): execution '{executionId.Value}' produced NONE of its declared " +
                $"outputs, yet {evidence} -- this room's classification may not reflect what actually " +
                "happened. Investigate before trusting it.");
        }
        catch (IOException)
        {
            // Same best-effort posture as the #1594 capture line this mirrors (OutcomeClassifier.Classify) —
            // a broken stderr pipe must not itself orphan the execution; the durable event below still
            // records the fact regardless of whether this line reached the console.
        }

        await eventLogWriter.AppendAsync(new FlowEvent.ZeroOutputsDespiteSubstantialWork(executionId, evidence), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// #1563 (S0 of the quota design, #802): resolves every parked-cancel intent the registry's
    /// wake latch just woke this deferral park for, against the CURRENT round's own projection —
    /// never against whatever the poller's thread saw, which can be a round stale by the time this
    /// runs. Intent-first, then settle, the same shape every other terminal append in this loop
    /// takes: <see cref="FlowEvent.CancellationRequested"/> is recorded even though the target
    /// already carries an <see cref="FlowEvent.ExecutionFailed"/> — <see cref="RequestCancellationAsync"/>'s
    /// direct path does the same for an already-terminal target — then
    /// <see cref="FlowEvent.ExecutionCancelled"/> settles it, overwriting the step's terminal status
    /// from <see cref="StepStatus.Failed"/> to <see cref="StepStatus.Cancelled"/> the same way
    /// <see cref="FlowEvent.WorkflowResumed"/>'s Reject decision already overwrites Failed to
    /// Rejected for a paused step (<see cref="Projection.StateProjector"/>'s own
    /// <c>WorkflowResumed</c> case). A target that no longer matches a parked step by the time this
    /// runs (redispatched already, or never was one) is silently dropped here — see the comment at
    /// the drop site below for the narrow race that produces this and why it self-heals. F5 (#1605
    /// review): that drop is NOT surfaced by <see cref="CancelRequestPoller"/>'s bounded 5-tick retry
    /// ceiling — the parked path deliberately bypasses that ceiling (its own <c>isParked</c> early
    /// return) — so a genuinely unreachable target is instead caught by the poller's ordinary
    /// settled-vs-still-running check re-evaluating against fresh state on its own next tick.
    /// </summary>
    private static async Task SettleParkedCancelIntentsAsync(
        FlowState state,
        InFlightExecutionRegistry inFlightExecutions,
        IEventLogWriter eventLogWriter,
        CancellationToken ioCancellationToken)
    {
        var intents = inFlightExecutions.DrainParkedCancelIntents();
        foreach (var executionId in intents)
        {
            if (!IsParkedRetryTarget(state, executionId))
            {
                // This guard serves two purposes, not one: it is the fail-closed rejection of a
                // target that was never a real park to begin with (a mismatched/stale execution id —
                // Marking_an_intent_for_a_mismatched_execution_id... pins exactly this), AND it is
                // where the F8 (#1605 review) delayTask-wins interleaving below lands. Neither
                // resolves the same way, but both fall through the same drop.
                //
                // The interleaving: a mark lands in the exact instant this round's own deferral timer
                // (not the parked-cancel wake) fires and moves the step off this parked shape — a
                // redispatch minting a new ExecutionId — dropping the stale id silently here instead
                // of settling it. Known, not fixed here. It is NOT silently lost end to end: the
                // poller never consumed the request file for a parked mark (its own isParked branch
                // just re-marks, never consumes), and the request's Target is always the ORIGINAL
                // literal execution id in this scenario ('latest' can never resolve to a parked step
                // in the first place — RunningExecutionResolver only sees Running steps, F2's own
                // point). So the poller's next tick re-checks that same stale id, finds it no longer
                // matches any step's LatestExecutionId, and reports the ordinary "too late (it already
                // settled)" verdict — an honest, if imprecise, outcome (the intent was not wrong, just
                // reported as arriving after the fact), not a request that vanishes with no trace.
                continue;
            }

            await eventLogWriter.AppendAsync(new FlowEvent.CancellationRequested(executionId), ioCancellationToken)
                .ConfigureAwait(false);
            await eventLogWriter.AppendAsync(new FlowEvent.ExecutionCancelled(executionId), ioCancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The fail-closed check behind <see cref="SettleParkedCancelIntentsAsync"/>: true only for a
    /// step whose LATEST execution is <paramref name="targetExecutionId"/>, currently
    /// <see cref="StepStatus.Failed"/>, and sitting on a scheduled <see cref="StepState.RetryNotBefore"/>
    /// — the idle-deferral park's exact shape. A step that already redispatched (a new
    /// <see cref="ExecutionId"/> is now latest) or was never parked at all resolves false.
    /// </summary>
    private static bool IsParkedRetryTarget(FlowState state, ExecutionId targetExecutionId) =>
        state.Steps.Any(s =>
            s.LatestExecutionId == targetExecutionId
            && s.Status == StepStatus.Failed
            && s.RetryNotBefore is not null);

    private sealed record PreparedExecution(ExecutionRequest Request, string OutputDirectory);

    private sealed record RetryObligation(
        StepId StepId,
        ExecutionId ForExecutionId,
        DateTimeOffset RetryNotBefore,
        int RetryDelayMs);

    // #1183: a vendor never legitimately reports a quota reset this far out (the instant comes from
    // PARSING vendor prose/fields, and a parse bug or garbage value must not become a pump crash) --
    // an ExhaustedUntil reset instant beyond this horizon is treated as bogus and capped rather than
    // trusted wholesale. Chosen comfortably under both the ~24.8-day int-ms cast range this obligation's
    // own RetryDelayMs is computed into, and the ~49.7-day range Task.Delay's TimeSpan overload accepts.
    private static readonly TimeSpan MaxExhaustionParkHorizon = TimeSpan.FromDays(14);

    // #1183: an ExhaustedUntil reset instant already at or in the past collapsed to a zero-delay
    // retry -- with ConsecutiveFailureCount frozen at 0 for quota hits, a vendor that keeps reporting
    // the same stale instant machine-guns the pump in a tight spend-nothing-but-CPU loop. A floor
    // makes the retry rate bounded instead, whether or not the instant is genuinely repeating.
    private static readonly TimeSpan PastResetInstantRetryFloor = TimeSpan.FromSeconds(1);

    private static List<RetryObligation> GetRetryObligations(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        TimeProvider timeProvider,
        Func<double> jitterSource,
        bool settleOnVendorExhaustion = false)
    {
        var stepDefinitionByStepId = snapshot.Steps.ToDictionary(s => s.StepId);
        var obligations = new List<RetryObligation>();

        foreach (var stepState in state.Steps)
        {
            if (stepState.Status != StepStatus.Failed || stepState.LatestExecutionId is null)
            {
                continue;
            }

            var stepDef = stepDefinitionByStepId[stepState.StepId];
            if (!RetryEngine.MayRetry(stepState, stepDef.RetryPolicy))
            {
                continue;
            }

            // A Failed step whose ConsecutiveFailureCount is zero with no live classification is
            // one an operator just reopened via RetryWithRevision — StateProjector resets
            // both for exactly that decision. Backoff exists to pace the machine's own retries; a
            // person's explicit "retry now" is not paced, so no obligation is scheduled for it.
            // An ExhaustedUntil step also sits at zero (quota hits consume no budget, 0026) but is
            // the machine's own wait, not a person's reopen — it must still be paced to the reset.
            if (stepState.ConsecutiveFailureCount == 0
                && stepState.LatestFailureClassification != FailureClassification.ExhaustedUntil)
            {
                continue;
            }

            if (stepState.RetryScheduledForExecutionId == stepState.LatestExecutionId)
            {
                continue;
            }

            // 0026 §5 (#1115 review): an ExhaustedUntil step whose vendor gave NO reset instant
            // gets NO obligation at all — "nothing wakes up, and the product says so". Falling
            // through to ordinary backoff here fabricated a ~1s-away instant on every cycle
            // (ConsecutiveFailureCount is frozen at 0 for quota hits, so the delay never grew),
            // auto-retrying a claude dispatch against a known-dead quota forever while the
            // status surfaced the fabricated time as a vendor reset. A person resumes this step
            // (RetryWithRevision), or a later failure carries a real instant.
            // 0026 §4 attended/unattended discriminator (#1184): when settleOnVendorExhaustion is true
            // (an attended interactive session turn), an ExhaustedUntil step ALSO gets NO retry obligation
            // even if a reset instant is known — the turn settles immediately and the operator re-sends after reset.
            if (stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil &&
                (settleOnVendorExhaustion || stepState.LatestExecutionFailedRetryNotBefore is null))
            {
                continue;
            }

            DateTimeOffset notBefore;
            int delayMs;

            if (stepState.LatestFailureClassification == FailureClassification.ExhaustedUntil &&
                stepState.LatestExecutionFailedRetryNotBefore is { } resetMoment)
            {
                var utcNow = timeProvider.GetUtcNow();

                // #1183: cap an absurd (parse-bug/garbage) far-future instant to the sane horizon
                // rather than trust it wholesale -- keeps RetryNotBefore and RetryDelayMs mutually
                // consistent for DependencyResolver's #712 backwards-clock-jump clamp below, and keeps
                // every downstream wait on this obligation's RetryNotBefore inside a range Task.Delay
                // actually accepts.
                var cappedResetMoment = resetMoment - utcNow > MaxExhaustionParkHorizon
                    ? utcNow + MaxExhaustionParkHorizon
                    : resetMoment;
                var rawDelay = cappedResetMoment - utcNow;

                // #1183: an instant less than PastResetInstantRetryFloor away -- already at or before
                // now (including one repeating unchanged), or legitimately future but imminent -- is
                // paced up to the floor instead of collapsing to a near-zero-delay retry. This branch
                // does not and need not distinguish "already past" from "about to hit": both would
                // otherwise machine-gun the pump the same way.
                if (rawDelay < PastResetInstantRetryFloor)
                {
                    notBefore = utcNow + PastResetInstantRetryFloor;
                    delayMs = (int)PastResetInstantRetryFloor.TotalMilliseconds;
                }
                else
                {
                    notBefore = cappedResetMoment;
                    // #1183: Ceiling, not Round -- DependencyResolver's #712 clamp needs
                    // delayMs >= the real notBefore-utcNow gap so a sub-millisecond rounddown can never
                    // make `remaining > maxDelay` misfire and release this step before cappedResetMoment.
                    delayMs = (int)Math.Ceiling(rawDelay.TotalMilliseconds);
                }
            }
            else
            {
                double jitterSample = jitterSource();
                int attempt = stepState.ConsecutiveFailureCount;
                TimeSpan delay = stepDef.RetryPolicy.Backoff.DelayFor(attempt, jitterSample);
                delayMs = (int)Math.Round(delay.TotalMilliseconds);
                notBefore = timeProvider.GetUtcNow().AddMilliseconds(delayMs);
            }

            obligations.Add(new RetryObligation(
                stepState.StepId,
                stepState.LatestExecutionId.Value,
                notBefore,
                delayMs));
        }

        return obligations;
    }

    /// <summary>
    /// The contract a crash-recovery classification runs against (#724): the live binding's when it
    /// resolves, else one reconstructed from the recorded <see cref="ExecutionRequest"/> — the
    /// execution already ran, so what it was asked to produce is a recorded fact, and a bindings
    /// file that changed or broke since must not make the recorded outcome unclassifiable (the
    /// #662 lesson, on the recovery path). The reconstruction carries output NAMES only: any
    /// <c>OutputCondition</c> the original contract declared is unknowable from the request today,
    /// so a conditioned output classifies on existence alone in this fallback. Recording the full
    /// contract on the request is #672's design to make.
    /// </summary>
    private static WorkerContract GetContractForClassification(
        ExecutionRequest request,
        IReadOnlyDictionary<string, WorkerBinding> workerBindings)
    {
        try
        {
            if (workerBindings.TryGetValue(request.Worker, out var binding) && binding is WorkerBinding.Process processBinding)
            {
                return processBinding.Contract;
            }
        }
        catch (BatonFlowException)
        {
            // Resolution refused (missing adapter, unsatisfiable grant) — exactly the case the
            // recorded request exists to cover. Anything else still propagates.
        }

        return new WorkerContract(
            request.Worker,
            RequiredInputs: [],
            ProducedOutputs: [.. request.Outputs.Select(o => new ProducedOutput(o))],
            OptionalMetadata: []);
    }
}
