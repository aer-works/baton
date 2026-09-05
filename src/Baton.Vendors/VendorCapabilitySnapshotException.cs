using Baton;

namespace Baton.Vendors;

/// <summary>
/// Raised when an adapter's embedded, dated capability recording — the evidence its model and effort
/// validation rests on — cannot be read (#1875). Deliberately loud: an unreadable recording yielding
/// an empty table would still fail closed, but every later rejection would read as "this model is
/// absent from the recorded snapshot" and send the operator hunting the model instead of the missing
/// or corrupted resource.
/// </summary>
public sealed class VendorCapabilitySnapshotException : BatonFlowException
{
    public string AdapterName { get; }

    public string ResourceName { get; }

    public VendorCapabilitySnapshotException(string adapterName, string resourceName, string reason)
        : base($"The '{adapterName}' adapter cannot read its recorded capability snapshot '{resourceName}': {reason}.")
    {
        AdapterName = adapterName;
        ResourceName = resourceName;
    }

    public VendorCapabilitySnapshotException(
        string adapterName, string resourceName, string reason, Exception innerException)
        : base(
            $"The '{adapterName}' adapter cannot read its recorded capability snapshot '{resourceName}': {reason}.",
            innerException)
    {
        AdapterName = adapterName;
        ResourceName = resourceName;
    }
}
