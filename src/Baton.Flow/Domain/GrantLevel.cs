using System.Text.Json.Serialization;

namespace Baton.Flow.Domain;

/// <summary>
/// Preset levels for worker grants. Memory adoption is carved out of EVERY level:
/// memory proposals always escalate to the human per decision 0016/0021 (producer ≠ decider
/// does not cover it).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GrantLevel
{
    /// <summary>L0 Observe — chat/report only.</summary>
    L0Observe,

    /// <summary>
    /// L1 Dispatch — the M26 floor. Originates runs within leash; every decision escalates.
    /// Memory adoption is carved out (always escalates to human).
    /// </summary>
    L1Dispatch,

    /// <summary>
    /// L2 Tend — post-floor level. Decides retries, reschedules, and parameter tweaks within
    /// already-approved runs; escalates ALL shipping and all origination beyond re-runs.
    /// Memory adoption is carved out (always escalates to human).
    /// </summary>
    L2Tend,

    /// <summary>
    /// L3 Ship routine — green + clean-second-read work on branches; merge/main and beyond escalate.
    /// Gated on #659; not yet grantable. Memory adoption is carved out (always escalates to human).
    /// </summary>
    L3ShipRoutine
}
