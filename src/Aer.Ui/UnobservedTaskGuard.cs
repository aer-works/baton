namespace Aer.Ui;

/// <summary>
/// The half of #1176's app-level guard that has nothing to do with Avalonia: a faulted Task nobody
/// awaited (<c>_ = SomethingAsync()</c>, 47 sites in this app), whose exception reaches no
/// synchronization context and is dropped by the runtime — not a crash, a silence, which the
/// error-handling rules forbid just as firmly.
/// </summary>
/// <remarks>
/// <para>
/// It lives apart from <see cref="App"/> because of what its test has to do: the event only fires
/// when the faulted Task is <b>finalized</b>, so proving it means forcing a collection, and forcing
/// one inside Avalonia's shared headless test session destabilised that session — locally green,
/// intermittently red on Windows CI with "The calling thread cannot access this object because a
/// different thread owns it" raised while the session re-initialised (#1200). Separating the
/// mechanism from the UI lets the fact run with no UI session at all, which is also the honest
/// scope: nothing here needs a window.
/// </para>
/// <para>
/// What it is not: prompt (the event arrives at collection, not at the failure) and not a crash
/// averted (an unobserved Task fault has terminated nothing since .NET 4.5). Its whole job is that
/// the failure gets written down instead of vanishing. One shape stays uncovered — the event is
/// raised for a <b>faulted</b> Task only, so a discard that ends <c>Canceled</c> is as silent as it
/// was before this existed.
/// </para>
/// </remarks>
internal static class UnobservedTaskGuard
{
    private static readonly Lock RegistrationLock = new();
    private static Action<Exception>? onSurface;
    private static bool subscribed;

    /// <param name="surface">
    /// Called with the first inner exception, for whatever shows it to a person. Invoked on the
    /// finalizer thread — the caller marshals. The durable write happens before this and does not
    /// depend on it.
    /// </param>
    /// <remarks>
    /// Surfaces accumulate rather than replace, and the <see cref="TaskScheduler"/> subscription is
    /// made once for all of them. The app registers exactly one, so this costs it nothing; what it
    /// buys is that a second registration cannot silently take the first one's place, and that
    /// unregistering one does not tear down another's. A last-writer-wins slot was a real defect
    /// under test, where whichever fact happened to build <c>App</c> next took the slot out from
    /// under the one running (#1200) — and a plain <c>+=</c> on the event subscribed the same
    /// handler twice, which logged every fault twice.
    /// </remarks>
    internal static void Register(Action<Exception> surface)
    {
        lock (RegistrationLock)
        {
            onSurface += surface;
            if (!subscribed)
            {
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                subscribed = true;
            }
        }
    }

    internal static void Unregister(Action<Exception> surface)
    {
        lock (RegistrationLock)
        {
            onSurface -= surface;
            if (onSurface is null && subscribed)
            {
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                subscribed = false;
            }
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();

        if (e.Exception is not { } aggregate)
        {
            return;
        }

        // The whole aggregate goes to the sink, not its first inner exception: a discarded
        // Task.WhenAll can carry several faults, and AggregateException.InnerException is only
        // InnerExceptions[0] — writing that alone would under-report while looking complete.
        AppUnhandledExceptionSink.LogException(aggregate);

        // This runs on a finalizer thread, where an escaping exception ends the process — the one
        // outcome this guard exists to prevent. The durable write above is unconditional; the
        // screen half is best-effort and caught.
        try
        {
            onSurface?.Invoke(aggregate.InnerExceptions.Count > 0 ? aggregate.InnerExceptions[0] : aggregate);
        }
        catch (Exception surfaceFailure)
        {
            AppUnhandledExceptionSink.LogException(surfaceFailure);
        }
    }
}
