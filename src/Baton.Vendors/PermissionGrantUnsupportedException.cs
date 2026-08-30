using Baton.Flow;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="IWorkerAdapter.Resolve"/> when a <see cref="WorkerInvocation.PermissionGrant"/>
/// requests a category the adapter's <see cref="IPermissionGrantTranslator.TryTranslatePermissionGrant"/>
/// cannot express — thrown at dispatch time as defense in depth; the bindings editor UI is expected
/// to call <see cref="IPermissionGrantTranslator.TryTranslatePermissionGrant"/> directly and surface
/// the same gap before Save, so hitting this exception in practice means a hand-edited config file
/// requested something the UI would have refused to save.
/// </summary>
public sealed class PermissionGrantUnsupportedException : BatonFlowException
{
    public string AdapterName { get; }

    public PermissionGrantUnsupportedException(string adapterName, string reason)
        : base($"The '{adapterName}' adapter cannot express the requested permission grant: {reason}")
    {
        AdapterName = adapterName;
    }
}
