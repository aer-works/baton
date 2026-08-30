namespace Aer.Core;

/// <summary>
/// Payload for <see cref="AerTask.EventRaised"/>. One instance is created per native
/// <c>AerEvent</c> delivered during a run; check <see cref="Kind"/> to determine which
/// other members are meaningful.
/// </summary>
public sealed class AerEventArgs : EventArgs
{
    /// <summary>Which kind of event this is.</summary>
    public required AerTaskEventKind Kind { get; init; }

    /// <summary>Process ID of the child. Meaningful when <see cref="Kind"/> is <see cref="AerTaskEventKind.Started"/>.</summary>
    public uint Pid { get; init; }

    /// <summary>Exit code of the child, or -1 if it was killed. Meaningful when <see cref="Kind"/> is <see cref="AerTaskEventKind.Exited"/>.</summary>
    public int ExitCode { get; init; }

    /// <summary>Reason the child exited. Meaningful when <see cref="Kind"/> is <see cref="AerTaskEventKind.Exited"/>.</summary>
    public AerExitReason ExitReason { get; init; }

    /// <summary>
    /// Monotonically increasing sequence number, scoped per stream (stdout and stderr each have
    /// their own sequence). Meaningful when <see cref="Kind"/> is <see cref="AerTaskEventKind.StdoutChunk"/>
    /// or <see cref="AerTaskEventKind.StderrChunk"/>; use it to detect out-of-order delivery within a stream.
    /// </summary>
    public ulong Seq { get; init; }

    /// <summary>
    /// A defensive copy of the chunk bytes, safe to retain past the event handler's return.
    /// Meaningful when <see cref="Kind"/> is <see cref="AerTaskEventKind.StdoutChunk"/> or
    /// <see cref="AerTaskEventKind.StderrChunk"/>; <see langword="null"/> otherwise.
    /// </summary>
    public byte[]? Data { get; init; }
}
