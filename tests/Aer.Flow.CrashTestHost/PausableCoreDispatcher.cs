using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Flow.CrashTestHost;

/// <summary>Where <see cref="PausableCoreDispatcher"/> blocks relative to the real dispatch it wraps.</summary>
public enum DispatchPausePoint
{
    /// <summary>No pause: dispatches immediately, exactly like the inner dispatcher alone.</summary>
    None,

    /// <summary>
    /// Blocks before ever calling the inner dispatcher — manufactures the "intent recorded, no
    /// execution trace" crash window against a real dispatch: by the time this decorator is
    /// invoked, <c>ExecutionRequestAccepted</c> is already fsync'd (<c>MutationInterface</c>
    /// appends and awaits it before starting any dispatch), so killing the host while blocked here
    /// leaves that write durable with no Core process ever having been spawned for it.
    /// </summary>
    BeforeDispatch,

    /// <summary>
    /// Calls the inner dispatcher through to completion — a real process really spawns, runs, and
    /// exits, and <see cref="CoreDispatcher.DispatchAsync"/> does not return until both
    /// <c>ExecutionStarted</c> and <c>ExecutionExited</c> are durably appended — then blocks before
    /// returning to the caller. Manufactures the "ran while Flow was down" window: killing the host
    /// while blocked here leaves a durable exit recorded with no Flow-side outcome ever appended.
    /// </summary>
    AfterDispatch,
}

/// <summary>
/// Test-only <see cref="ICoreDispatcher"/> decorator (M10 Phase 4, issue #72) that lets
/// <see cref="Program"/> hold a real dispatch open at an exact, named point in its lifecycle — by
/// polling for a sentinel file's existence rather than ever actually reaching it — so the test
/// harness can kill this process while genuinely, deterministically inside that window, instead of
/// racing real wall-clock timing against a sub-millisecond write. Never shipped: production
/// <c>Aer.Flow</c> never sees this type.
/// </summary>
public sealed class PausableCoreDispatcher(ICoreDispatcher inner, DispatchPausePoint pausePoint, string signalFilePath)
    : ICoreDispatcher
{
    public async Task<CoreDispatchResult> DispatchAsync(
        ExecutionRequest request, CoreDispatchTarget target, CancellationToken cancellationToken = default)
    {
        if (pausePoint == DispatchPausePoint.BeforeDispatch)
        {
            await WaitForSignalAsync().ConfigureAwait(false);
        }

        var result = await inner.DispatchAsync(request, target, cancellationToken).ConfigureAwait(false);

        if (pausePoint == DispatchPausePoint.AfterDispatch)
        {
            await WaitForSignalAsync().ConfigureAwait(false);
        }

        return result;
    }

    // Deliberately CancellationToken.None: this process is meant to be killed outright while
    // paused, never gracefully cancelled, so there is nothing for this loop to observe or react to.
    private async Task WaitForSignalAsync()
    {
        while (!File.Exists(signalFilePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }
}
