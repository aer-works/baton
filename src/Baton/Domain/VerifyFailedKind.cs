using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// #1623 / review finding F3: discriminator for <see cref="FlowEvent.VerifyFailed"/> so a conductor can
/// tell gate failures from contention timeouts, cancellations, or engine restarts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyFailedKind
{
    GatesFailed,
    TimedOut,
    Cancelled,
    EngineRestart,
}
