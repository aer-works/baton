using Baton.Flow.Domain;

namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton decide</c> (M12 Phase 3), the external-decision surface exposed
/// on the CLI. Mirrors <see cref="CancelOptions"/>'s shape (mutation commands do not bind fresh
/// snapshots).
/// </summary>
/// <param name="RoomDirectoryPath">An already-started room's durable state directory.</param>
/// <param name="ExecutionId">
/// The execution this decision resolves — the reference the pause-aware output
/// (<see cref="FlowStateReporter"/>) reports so a terminal user knows what to pass here. Ordinarily
/// the currently paused latest attempt; for <see cref="Domain.DecisionType.RetryWithRevision"/>
/// only, also a Failed latest attempt with a scheduled retry still pending (#815) — a step #594's
/// classification quota-parked without ever pausing it.
/// </param>
/// <param name="DecisionType">One of the closed set: <c>resume</c>, <c>reject</c>, <c>retry-with-revision</c>, <c>supersede</c>.</param>
/// <param name="TargetStepId">Required for, and only valid with, <see cref="Domain.DecisionType.Supersede"/>.</param>
/// <param name="SupplementaryExecutionId">
/// Required for <see cref="Domain.DecisionType.Supersede"/>; optional for
/// <see cref="Domain.DecisionType.RetryWithRevision"/>. Names an already-succeeded
/// supplementary execution — see <c>baton supply</c>.
/// </param>
/// <param name="BindingsFilePath">The worker-binding config file (M11 Phase 1's sidecar shape).</param>
/// <param name="WorkflowId">Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given, same as <c>baton run</c>.</param>
/// <param name="SettleOnVendorExhaustion">
/// The same in-process-only attended flag <see cref="RunOptions"/> carries, for the decide half of a
/// chat turn (#1184).
/// </param>
public sealed record DecideOptions(
    string RoomDirectoryPath,
    string ExecutionId,
    DecisionType DecisionType,
    StepId? TargetStepId,
    string? SupplementaryExecutionId,
    string BindingsFilePath,
    string? WorkflowId = null,
    bool SettleOnVendorExhaustion = false);
