using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Templates;

namespace Baton.Store;

/// <summary>
/// The read-only, at-READ-time probe of a held-work workflow directory feeding
/// <see cref="RoomWakeDerivation"/> (#799). Mirrors <c>Baton.Cli.StatusCommand</c>'s own
/// snapshot.json + flow.jsonl read exactly — same files, same
/// <see cref="StateProjector.Project(System.Collections.Generic.IReadOnlyList{FlowEvent},WorkflowDefinitionSnapshot,ProjectionCheckpoint)"/>
/// terminal authority (post-#811 <c>DeriveWorkflowStatus</c>) — never a second reading of what
/// "terminal" means. Uses projection checkpoints (#903 Scope 1) when present for bounded O(tail) replay.
/// Takes no <see cref="Baton.Concurrency.ConcurrencyGuard"/>: this can run
/// concurrently with the workflow's own live pump, the same read-only discipline <c>baton status</c>
/// already established.
/// </summary>
public static class WorkflowTerminalProbe
{
    public static async Task<WorkflowProbeResult> ProbeAsync(
        string workflowDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowDirectoryPath);

        var logPath = Path.Combine(workflowDirectoryPath, BatonPaths.FlowLogFileName);
        if (!File.Exists(logPath))
        {
            return new WorkflowProbeResult(JournalExists: false, IsTerminal: false);
        }

        var snapshotPath = Path.Combine(workflowDirectoryPath, BatonPaths.SnapshotFileName);
        if (!File.Exists(snapshotPath))
        {
            return new WorkflowProbeResult(JournalExists: true, IsTerminal: false);
        }

        var snapshot = await SnapshotBinder.LoadFromFileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = ProjectionCheckpointStore.Load(workflowDirectoryPath);
        var state = StateProjector.Project(events, snapshot, checkpoint);

        return new WorkflowProbeResult(JournalExists: true, IsTerminal: state.Status == WorkflowStatus.Terminal);
    }
}
