namespace Baton.Status;

/// <summary>
/// Raised when <see cref="TerminalSentinelWriter.DeleteStaleSentinel(string, bool)"/> cannot remove a
/// room's stale <c>terminal.json</c> and the caller asked to fail closed (#1608 re-review finding 2).
/// A typed <see cref="BatonFlowException"/> rather than the underlying <see cref="IOException"/> so the
/// one place that catches those (<c>Program.cs</c>) prints an operator-facing refusal naming the locked
/// file, instead of surfacing a stack trace for a condition the operator can fix.
/// </summary>
public sealed class StaleSentinelDeletionException : BatonFlowException
{
    public StaleSentinelDeletionException(string message)
        : base(message)
    {
    }

    public StaleSentinelDeletionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
