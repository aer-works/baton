using Baton.Vendors;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A runway decision for a dispatch test that is not about the runway (#1848). Every test binding
/// keyed as <c>"claude"</c> or <c>"agy"</c> runs against an <see cref="IsolatedBatonHome"/> with no
/// harvested usage snapshot in it, and no snapshot is a Hold — correctly, since a machine whose
/// daemon has never harvested has no evidence of headroom. Tests about continuation, worktrees, or
/// grants say so explicitly here rather than depending on the fleet's runway state; the gate's own
/// arms live in <c>RunwayHoldDispatchTests</c> and <c>Baton.Vendors.Tests.RunwayGateTests</c>.
/// </summary>
internal static class RunwayTestGate
{
    /// <summary>Admits every vendor, with no counters — the "this test is not about the runway" seam.</summary>
    public static RunwayDecision Admit(string vendor) =>
        new(vendor, RunwayDisposition.Admit, Reason: null, Counters: []);
}
