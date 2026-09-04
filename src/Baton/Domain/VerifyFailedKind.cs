using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// #1623 / review finding F3: discriminator for <see cref="FlowEvent.VerifyFailed"/> so a conductor can
/// tell gate failures from contention timeouts, cancellations, or engine restarts. #1788 adds
/// <see cref="DeliveryFailed"/> for the post-exit push/PR delivery check
/// (<c>Mutation.DeliveryVerifier</c>) — a different stage than the role's own gate command, so a
/// conductor can tell "the code doesn't pass gates" from "the code passed gates but was never pushed or
/// opened as a PR". #1796 adds <see cref="BuildLockBusy"/>: <see cref="Mutation.VerifyRunner"/> uses
/// this value only as its own outcome discriminator (never as a <see cref="FlowEvent.VerifyFailed.Kind"/>
/// on the wire) — a run whose only failing member(s) were blocked on <c>tools/buildlock.py</c>'s lock
/// settles as <see cref="FlowEvent.VerifyNotRun"/>, not <see cref="FlowEvent.VerifyFailed"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerifyFailedKind
{
    GatesFailed,
    TimedOut,
    Cancelled,
    EngineRestart,
    DeliveryFailed,
    BuildLockBusy,
}
