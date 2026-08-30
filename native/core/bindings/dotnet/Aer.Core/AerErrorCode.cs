namespace Aer.Core;

/// <summary>
/// Return codes from all fallible FFI functions. Integer values are stable ABI — never reorder.
/// </summary>
public enum AerErrorCode : int
{
    /// <summary>Operation succeeded.</summary>
    Ok = 0,
    /// <summary>A required pointer argument was null.</summary>
    NullPointer = 1,
    /// <summary>The OS refused to spawn the child process.</summary>
    SpawnFailed = 2,
    /// <summary>Waiting on the child process failed.</summary>
    WaitFailed = 3,
    /// <summary>An operation was attempted in an invalid task state.</summary>
    InvalidStateTransition = 4,
    /// <summary>The task exceeded its configured wall-clock timeout.</summary>
    TimedOut = 5,
    /// <summary>Sending SIGKILL/TerminateProcess to the child failed.</summary>
    KillFailed = 6,
    /// <summary>The task handle has already been run and cannot be run again.</summary>
    AlreadyRun = 7,
    /// <summary>The native library encountered an unrecoverable internal error.</summary>
    Panic = 8,
    /// <summary>The task was cancelled via its cancel handle.</summary>
    Cancelled = 9,
    /// <summary>A string argument failed validation (invalid UTF-8, empty key, or a key containing '=').</summary>
    InvalidArgument = 10,
}
