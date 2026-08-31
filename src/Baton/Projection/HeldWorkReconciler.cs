using Baton.Domain;
using Baton.Mutation;
using Baton.Status;

namespace Baton.Projection;

/// <summary>
/// Reconciles a <see cref="HeldWorkState"/> at READ time against an existence probe appropriate to
/// its <see cref="HeldWorkState.Shape"/> (#774 pattern, made shape-aware by #832). Does not mutate
/// or alter the pure <see cref="RoomState"/> projection.
/// </summary>
public static class HeldWorkReconciler
{
    /// <summary>
    /// Renders the reconciled status of <paramref name="state"/>.
    /// <para>
    /// <see cref="MemoryProposalEscalation.MemoryProposalShape"/> is the one shape whose
    /// <see cref="HeldWorkState.Ref"/> is not a workflow directory (#801: it is a capture FILE), so it
    /// gets its own existence probe and its own honest lines -- "awaiting operator decision" while
    /// the file is there, "proposal file missing" when it is not, never the workflow's "never started".
    /// </para>
    /// <para>
    /// Every other <c>Shape</c> -- including one not yet invented -- falls through to the original
    /// workflow probe, producing <c>dispatch recorded; workflow never started (&lt;probe why&gt;)</c> when a
    /// dispatched workflow has no journal. This is a deliberate default, not an accidental one: nothing
    /// in this codebase names a "workflow" shape constant today (dispatchers pass arbitrary strings), so
    /// the workflow probe already stood in for "shape unrecognised" before #832 ever distinguished a
    /// shape at all. A future shape that needs different treatment must get its own case above this
    /// one -- it must not be assumed to fall here silently.
    /// </para>
    /// </summary>
    public static string RenderStatus(
        HeldWorkState state,
        Func<string, bool>? workflowJournalExistsProbe = null,
        Func<string, bool>? memoryProposalFileExistsProbe = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Shape == MemoryProposalEscalation.MemoryProposalShape)
        {
            return RenderMemoryProposalStatus(state, memoryProposalFileExistsProbe ?? File.Exists);
        }

        return RenderWorkflowStatus(state, workflowJournalExistsProbe ?? DefaultWorkflowJournalExistsProbe);
    }

    private static bool DefaultWorkflowJournalExistsProbe(string workflowDirectoryPath)
        => File.Exists(Path.Combine(workflowDirectoryPath, BatonPaths.FlowLogFileName));

    private static string RenderWorkflowStatus(HeldWorkState state, Func<string, bool> workflowJournalExistsProbe)
    {
        if (state.Status != HeldWorkStatus.Resolved && !workflowJournalExistsProbe(state.Ref.AsWorkflowDirectoryPath()))
        {
            return $"dispatch recorded; workflow never started (no ledger found at {state.Ref.AsWorkflowDirectoryPath()})";
        }

        return RenderStatusLine(state);
    }

    private static string RenderMemoryProposalStatus(HeldWorkState state, Func<string, bool> memoryProposalFileExistsProbe)
    {
        if (state.Status != HeldWorkStatus.Resolved && !memoryProposalFileExistsProbe(state.Ref.Value))
        {
            return $"proposal file missing (memory proposal; no capture file found at {state.Ref.Value})";
        }

        return state.Status switch
        {
            HeldWorkStatus.Dispatched => "awaiting operator decision (memory proposal)",
            _ => RenderStatusLine(state),
        };
    }

    private static string RenderStatusLine(HeldWorkState state) => state.Status switch
    {
        HeldWorkStatus.Dispatched => "dispatched",
        HeldWorkStatus.Escalated => $"escalated to {state.EscalatedTo}",
        HeldWorkStatus.Resolved => $"resolved ({state.Citation?.EventType} {state.Citation?.Subject})",
        _ => state.Status.ToString(),
    };
}
