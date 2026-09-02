namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton cancel</c> (M12 Phase 2), the on-demand cancellation surface exposed
/// on the CLI.
/// </summary>
/// <param name="RoomDirectoryPath">
/// An already-started room's durable state directory — <c>baton cancel</c> never binds a fresh
/// snapshot the way <c>baton run</c> does ("mutation commands never bind fresh" rule).
/// </param>
/// <param name="ExecutionId">
/// The target execution's <c>ExecutionId</c> to request cancellation for. <c>null</c> (#1495) means
/// "the target lane" — <see cref="CancelCommand"/> resolves it from the room's own projected state:
/// exactly one candidate's latest execution — a <see cref="Baton.Domain.StepStatus.Running"/> step, or
/// (#1607) a quota-parked one — or a refusal naming every candidate when there are zero or more than
/// one (fail closed, no guessing).
/// </param>
/// <param name="BindingsFilePath">
/// The worker-binding config file (M11 Phase 1's sidecar shape). Optional at the CLI layer (#1607):
/// <see cref="CancelOptionsParser"/> defaults an omitted <c>--bindings</c> to the room's own
/// <c>bindings.json</c> before this record is ever constructed, so this field is never null.
/// </param>
/// <param name="WorkflowId">
/// Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given, same as <c>baton run</c>.
/// </param>
public sealed record CancelOptions(
    string RoomDirectoryPath,
    string? ExecutionId,
    string BindingsFilePath,
    string? WorkflowId = null);
