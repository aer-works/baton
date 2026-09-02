using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised when a <c>--effort</c> cannot be honored, for any of four measured reasons across the two
/// adapters that throw it. On agy, from <c>AgyWorkerAdapter.ReconcileAgyEffort</c>: the value is
/// outside agy's set (sentinel <c>effort.agy-value-set</c>: exactly {low, medium, high}), or it
/// disagrees with the effort the model name already encodes as a suffix (sentinel
/// <c>effort.agy-effort-and-suffix-must-agree</c>: <c>gemini-3.6-flash-low --effort high</c> is one
/// control spelled two conflicting ways, refused at agy-bind-time with a raw <c>conflicts with</c>).
/// Also from agy, but from <c>AgyWorkerAdapter.RequiresAgyEffort</c> (#1596): no <c>--effort</c> was
/// given at all, and the model requires one (a suffix-less gemini model, e.g. <c>gemini-3.7-flash</c>)
/// — agy's own refusal for this is <c>--model X requires --effort (available: ...)</c>, paid for by a
/// full spawn first; this refuses it at resolution instead, before that spawn.
/// On both agy and claude, from <see cref="EffortTierMapping"/>'s shared <c>Resolve</c> (used by both
/// <see cref="EffortTierMapping.ResolveForClaude"/> and <see cref="EffortTierMapping.ResolveForAgy"/>):
/// the effort string is neither one of 0023's four canonical words nor already one of that vendor's
/// own raw values — see that type's own remarks for why this is refused rather than forwarded.
/// Refused up-front like its sibling <see cref="MalformedVendorModelException"/>. An agreeing pair is
/// left untouched and emitted byte-for-byte.
/// </summary>
public sealed class IncoherentVendorEffortException : BatonFlowException
{
    public string AdapterName { get; }

    public IncoherentVendorEffortException(string adapterName, string reason)
        : base($"The '{adapterName}' adapter cannot use the requested --effort: {reason}")
    {
        AdapterName = adapterName;
    }
}
