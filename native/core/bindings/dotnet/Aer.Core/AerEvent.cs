using System.Runtime.InteropServices;

namespace Aer.Core;

// Explicit layout must match aer.h exactly (64-bit assumed; all target platforms are 64-bit):
//   offset  0: kind      uint32_t
//   offset  4: pid       uint32_t
//   offset  8: code      int32_t
//   offset 12: reason    uint32_t
//   offset 16: seq       uint64_t
//   offset 24: data      const uint8_t *   (8 bytes on 64-bit)
//   offset 32: data_len  size_t            (8 bytes on 64-bit)
//   total: 40 bytes
/// <summary>
/// Raw event payload delivered to <see cref="AerEventCallback"/>. Layout is stable ABI.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
public struct AerEvent
{
    /// <summary>Discriminant — one of the <see cref="AerEventKind"/> constants.</summary>
    [FieldOffset(0)] public uint Kind;
    /// <summary>Process ID of the child.</summary>
    [FieldOffset(4)] public uint Pid;
    /// <summary>Exit code (valid on <see cref="AerEventKind.Exited"/>).</summary>
    [FieldOffset(8)] public int Code;
    /// <summary>Exit reason — one of the <see cref="AerExitReason"/> values (valid on <see cref="AerEventKind.Exited"/>).</summary>
    [FieldOffset(12)] public uint Reason;
    /// <summary>Monotonically increasing event sequence number.</summary>
    [FieldOffset(16)] public ulong Seq;
    /// <summary>Pointer to chunk bytes (valid on stdout/stderr events). Do not free; valid only for the duration of the callback.</summary>
    [FieldOffset(24)] public nint Data;
    /// <summary>Byte length of <see cref="Data"/>.</summary>
    [FieldOffset(32)] public nuint DataLen;
}

/// <summary>Discriminant constants for <see cref="AerEvent.Kind"/>.</summary>
public static class AerEventKind
{
    /// <summary>The child process has started.</summary>
    public const uint Started = 0;
    /// <summary>The child process has exited.</summary>
    public const uint Exited = 1;
    /// <summary>A chunk of stdout bytes is available in <see cref="AerEvent.Data"/>.</summary>
    public const uint StdoutChunk = 2;
    /// <summary>A chunk of stderr bytes is available in <see cref="AerEvent.Data"/>.</summary>
    public const uint StderrChunk = 3;
}
