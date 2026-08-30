using System.Runtime.InteropServices;

namespace Aer.Core;

internal sealed class AerCancelHandle : SafeHandle
{
    internal AerCancelHandle() : base(nint.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.aer_cancel_free(handle);
        return true;
    }
}
