using Baton;

namespace Baton.Vendors;

/// <summary>
/// #1732 review N5: the sibling refusal to <see cref="AgyHookUnverifiedException"/> -- see that
/// type's own remarks for the shape of grant this guards (decision 0029; #1680) and why AER refuses
/// rather than dispatches unverified. This one fires earlier, before the probe even runs: the
/// per-execution canary that backs up the probe derives its tool-call count entirely from
/// stream-json <c>step_update</c> lines, so a <c>StreamJson: false</c> binding under that same grant
/// shape leaves the canary permanently unreachable -- a hook that dies after resolve would then go
/// uncaught for the binding's whole lifetime, with no way for the operator to see it. Thrown
/// fail-closed rather than shipping that gap undisclosed.
/// </summary>
public sealed class AgyCanaryRequiresStreamJsonException : BatonFlowException
{
    public AgyCanaryRequiresStreamJsonException()
        : base(
            "This role's grant requires agy's PreToolUse hook to be its sole narrowing (decision " +
            "0029; #1680), which in turn requires the per-execution verdict canary to be reachable -- " +
            "and that canary counts tool calls from stream-json output, so it can never fire on a " +
            "StreamJson: false binding. Set StreamJson: true for this role, or widen its grant so the " +
            "hook is not the sole narrowing.")
    {
    }
}
