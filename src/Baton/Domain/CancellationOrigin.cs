using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// #1762: who authored a <see cref="FlowEvent.CancellationRequested"/> line — the durable
/// distinction a per-call, in-memory flag could not carry across a process boundary (spec/baton.md
/// §2). New (no pre-#1762 journal line carries this field at all), so it carries
/// <see cref="JsonStringEnumConverter"/> directly — same pattern as <see cref="ArrestReason"/> —
/// rather than needing an ordinal-stability pin: there is no legacy ordinal to stay compatible with.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CancellationOrigin
{
    /// <summary>
    /// An operator named this execution: <c>CancelCommand</c>'s direct path
    /// (<see cref="Mutation.MutationInterface.RequestCancellationAsync"/>), its live-pump
    /// fall-through poller's marked arrest intent settling
    /// (<c>MutationInterface.SettleArrestIntentsAsync</c>, #1556 — generalized from #1563's
    /// narrower <c>SettleParkedCancelIntentsAsync</c>), or
    /// <see cref="Mutation.InFlightExecutionRegistry.RequestCancellationAsync"/> delivering to a
    /// still-registered in-process execution.
    /// </summary>
    Operator,

    /// <summary>
    /// <see cref="Mutation.InFlightExecutionRegistry.RequestStopAsync"/>'s wind-down mint, journalled
    /// for every still-registered execution as the pump's host token fires — not an operator naming
    /// that step.
    /// </summary>
    HostStop,
}
