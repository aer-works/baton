using System.Runtime.InteropServices;

namespace Aer.Core;

internal sealed class AerTaskHandle : SafeHandle
{
    internal AerTaskHandle() : base(nint.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.aer_task_free(handle);
        return true;
    }
}
