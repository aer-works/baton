namespace Baton.Core;

/// <summary>
/// Failure taxonomy for <see cref="BatonException"/>. Pre-#1474 this enum doubled as the return-code
/// ABI crossing the aer-core FFI boundary (values were "stable ABI, never reorder"); that boundary no
/// longer exists, so members that existed only to signal an FFI-level programmer error (a null native
/// pointer, an already-freed handle, a caught native panic, a state-machine transition invalid only
/// because of two-call FFI sequencing) are gone. What remains are the failure modes the managed
/// implementation can actually produce.
/// </summary>
public enum BatonErrorCode
{
    /// <summary>The OS refused to spawn the child process.</summary>
    SpawnFailed,
    /// <summary>Waiting on the child process failed.</summary>
    WaitFailed,
    /// <summary>The task exceeded its configured wall-clock timeout.</summary>
    TimedOut,
    /// <summary>The task was cancelled via the <see cref="CancellationToken"/> passed to <see cref="BatonTask.RunAsync"/>.</summary>
    Cancelled,
    /// <summary>A string argument failed validation (an environment variable key that is empty or contains '=', or an empty working directory).</summary>
    InvalidArgument,
}
