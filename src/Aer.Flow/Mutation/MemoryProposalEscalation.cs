using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;

namespace Aer.Flow.Mutation;

/// <summary>
/// Turns a captured <c>Aer.Mcp.Host.MemoryProposalTool</c> call into room-journal held work (#801),
/// so proposals reach the operator through the same escalation surface every other held item uses
/// (<see cref="RoomMutationInterface"/>) rather than a new one -- for the design constraint, see
/// <see cref="Aer.Mcp.Host.MemoryProposalTool"/> (#672 item 3).
/// <!-- record-once-ok: #801 src/Aer.Mcp.Host/MemoryProposalTool.cs -->
/// <para>
/// Deliberately narrow: this class only turns a capture file into a dispatched <see cref="HeldWorkRef"/>.
/// It never reads <c>memory/</c>, never applies a proposal, and never escalates or resolves one past
/// <see cref="HeldWorkStatus.Dispatched"/> -- deciding and applying a proposal is #672's other half,
/// explicitly out of #801's scope.
/// </para>
/// </summary>
public static class MemoryProposalEscalation
{
    /// <summary>
    /// The room's own placeholder budget for a memory-proposal item (#801): unlike a dispatched
    /// workflow, a proposal has no natural timeout of its own -- it waits on an operator
    /// decision, not a process. <see cref="TimeSpan.Zero"/> carries no live meaning today (nothing
    /// in <see cref="RoomProjector"/>/<see cref="HeldWorkReconciler"/> currently branches on
    /// <c>Budget</c>); recorded here rather than a made-up nonzero figure so a future consumer that
    /// does start reading it does not inherit an invented number.
    /// </summary>
    public static readonly TimeSpan NoBudget = TimeSpan.Zero;

    public const string MemoryProposalShape = "memory-proposal";

    /// <summary>
    /// The decider identity every daemon-driven sweep dispatches held work under (#833) --
    /// memory-proposal decisions always route to a person (decision 0044's point 3: this tool never
    /// applies its own proposal), so a fixed, honest identity rather than a made-up per-room one.
    /// </summary>
    public const string DefaultDeciderIdentity = "operator";

    /// <summary>
    /// The capture subdirectory name relative to one execution's own <c>AER_OUTPUT_DIR</c> (#833) --
    /// mirrors <see cref="Aer.Mcp.Host.MemoryProposalTool.CaptureDirectoryName"/>'s own constant of
    /// the identical value; <c>Aer.Flow</c> cannot reference <c>Aer.Mcp.Host</c> (the dependency runs
    /// the other way -- adapters and the tool host sit above the engine), so the literal is
    /// duplicated across the boundary rather than shared, the same way
    /// <c>HeldWorkReconciler.DefaultWorkflowJournalExistsProbe</c> already hardcodes <c>flow.jsonl</c>'s
    /// name rather than importing it. <c>MemoryProposalCaptureDirectoryNameTests</c> (both projects)
    /// pins the two literals to the same value.
    /// </summary>
    public const string CaptureDirectoryName = "memory-proposals";

    /// <summary>
    /// Sweeps every execution directory under <paramref name="roomDirectoryPath"/>'s own
    /// <c>artifacts/</c> for a <see cref="CaptureDirectoryName"/> subdirectory and escalates each
    /// one's new captures into this same room (#833). Attribution is structural, never a claim: the
    /// room's storage form IS the room directory (spec/baton.md §2), so every <c>execution_*</c> directory
    /// found under <c>{roomDirectoryPath}/artifacts</c> was, by construction, dispatched by this room
    /// and no other -- there is nothing here for a worker to lie about. Retires the #801 static,
    /// shared capture directory this replaces: that directory served every room at once with no way
    /// to tell them apart, which is the defect #833 exists to fix.
    /// </summary>
    public static async Task<RoomState> EscalateNewProposalsForRoomAsync(
        string roomDirectoryPath,
        string deciderIdentity,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(deciderIdentity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var state = RoomProjector.Project(await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false));

        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        if (!Directory.Exists(artifactsRoot))
        {
            return state;
        }

        foreach (var executionDirectory in Directory.GetDirectories(artifactsRoot, "execution_*")
            .OrderBy(d => d, StringComparer.Ordinal))
        {
            var captureDirectoryPath = Path.Combine(executionDirectory, CaptureDirectoryName);
            state = await EscalateNewProposalsAsync(
                captureDirectoryPath, roomDirectoryPath, deciderIdentity, reader, writer, cancellationToken)
                .ConfigureAwait(false);
        }

        return state;
    }

    /// <summary>
    /// Dispatches every capture file under <paramref name="captureDirectoryPath"/> that is not
    /// already held work in this room, in filename order. A capture file's own path becomes its
    /// <see cref="HeldWorkRef"/> -- there is no workflow directory for a memory proposal, so this reuses
    /// the ref's role as "the thing to point an operator at" rather than "a workflow with a flow.jsonl".
    /// Idempotent: re-running against the same directory re-dispatches nothing already recorded.
    /// The idempotency key is the capture file's full path, so <paramref name="captureDirectoryPath"/>
    /// must be rooted -- a relative path would resolve against the caller's current directory and
    /// mint a second ref for the same physical file under a different cwd (#801 review).
    /// Scope limit: dispatch keys on the file's presence alone; its JSON content is not validated
    /// here (a half-written file is never visible thanks to
    /// <see cref="Aer.Mcp.Host.MemoryProposalTool"/>'s atomic write -- see its own remarks).
    /// </summary>
    public static async Task<RoomState> EscalateNewProposalsAsync(
        string captureDirectoryPath,
        string roomDirectoryPath,
        string deciderIdentity,
        IRoomEventLogReader reader,
        IRoomEventLogWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureDirectoryPath);
        if (!Path.IsPathRooted(captureDirectoryPath))
        {
            throw new ArgumentException(
                $"captureDirectoryPath must be rooted; got '{captureDirectoryPath}'. The full path is the " +
                "held-work idempotency key, and a relative path keys on the caller's current directory.",
                nameof(captureDirectoryPath));
        }

        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(deciderIdentity);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var state = RoomProjector.Project(await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false));

        if (!Directory.Exists(captureDirectoryPath))
        {
            return state;
        }

        foreach (var file in Directory.GetFiles(captureDirectoryPath, "proposal-*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var @ref = new HeldWorkRef(Path.GetFullPath(file));
            if (state.HeldWork.ContainsKey(@ref))
            {
                continue;
            }

            try
            {
                state = await RoomMutationInterface.DispatchHeldWorkAsync(
                    roomDirectoryPath, @ref, MemoryProposalShape, NoBudget, deciderIdentity, reader, writer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidRoomMutationException)
            {
                // Lost a dispatch race (#851): the idempotency check above ran against a projection
                // read before the room lock, and a concurrent sweeper recorded this ref in the
                // window. "Already dispatched" is this loop's goal state, not a failure -- refresh
                // the projection and keep sweeping so one collision never aborts the pass.
                state = RoomProjector.Project(await reader.ReadAllRoomEventsAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        return state;
    }
}
