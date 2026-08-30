using System.Runtime.InteropServices;

namespace Baton.Core;

internal sealed class BatonCancelHandle : SafeHandle
{
    internal BatonCancelHandle() : base(nint.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.aer_cancel_free(handle);
        return true;
    }
}
