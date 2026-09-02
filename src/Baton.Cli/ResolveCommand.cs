using Baton.Artifacts;
using Baton.Domain;
using Baton.Mutation;
using Baton.Projection;
using Baton.Status;
using Baton.Store;
using Baton.Templates;

namespace Baton.Cli;

/// <summary>
/// <c>baton resolve</c> (#1608): exposes <see cref="MutationInterface.RecordCaptureResolutionAsync"/>
/// on the CLI. Unlike <see cref="DecideCommand"/>/<see cref="CancelCommand"/>, this never loads worker
/// bindings and never pumps — see <see cref="MutationInterface.RecordCaptureResolutionAsync"/>'s own
/// remarks for why an unresolved indeterminate capture is unreachable any other way — so nothing here
/// dispatches, and there is nothing to bind for a room that already settled Indeterminate.
/// </summary>
public static class ResolveCommand
{
    private const string ArtifactsDirectoryName = ArtifactManager.ArtifactsDirectoryName;

    /// <exception cref="SnapshotLoadException">
    /// The room directory has no persisted snapshot yet (never started via <c>baton run</c>), or its
    /// persisted snapshot is malformed.
    /// </exception>
    /// <exception cref="CliArgumentException">
    /// <paramref name="options"/>'s <c>ExecutionId</c> is <c>null</c> (room-level targeting) and the
    /// room's own projected state has zero or more than one step still awaiting resolution — fail
    /// closed rather than guess; the message names every candidate found. Also thrown when an
    /// explicit <c>--execution</c> names a step with no unresolved indeterminate capture.
    /// </exception>
    /// <exception cref="InvalidCaptureResolutionException">
    /// The resolution itself is invalid against the room's current state — see
    /// <see cref="MutationInterface.RecordCaptureResolutionAsync"/>.
    /// </exception>
    /// <exception cref="Baton.Concurrency.WorkflowLockedException">
    /// Another Flow instance already holds this room directory's lock.
    /// </exception>
    public static async Task<CommandResult> ExecuteAsync(
        ResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var snapshotPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.SnapshotFileName);
        var logPath = Path.Combine(options.RoomDirectoryPath, BatonPaths.FlowLogFileName);
        var artifactsRootPath = Path.Combine(options.RoomDirectoryPath, ArtifactsDirectoryName);

        if (!File.Exists(snapshotPath))
        {
            throw new SnapshotLoadException(
                $"Room directory '{options.RoomDirectoryPath}' has no bound snapshot — 'baton resolve' " +
                "targets a room 'baton run' has already started, and never binds one fresh.");
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);

        var targetExecutionId = options.ExecutionId is { } explicitExecutionId
            ? await ResolveExplicitExecutionAsync(
                    reader, snapshot, explicitExecutionId, options.RoomDirectoryPath, options.Accept, cancellationToken)
                .ConfigureAwait(false)
            : await ResolveSingleCandidateAsync(reader, snapshot, options.RoomDirectoryPath, cancellationToken)
                .ConfigureAwait(false);

        await using var writer = new FlowEventLogWriter(logPath);

        var state = await MutationInterface.RecordCaptureResolutionAsync(
                options.RoomDirectoryPath,
                snapshot,
                artifactsRootPath,
                reader,
                writer,
                targetExecutionId,
                options.Accept,
                options.Reason,
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(state, snapshot, RoomDirectoryPath: options.RoomDirectoryPath);
    }

    private static async Task<ExecutionId> ResolveExplicitExecutionAsync(
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        string explicitExecutionId,
        string roomDirectoryPath,
        bool accepted,
        CancellationToken cancellationToken)
    {
        var executionId = new ExecutionId(explicitExecutionId);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);

        var namedStep = state.Steps.FirstOrDefault(step => step.LatestExecutionId == executionId);

        // #1623 merge: the flag alone is NOT the admission test — it has three producers now, and only
        // the captured-response one leaves something for this verb to accept or reject. Mirrors
        // MutationInterface.RecordCaptureResolutionAsync's own guard (see its comment for the failure
        // this closes) so the refusal lands here, with a message that can name the right remedy,
        // rather than deeper in as a bare "no unresolved indeterminate capture".
        var isAwaitingResolution =
            namedStep is { IndeterminateAwaitingResolution: true, LatestCapturedResponseFile: not null };
        var isNonCaptureIndeterminate =
            namedStep is { IndeterminateAwaitingResolution: true, LatestCapturedResponseFile: null };

        // #1608 review finding 5: also admit a step already ACCEPTED for this exact execution, but
        // only when this call is itself an --accept-capture -- MutationInterface's own gate on this
        // (RecordCaptureResolutionAsync) requires the same, so a --reject against an already-accepted
        // execution still refuses here rather than reading as a repair of someone else's accept.
        // MutationInterface.RecordCaptureResolutionAsync treats an admitted repair as a crash-repair
        // request (its own ReconcileAcceptedCaptureAsync), not a fresh resolution.
        var isRepairableAccepted = accepted && namedStep is not null
            && events.OfType<FlowEvent.CaptureResolved>()
                .LastOrDefault(resolved => resolved.ExecutionId == executionId && resolved.StepId == namedStep.StepId)
            is { Accepted: true };

        if (!isAwaitingResolution && !isRepairableAccepted)
        {
            // #1623 merge: stated as its own case rather than folded into the generic refusal below,
            // because the generic one's advice ("confirm 'state' reads Indeterminate") is exactly the
            // check this operator has already passed -- the room DOES read Indeterminate, and this
            // verb still refuses. Sending them back to re-read `state` would be a loop.
            if (isNonCaptureIndeterminate)
            {
                throw new CliArgumentException(
                    $"Execution '{explicitExecutionId}' in room '{roomDirectoryPath}' settled Indeterminate "
                    + "without a captured response — a verify failure or a token-budget arrest, not an "
                    + "unwritten output. There is nothing for 'baton resolve' to accept or reject.",
                    $"read the step's failure reason (`baton status {roomDirectoryPath} --json`) to see "
                    + "which, fix the underlying cause, then re-dispatch — a fresh execution reopens the "
                    + "step. See spec/baton.md §3.");
            }

            // #1608 review finding 7: a resolved-but-Failed step and an unresolved one both read
            // ordinary "Failed" per-step in status --json (WorkflowStatusStepView carries no
            // IndeterminateAwaitingResolution field) -- the room-level `state` reading Indeterminate
            // is the one thing status --json actually distinguishes, so that is what this points at.
            throw new CliArgumentException(
                $"Execution '{explicitExecutionId}' has no unresolved indeterminate capture in room " +
                $"'{roomDirectoryPath}' — 'baton resolve' only targets a step still awaiting conductor resolution " +
                "(or already accepted, to repair a crash-interrupted write). " +
                $"Run 'baton status {roomDirectoryPath} --json' and confirm 'state' reads Indeterminate before naming --execution.");
        }

        return executionId;
    }

    /// <summary>
    /// Room-level target resolution, the same fail-closed shape <c>baton cancel</c>'s
    /// <c>ResolveRunningExecutionAsync</c> already uses for its own omitted-<c>--execution</c> case.
    /// </summary>
    private static async Task<ExecutionId> ResolveSingleCandidateAsync(
        FlowEventLogReader reader,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        CancellationToken cancellationToken)
    {
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);
        var candidates = state.Steps
            .Where(step => step.IndeterminateAwaitingResolution && step.LatestExecutionId is not null)
            .ToList();

        if (candidates.Count == 1)
        {
            return candidates[0].LatestExecutionId!.Value;
        }

        if (candidates.Count == 0)
        {
            throw new CliArgumentException(
                $"No --execution given, and room '{roomDirectoryPath}' has no unresolved indeterminate " +
                "capture to resolve — 'baton resolve' refuses to guess.",
                $"run 'baton status {roomDirectoryPath}' to confirm the room's current state reads Indeterminate.");
        }

        throw new CliArgumentException(
            $"No --execution given, and room '{roomDirectoryPath}' has {candidates.Count} steps " +
            $"awaiting resolution ({string.Join(", ", candidates.Select(step => step.LatestExecutionId!.Value.Value))}) " +
            "— 'baton resolve' refuses to guess which one.",
            "pass --execution explicitly, naming the one to resolve.");
    }
}
