namespace Baton.Status;

/// <summary>
/// The named-<see cref="Mutex"/> critical-section primitive <c>RoomRegistryStore</c> originated
/// (spec/baton.md §8) — extracted here so <c>QuotaLedgerStore</c> (spec/baton.md §7, issue #1570) can
/// share it rather than copying it; a second caller is what turns this from "the registry's private
/// helper" into a real seam. Every process touching the same file — reader or writer — serializes
/// against every other one, keyed on <paramref name="filePath"/> and <paramref name="lockNamePrefix"/>
/// (distinct prefixes for distinct files mean two stores can never contend on each other's locks even
/// if two file paths happened to collide, which <see cref="BatonPaths.RecordKey"/> already makes
/// vanishingly unlikely on its own).
/// </summary>
/// <remarks>
/// <b>Acquire, the caller's action, and release all happen synchronously on one thread.</b>
/// <see cref="Mutex"/> ownership is thread-affine on Windows: the OS thread that calls
/// <see cref="Mutex.WaitOne()"/> is the only one allowed to call <see cref="Mutex.ReleaseMutex"/>. An
/// <c>await</c> between acquiring and releasing can resume on a different thread-pool thread with no
/// synchronization context to pin it, which makes <see cref="Mutex.ReleaseMutex"/> throw. Callers must
/// wrap this in one <c>Task.Run</c> from the outside (moving the whole synchronous call to a worker
/// thread) rather than making <paramref name="action"/> itself <c>async</c>.
/// <para>
/// The lock name is built from a SHA-256 digest of <paramref name="filePath"/> (normalized through
/// <see cref="BatonPaths.RecordKey"/> and upper-invariant, so two spellings of the same file — forward
/// vs. backward slashes, a different <c>BATON_HOME</c> casing — hash to the one mutex name) rather than
/// the raw path, because a raw path is neither a valid nor a safely short Windows kernel-object name.
/// <b>Changing this digest formula, the <c>Global\</c> prefix, or an existing caller's
/// <paramref name="lockNamePrefix"/> renames the lock</b> — an older <c>baton</c> build and a newer one
/// (side-by-side per-commit installs, #1668) would then take out two different mutexes against the same
/// file, which is exactly the loss this primitive exists to prevent.
/// </para>
/// </remarks>
public static class MutexGuardedFileLock
{
    /// <summary>
    /// The exact kernel-object name <see cref="RunUnderLock{T}"/> takes out for
    /// <paramref name="filePath"/>/<paramref name="lockNamePrefix"/> — exposed so a test can pin the
    /// literal format against an independently-computed expectation, not for any production caller to
    /// build a <see cref="Mutex"/> of its own.
    /// </summary>
    internal static string BuildMutexName(string filePath, string lockNamePrefix)
    {
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(BatonPaths.RecordKey(filePath).ToUpperInvariant())));
        return $"Global\\{lockNamePrefix}-{digest}";
    }

    /// <summary>
    /// Runs <paramref name="action"/> holding a named <see cref="Mutex"/> keyed on
    /// <paramref name="filePath"/> and <paramref name="lockNamePrefix"/>. Throws
    /// <see cref="IOException"/> if <paramref name="lockTimeout"/> elapses before the lock is
    /// acquired, and lets <see cref="WaitHandleCannotBeOpenedException"/> (a non-mutex kernel object
    /// already holding the name) propagate — both are the caller's fail-open contract to honour, not
    /// this primitive's to swallow.
    /// </summary>
    public static T RunUnderLock<T>(string filePath, string lockNamePrefix, TimeSpan lockTimeout, Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, name: BuildMutexName(filePath, lockNamePrefix));

        bool owned;
        try
        {
            owned = mutex.WaitOne(lockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A prior holder crashed mid-access. Per Mutex's own contract, ownership still transfers
            // to us when this is thrown -- whatever partial state it left behind is each caller's own
            // tolerant, skip-malformed-lines read path to handle, not something to react to here.
            owned = true;
        }

        if (!owned)
        {
            throw new IOException($"Timed out after {lockTimeout} waiting for the '{lockNamePrefix}' lock on '{filePath}'.");
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Action-returning overload of <see cref="RunUnderLock{T}"/>.</summary>
    public static void RunUnderLock(string filePath, string lockNamePrefix, TimeSpan lockTimeout, Action action) =>
        RunUnderLock<object?>(filePath, lockNamePrefix, lockTimeout, () =>
        {
            action();
            return null;
        });
}
