namespace Baton.Concurrency;

/// <summary>
/// #1296: caps how many session turns dispatch to a vendor CLI at once — global and per-vendor —
/// and FIFO-queues the rest in memory, deliberately not durable like <see cref="ConcurrencyGuard"/>.
/// The rationale (why in-memory, why the numbers are a guess, why a restart dropping the queue is
/// correct) lives in decisions/0020's 2026-08-16 amendment, not restated here.
/// <para>
/// This type's state is process-static rather than per-instance because, until #1412 archived it,
/// desktop hosted the daemon in-process (<c>Baton.Ui</c> referenced <c>Baton.Daemon</c> directly) and
/// needed to share it — the same reason <see cref="ConcurrencyGuard.IsHeld"/> is safe to probe.
/// </para>
/// </summary>
public static class ConcurrencySlotGate
{
    public const int DefaultGlobalCap = 3;
    public const int DefaultPerVendorCap = 2;

    private static readonly object Lock = new();
    private static int _globalCap = DefaultGlobalCap;
    private static int _perVendorCap = DefaultPerVendorCap;
    private static int _globalActive;
    private static readonly Dictionary<string, int> VendorActive = new();
    private static readonly List<Waiter> Queue = [];

    public static int GlobalCap
    {
        get { lock (Lock) { return _globalCap; } }
    }

    public static int PerVendorCap
    {
        get { lock (Lock) { return _perVendorCap; } }
    }

    /// <summary>
    /// Sets the caps a fresh <c>AcquireAsync</c> reserves against (#1298, settings-driven). Takes
    /// effect immediately for slots not yet reserved -- an in-flight active slot is never revoked, so
    /// shrinking a cap below the current active count is accepted and simply stops new reservations
    /// until enough slots release naturally. Raising a cap immediately dispatches any queued waiters
    /// it now has room for.
    /// </summary>
    public static void SetCaps(int globalCap, int perVendorCap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(globalCap, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perVendorCap, 1);

        lock (Lock)
        {
            _globalCap = globalCap;
            _perVendorCap = perVendorCap;
            DispatchWaiters();
        }
    }

    private sealed class Waiter
    {
        public required string DirectoryPath { get; init; }
        public required string Vendor { get; init; }
        public required TaskCompletionSource Tcs { get; init; }
    }

    /// <summary>True while <paramref name="directoryPath"/> sits FIFO-queued behind the cap — originally the input Baton.RoomSession's <c>RoomCardViewModel.DeriveStatus</c> needed to derive <c>RoomCardStatus.WaitingToStart</c> (that project is deleted, #1420; this project never referenced it, hence plain text here rather than a cref).</summary>
    public static bool IsWaiting(string directoryPath)
    {
        lock (Lock)
        {
            return Queue.Exists(w => w.DirectoryPath == directoryPath);
        }
    }

    /// <summary>
    /// Awaits a global+per-vendor slot for <paramref name="directoryPath"/>, queueing FIFO if none is
    /// free. Dispose the returned slot when the turn finishes to release it (and dispatch the next
    /// waiter, if any).
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(string directoryPath, string vendor)
    {
        TaskCompletionSource? tcs;
        lock (Lock)
        {
            if (TryReserve(vendor))
            {
                return new Slot(vendor);
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add(new Waiter { DirectoryPath = directoryPath, Vendor = vendor, Tcs = tcs });
        }

        await tcs.Task.ConfigureAwait(false);
        return new Slot(vendor);
    }

    /// <summary>
    /// Removes <paramref name="directoryPath"/> from the wait queue if it is still there (a cancel or
    /// stop reaching a room before its turn ever got a slot) — never removes an already-dispatched
    /// turn, since by then it is no longer in <see cref="Queue"/>. Returns whether it was queued.
    /// </summary>
    public static bool CancelWaiting(string directoryPath)
    {
        lock (Lock)
        {
            var index = Queue.FindIndex(w => w.DirectoryPath == directoryPath);
            if (index < 0)
            {
                return false;
            }

            var waiter = Queue[index];
            Queue.RemoveAt(index);
            waiter.Tcs.TrySetCanceled();
            return true;
        }
    }

    /// <summary>Caller must hold <see cref="Lock"/>.</summary>
    private static bool TryReserve(string vendor)
    {
        if (_globalActive >= _globalCap)
        {
            return false;
        }

        var vendorCount = VendorActive.GetValueOrDefault(vendor);
        if (vendorCount >= _perVendorCap)
        {
            return false;
        }

        _globalActive++;
        VendorActive[vendor] = vendorCount + 1;
        return true;
    }

    private static void Release(string vendor)
    {
        lock (Lock)
        {
            _globalActive--;
            VendorActive[vendor] = VendorActive.GetValueOrDefault(vendor) - 1;
            DispatchWaiters();
        }
    }

    /// <summary>
    /// Caller must hold <see cref="Lock"/>. Walks the FIFO queue front-to-back, dispatching every
    /// waiter whose vendor+global slots are currently free — a later waiter of a different vendor can
    /// skip ahead of a head blocked purely on its own vendor's cap, so one vendor's queue never
    /// head-of-line-blocks another vendor's.
    /// </summary>
    private static void DispatchWaiters()
    {
        for (var i = 0; i < Queue.Count; i++)
        {
            var waiter = Queue[i];
            if (!TryReserve(waiter.Vendor))
            {
                continue;
            }

            Queue.RemoveAt(i);
            i--;
            waiter.Tcs.TrySetResult();
        }
    }

    /// <summary>Test-only: drops all active counts and queued waiters so tests don't leak state across each other (this type's counters are static/process-wide, same caveat as any other static gate in this codebase).</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            _globalCap = DefaultGlobalCap;
            _perVendorCap = DefaultPerVendorCap;
            _globalActive = 0;
            VendorActive.Clear();
            foreach (var waiter in Queue)
            {
                waiter.Tcs.TrySetCanceled();
            }

            Queue.Clear();
        }
    }

    private sealed class Slot(string vendor) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Release(vendor);
        }
    }
}
