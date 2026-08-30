using System.Runtime.InteropServices;

namespace Baton.Core;

/// <summary>
/// Bridges a managed handler to the raw <see cref="BatonEventCallback"/> expected by
/// <c>aer_task_run</c>. Marshals <see cref="BatonEvent"/> from the native pointer and
/// copies chunk data into a managed <c>byte[]</c> before dispatch.
/// </summary>
internal sealed class CallbackBridge : IDisposable
{
    internal BatonEventCallback NativeCallback { get; }
    private readonly GCHandle pin;
    private readonly Action<BatonEvent, byte[]?> handler;
    private bool disposed;

    internal CallbackBridge(Action<BatonEvent, byte[]?> handler)
    {
        this.handler = handler;
        NativeCallback = Dispatch;
        // GCHandleType.Normal keeps the delegate rooted for the lifetime of this bridge.
        // The GC must not collect it while native code holds a pointer to its trampoline.
        pin = GCHandle.Alloc(NativeCallback);
    }

    private void Dispatch(nint evtPtr, nint userData)
    {
        BatonEvent evt = Marshal.PtrToStructure<BatonEvent>(evtPtr);

        // For chunk events, evt.Data is a native pointer valid only for this callback.
        // Copy the bytes into a managed array before returning.
        byte[]? chunkData = null;
        if ((evt.Kind == BatonEventKind.StdoutChunk || evt.Kind == BatonEventKind.StderrChunk)
            && evt.DataLen > 0)
        {
            chunkData = new byte[(int)evt.DataLen];
            Marshal.Copy(evt.Data, chunkData, 0, chunkData.Length);
        }

        handler(evt, chunkData);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            pin.Free();
            disposed = true;
        }
    }
}
