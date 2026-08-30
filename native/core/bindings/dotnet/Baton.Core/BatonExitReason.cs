namespace Baton.Core;

/// <summary>
/// Reason an <see cref="BatonEvent"/> of kind Exited was delivered. Integer values are stable ABI — never reorder.
/// </summary>
public enum BatonExitReason : uint
{
    /// <summary>The process exited on its own.</summary>
    Natural = 0,
    /// <summary>The task exceeded its configured wall-clock timeout.</summary>
    TimedOut = 1,
    /// <summary>A cancel handle was triggered.</summary>
    CancelRequested = 2,
}
