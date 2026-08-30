using System.Runtime.InteropServices;

namespace Baton.Core;

/// <summary>
/// Raw native callback signature matching <c>BatonEventCallback</c> in <c>aer.h</c>.
/// Called synchronously from <c>aer_task_run</c> on the calling thread.
/// The <paramref name="evt"/> pointer is valid only for the duration of the callback.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void BatonEventCallback(nint evt, nint userData);
