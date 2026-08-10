using Aer.Flow;

namespace Aer.Adapters;

/// <summary>
/// Raised by <see cref="IWorkerAdapter.Resolve"/> when a <c>--effort</c> cannot be honored on the
/// requested agy model, for either measured reason: the value is outside agy's set (sentinel
/// <c>effort.agy-value-set</c>: exactly {low, medium, high}), or it disagrees with the effort the
/// model name already encodes as a suffix (sentinel <c>effort.agy-effort-and-suffix-must-agree</c>:
/// <c>gemini-3.6-flash-low --effort high</c> is one control spelled two conflicting ways, refused at
/// agy-bind-time with a raw <c>conflicts with</c>). Refused up-front like its sibling
/// <see cref="MalformedVendorModelException"/>, naming the real cause instead of letting agy's message
/// surface after the operator has waited for a run that could never bind. An agreeing pair is left
/// untouched and emitted byte-for-byte.
/// </summary>
public sealed class IncoherentVendorEffortException : AerFlowException
{
    public string AdapterName { get; }

    public IncoherentVendorEffortException(string adapterName, string reason)
        : base($"The '{adapterName}' adapter cannot use the requested --effort: {reason}")
    {
        AdapterName = adapterName;
    }
}
