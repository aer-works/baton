using System.Runtime.InteropServices;

namespace Baton.Core;

internal sealed class BatonTaskHandle : SafeHandle
{
    internal BatonTaskHandle() : base(nint.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.aer_task_free(handle);
        return true;
    }
}
