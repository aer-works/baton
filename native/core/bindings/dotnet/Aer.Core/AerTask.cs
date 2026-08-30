using System.Runtime.InteropServices;

namespace Aer.Core;

/// <summary>
/// Idiomatic managed wrapper over the <c>aer_core</c> native task API. Owns a native task handle
/// (and, when cancellation is requested, a native cancel handle) and exposes configuration via
/// fluent <c>With*</c> methods, execution via <see cref="Run"/> / <see cref="RunAsync"/>, and
/// progress via the <see cref="EventRaised"/> event instead of raw callbacks.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime/thread-safety: <see cref="AerTaskHandle"/> and <see cref="AerCancelHandle"/> are
/// <see cref="SafeHandle"/>s, so every native call below add-refs the handle for the call's
/// duration — <see cref="Dispose"/> can safely race a concurrent <see cref="Run"/> (the CLR defers
/// the actual free until the in-flight call returns); there is no raw <c>nint</c> +
/// <c>DangerousGetHandle</c> use-after-free window anywhere in this type.
/// </para>
/// <para>
/// A given instance may be run at most once (<c>aer_task_run</c> enforces this natively; this
/// wrapper also fails fast with <see cref="InvalidOperationException"/>).
/// </para>
/// </remarks>
public sealed class AerTask : IDisposable
{
    private readonly AerTaskHandle handle;
    private int hasRunFlag;
    private int disposedFlag;

    // Set for the duration of a run that observes a cancellable token; cleared afterwards.
    // Tracked as a field (rather than only a local) so Dispose() can free it if the caller
    // disposes the AerTask without awaiting/observing RunAsync to completion.
    private AerCancelHandle? cancelHandle;

    /// <summary>
    /// Raised for every event the native run produces, in delivery order: one <c>Started</c>,
    /// then interleaved <c>StdoutChunk</c>/<c>StderrChunk</c> events (only when
    /// <see cref="WithCaptureOutput"/> was enabled; each stream's <see cref="AerEventArgs.Seq"/> is
    /// monotonically increasing within that stream), then one <c>Exited</c>. Invoked synchronously
    /// on the thread executing the native run — for <see cref="RunAsync"/> that is the thread-pool
    /// thread the run was scheduled on, not the caller's original thread.
    /// </summary>
    public event EventHandler<AerEventArgs>? EventRaised;

    /// <summary>
    /// Creates a task for the given program and arguments. The process is not spawned until
    /// <see cref="Run"/> or <see cref="RunAsync"/> is called.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="args"/> is null.</exception>
    /// <exception cref="AerException">
    /// <paramref name="program"/> is not valid UTF-8, or any element of <paramref name="args"/> is
    /// null or not valid UTF-8.
    /// </exception>
    public AerTask(string program, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(args);

        handle = NativeMethods.CreateTask(program, args);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new AerException(
                AerErrorCode.NullPointer,
                "Failed to create task: 'program' or one of 'args' was null or not valid UTF-8.");
        }
    }

    /// <summary>
    /// Sets a wall-clock timeout. If the process has not exited by the deadline it is killed and
    /// <see cref="Run"/>/<see cref="RunAsync"/> throws <see cref="AerTimeoutException"/>. Must be
    /// called before the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public AerTask WithTimeout(TimeSpan timeout)
    {
        ThrowIfDisposed();
        ThrowIfConfigError(NativeMethods.aer_task_with_timeout(handle, (ulong)timeout.TotalMilliseconds));
        return this;
    }

    /// <summary>
    /// Enables (or disables) stdout/stderr capture. When enabled, <see cref="EventRaised"/> carries
    /// <c>StdoutChunk</c>/<c>StderrChunk</c> events with the child's output. Must be called before
    /// the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public AerTask WithCaptureOutput(bool capture = true)
    {
        ThrowIfDisposed();
        ThrowIfConfigError(NativeMethods.aer_task_with_capture_output(handle, capture));
        return this;
    }

    /// <summary>
    /// Sets an environment variable for the child process. Repeatable: calling this again with the
    /// same <paramref name="key"/> overrides the previously set value. Variables set this way are
    /// always visible to the child, regardless of <see cref="WithClearEnv"/>. Must be called before
    /// the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="AerException">
    /// <paramref name="key"/> is empty or contains '=', or either argument is not valid UTF-8
    /// (<see cref="AerErrorCode.InvalidArgument"/>).
    /// </exception>
    public AerTask WithEnv(string key, string value)
    {
        ThrowIfDisposed();
        ThrowIfConfigError(NativeMethods.aer_task_set_env(handle, key, value));
        return this;
    }

    /// <summary>
    /// Sets whether the child inherits the parent's environment. When <paramref name="clear"/> is
    /// <see langword="true"/>, the child inherits nothing except variables set via
    /// <see cref="WithEnv"/>. Default is <see langword="false"/> (inherit everything). Must be
    /// called before the task is run.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public AerTask WithClearEnv(bool clear = true)
    {
        ThrowIfDisposed();
        ThrowIfConfigError(NativeMethods.aer_task_set_clear_env(handle, clear));
        return this;
    }

    /// <summary>
    /// Sets the child process's working directory. Must be called before the task is run. If the
    /// path does not exist or is not a directory, this surfaces at run time as an
    /// <see cref="AerException"/> with <see cref="AerErrorCode.SpawnFailed"/>.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="AerException"><paramref name="path"/> is empty or not valid UTF-8 (<see cref="AerErrorCode.InvalidArgument"/>).</exception>
    public AerTask WithCwd(string path)
    {
        ThrowIfDisposed();
        ThrowIfConfigError(NativeMethods.aer_task_set_cwd(handle, path));
        return this;
    }

    /// <summary>
    /// Spawns the process and blocks the calling thread until it exits. The native run is
    /// inherently blocking (there is no native async execution model); use <see cref="RunAsync"/>
    /// to run it on a thread-pool thread instead of the caller's thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">This instance has already been run.</exception>
    /// <exception cref="AerTimeoutException">The configured timeout elapsed.</exception>
    /// <exception cref="AerException">The native run failed for any other reason.</exception>
    public void Run() => RunCore(CancellationToken.None);

    /// <summary>
    /// Runs the task on a thread-pool thread via <see cref="Task.Run(Action)"/>, wrapping the
    /// inherently-blocking native call. Because the native ABI has no async execution model, this
    /// does not reduce the number of OS threads consumed — it only frees the calling thread.
    /// </summary>
    /// <param name="cancellationToken">
    /// When cancelled, requests cancellation of the native run via a per-run
    /// <see cref="AerCancelHandle"/> (bridged with <c>aer_cancel</c>). The task then completes by
    /// throwing <see cref="AerCancelException"/> — not <see cref="OperationCanceledException"/> —
    /// once the native run observes the cancellation and returns.
    /// </param>
    /// <exception cref="InvalidOperationException">This instance has already been run.</exception>
    /// <exception cref="AerCancelException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="AerTimeoutException">The configured timeout elapsed.</exception>
    /// <exception cref="AerException">The native run failed for any other reason.</exception>
    public Task RunAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCore(cancellationToken), CancellationToken.None);

    /// <summary>Releases the underlying native task handle (and cancel handle, if one was created).</summary>
    /// <remarks>
    /// Safe to call while a run is in progress on another thread/task — the <see cref="SafeHandle"/>s
    /// defer the actual native free until any in-flight call on them returns.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
        {
            return;
        }

        cancelHandle?.Dispose();
        handle.Dispose();
    }

    private void RunCore(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref hasRunFlag, 1) != 0)
        {
            throw new InvalidOperationException("AerTask.Run/RunAsync may only be called once per instance.");
        }

        AerCancelHandle? localCancel = null;
        CancellationTokenRegistration registration = default;
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                // Must be created before aer_task_run: the native side only wires cancellation
                // support into the run if a cancel handle already exists on the task at that point.
                localCancel = NativeMethods.aer_task_make_cancel_handle(handle);
                if (localCancel.IsInvalid)
                {
                    localCancel.Dispose();
                    localCancel = null;
                    throw new AerException(
                        AerErrorCode.NullPointer,
                        "Failed to create a native cancel handle for this run.");
                }

                cancelHandle = localCancel;
                registration = cancellationToken.Register(
                    static state => _ = NativeMethods.aer_cancel((AerCancelHandle)state!),
                    localCancel);
            }

            // CallbackBridge GC-pins its native trampoline delegate for its own lifetime (see
            // CallbackBridge.cs), so it — and this AerTask, kept alive as the closure's captured
            // 'this' — cannot be collected while aer_task_run holds a pointer to it.
            using CallbackBridge bridge = new(Dispatch);

            AerErrorCode result = NativeMethods.aer_task_run(handle, bridge.NativeCallback, nint.Zero);
            if (result != AerErrorCode.Ok)
            {
                throw BuildRunException(result);
            }
        }
        finally
        {
            registration.Dispose();
            cancelHandle = null;
            localCancel?.Dispose();
        }
    }

    private void Dispatch(AerEvent evt, byte[]? data)
    {
        AerEventArgs? args = null;
        if (evt.Kind == AerEventKind.Started)
        {
            args = new AerEventArgs { Kind = AerTaskEventKind.Started, Pid = evt.Pid };
        }
        else if (evt.Kind == AerEventKind.Exited)
        {
            args = new AerEventArgs
            {
                Kind = AerTaskEventKind.Exited,
                ExitCode = evt.Code,
                ExitReason = (AerExitReason)evt.Reason,
            };
        }
        else if (evt.Kind == AerEventKind.StdoutChunk)
        {
            args = new AerEventArgs { Kind = AerTaskEventKind.StdoutChunk, Seq = evt.Seq, Data = data };
        }
        else if (evt.Kind == AerEventKind.StderrChunk)
        {
            args = new AerEventArgs { Kind = AerTaskEventKind.StderrChunk, Seq = evt.Seq, Data = data };
        }

        if (args is not null)
        {
            EventRaised?.Invoke(this, args);
        }
    }

    private static void ThrowIfConfigError(AerErrorCode code)
    {
        if (code == AerErrorCode.Ok)
        {
            return;
        }

        throw new AerException(code, GetLastErrorMessage() ?? DefaultMessageFor(code));
    }

    private static AerException BuildRunException(AerErrorCode code)
    {
        string message = GetLastErrorMessage() ?? DefaultMessageFor(code);
        return code switch
        {
            AerErrorCode.TimedOut => new AerTimeoutException(message),
            AerErrorCode.Cancelled => new AerCancelException(message),
            AerErrorCode.Ok
                or AerErrorCode.NullPointer
                or AerErrorCode.SpawnFailed
                or AerErrorCode.WaitFailed
                or AerErrorCode.InvalidStateTransition
                or AerErrorCode.KillFailed
                or AerErrorCode.AlreadyRun
                or AerErrorCode.Panic
                or AerErrorCode.InvalidArgument => new AerException(code, message),
            _ => new AerException(code, message),
        };
    }

    private static string? GetLastErrorMessage()
    {
        nint ptr = NativeMethods.aer_last_error_message();
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    private static string DefaultMessageFor(AerErrorCode code) => code switch
    {
        AerErrorCode.Ok => "No error.",
        AerErrorCode.NullPointer => "A required native argument was null.",
        AerErrorCode.SpawnFailed => "The operating system refused to spawn the child process.",
        AerErrorCode.WaitFailed => "Waiting on the child process failed.",
        AerErrorCode.InvalidStateTransition => "The task was used in an invalid state.",
        AerErrorCode.TimedOut => "The task was killed because it exceeded its configured timeout.",
        AerErrorCode.KillFailed => "Killing the child process failed.",
        AerErrorCode.AlreadyRun => "The task has already been run.",
        AerErrorCode.Panic => "The native library encountered an unexpected internal error.",
        AerErrorCode.Cancelled => "The task was cancelled.",
        AerErrorCode.InvalidArgument => "A string argument failed native validation.",
        _ => $"AER operation failed with error code {code}.",
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposedFlag) != 0, this);
}
