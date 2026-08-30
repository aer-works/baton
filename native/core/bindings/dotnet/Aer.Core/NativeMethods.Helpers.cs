using System.Runtime.InteropServices;

namespace Aer.Core;

internal static partial class NativeMethods
{
    /// <summary>
    /// Convenience overload that marshals a managed string array to native UTF-8 pointers,
    /// calls <see cref="aer_task_new"/>, then frees the native memory.
    /// </summary>
    internal static AerTaskHandle CreateTask(string program, params string[] args)
    {
        if (args.Length == 0)
        {
            return aer_task_new(program, nint.Zero, 0);
        }

        nint[] ptrs = new nint[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        }

        // Pin the pointer array so its address is stable for the duration of aer_task_new.
        GCHandle pin = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
        try
        {
            return aer_task_new(program, pin.AddrOfPinnedObject(), (nuint)ptrs.Length);
        }
        finally
        {
            pin.Free();
            foreach (nint p in ptrs)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }
}
