namespace Baton.Core;

/// <summary>
/// Reason a <see cref="BatonEventArgs"/> of kind Exited was delivered.
/// </summary>
public enum BatonExitReason : uint
{
    /// <summary>The process exited on its own.</summary>
    Natural = 0,
    /// <summary>The task exceeded its configured wall-clock timeout.</summary>
    TimedOut = 1,
    /// <summary>An on-demand cancellation request killed the process.</summary>
    CancelRequested = 2,
}
