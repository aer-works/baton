namespace Aer.Flow.Concurrency;

/// <summary>
/// Raised when a <see cref="ConcurrencyGuard"/> acquire — any of <see cref="ConcurrencyGuard.Acquire"/>,
/// <see cref="ConcurrencyGuard.AcquireWithin"/>, <see cref="ConcurrencyGuard.AcquireRoomEvents"/>, or
/// <see cref="ConcurrencyGuard.AcquireRoomEventsWithin"/> — cannot obtain one of a room directory's
/// lock files (0053: <c>flow.lock</c> or <c>room-events.lock</c>) because another Flow instance
/// already holds it ("at most one writer per room namespace", held per log).
/// The exception's message names which lock file was contended.
/// </summary>
public sealed class WorkflowLockedException : AerFlowException
{
    public string? HolderDescription { get; }
    public DateTime? AcquiredAtUtc { get; }

    public WorkflowLockedException(string message, string? holderDescription = null, DateTime? acquiredAtUtc = null)
        : base(message)
    {
        HolderDescription = holderDescription;
        AcquiredAtUtc = acquiredAtUtc;
    }

    public WorkflowLockedException(string message, Exception innerException, string? holderDescription = null, DateTime? acquiredAtUtc = null)
        : base(message, innerException)
    {
        HolderDescription = holderDescription;
        AcquiredAtUtc = acquiredAtUtc;
    }
}
