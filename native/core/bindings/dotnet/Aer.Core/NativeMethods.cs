using System.Runtime.InteropServices;

namespace Aer.Core;

/// <summary>
/// Raw P/Invoke declarations matching the stable C ABI in <c>aer.h</c>.
/// All signatures are unsafe-free; the idiomatic managed surface lives in <see cref="AerTask"/>.
/// </summary>
internal static partial class NativeMethods
{
    private const string Lib = "aer_core";

    // DllImport: LibraryImport cannot marshal SafeHandle as a return type (SYSLIB1051).
    /// <summary>
    /// Create a new task. Returns an invalid handle on invalid input.
    /// The returned handle is freed automatically via <see cref="AerTaskHandle.ReleaseHandle"/>.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern AerTaskHandle aer_task_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string program,
        nint args,
        nuint argsLen);

    /// <summary>
    /// Set a wall-clock timeout in milliseconds. Must be called before <see cref="aer_task_run"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="task"/> is typed as the <see cref="AerTaskHandle"/> SafeHandle itself (not
    /// <c>nint</c>) so the CLR add-refs it for the duration of this call — see the discussion on issue #62
    /// for why a raw handle + <c>DangerousGetHandle</c> is unsafe here (finalizer could free the task
    /// mid-call).
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial AerErrorCode aer_task_with_timeout(AerTaskHandle task, ulong timeoutMs);

    /// <summary>Enable stdout/stderr capture. Must be called before <see cref="aer_task_run"/>.</summary>
    [LibraryImport(Lib)]
    public static partial AerErrorCode aer_task_with_capture_output(
        AerTaskHandle task,
        [MarshalAs(UnmanagedType.U1)] bool capture);

    /// <summary>
    /// Set an environment variable for the child process. Must be called before <see cref="aer_task_run"/>.
    /// Repeatable: calling this again with the same <paramref name="key"/> overrides the previously set
    /// value. Variables set this way are always visible to the child, regardless of
    /// <see cref="aer_task_set_clear_env"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="task"/> is typed as the <see cref="AerTaskHandle"/> SafeHandle itself (not
    /// <c>nint</c>) so the CLR add-refs it for the duration of this call — see the discussion on issue #62
    /// for why a raw handle + <c>DangerousGetHandle</c> is unsafe here (finalizer could free the task
    /// mid-call).
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial AerErrorCode aer_task_set_env(
        AerTaskHandle task,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    /// <summary>
    /// Set whether the child process inherits the parent's environment. Must be called before
    /// <see cref="aer_task_run"/>. When <paramref name="clear"/> is true, the child inherits nothing from
    /// the parent environment — only variables set via <see cref="aer_task_set_env"/> are present.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial AerErrorCode aer_task_set_clear_env(
        AerTaskHandle task,
        [MarshalAs(UnmanagedType.U1)] bool clear);

    /// <summary>
    /// Set the child process's working directory. Must be called before <see cref="aer_task_run"/>. If the
    /// path does not exist or is not a directory, this surfaces at <see cref="aer_task_run"/> time as
    /// <see cref="AerErrorCode.SpawnFailed"/>.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial AerErrorCode aer_task_set_cwd(
        AerTaskHandle task,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    // DllImport: LibraryImport cannot marshal SafeHandle as a return type (SYSLIB1051).
    /// <summary>
    /// Create a cancellation handle. Must be called before <see cref="aer_task_run"/>.
    /// Returns an invalid handle on failure. Freed automatically via <see cref="AerCancelHandle.ReleaseHandle"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="task"/> is typed as the <see cref="AerTaskHandle"/> SafeHandle itself so the CLR
    /// add-refs it for the duration of this call rather than relying on a raw <c>nint</c> plus
    /// <c>DangerousGetHandle</c> (see issue #62).
    /// </remarks>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern AerCancelHandle aer_task_make_cancel_handle(AerTaskHandle task);

    // DllImport: LibraryImport does not support delegate marshaling.
    // Callers should wrap this via CallbackBridge, which GC-pins the delegate and marshals the event.
    /// <summary>
    /// Spawn the process and block until it exits. <paramref name="callback"/> may be <see langword="null"/>.
    /// A handle may only be run once; a second call returns <see cref="AerErrorCode.AlreadyRun"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="task"/> is typed as the <see cref="AerTaskHandle"/> SafeHandle itself so the CLR
    /// add-refs it for the duration of this (potentially long-running, blocking) call — a raw <c>nint</c>
    /// plus <c>DangerousGetHandle</c> would let the finalizer free the task mid-run (see issue #62).
    /// </remarks>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern AerErrorCode aer_task_run(
        AerTaskHandle task,
        AerEventCallback? callback,
        nint userData);

    /// <summary>Free a task handle. Safe to call with <see cref="nint.Zero"/>.</summary>
    /// <remarks>
    /// Kept as a raw <c>nint</c> parameter: this is invoked only from
    /// <see cref="AerTaskHandle.ReleaseHandle"/>, which by definition already owns the last reference to
    /// the handle value and must not go through the SafeHandle itself.
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial void aer_task_free(nint task);

    /// <summary>Cancel a running task. Safe to call from any thread at any time.</summary>
    /// <remarks>
    /// <paramref name="cancel"/> is typed as the <see cref="AerCancelHandle"/> SafeHandle itself so the
    /// CLR add-refs it for the duration of this call (see issue #62).
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial AerErrorCode aer_cancel(AerCancelHandle cancel);

    /// <summary>Free a cancel handle. Safe to call with <see cref="nint.Zero"/>.</summary>
    /// <remarks>
    /// Kept as a raw <c>nint</c> parameter: this is invoked only from
    /// <see cref="AerCancelHandle.ReleaseHandle"/>, which by definition already owns the last reference to
    /// the handle value and must not go through the SafeHandle itself.
    /// </remarks>
    [LibraryImport(Lib)]
    public static partial void aer_cancel_free(nint cancel);

    /// <summary>
    /// Return the last error message for this thread.
    /// Returns <see cref="nint.Zero"/> if no error since the last successful operation.
    /// Valid until the next FFI call on this thread; do not free.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial nint aer_last_error_message();
}
