namespace Baton.Flow.Store;

/// <summary>
/// Wall-clock bounded retry loop around <see cref="File.Move(string, string, bool)"/> for atomic-move sites.
/// Mirrors the pattern in <see cref="T:Baton.Vendors.AtomicLaunchConfigWriter"/>.
/// </summary>
public static class RetryingFileMove
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);
    private const double MaxBackoffMs = 250;

    /// <summary>
    /// Moves a file from <paramref name="source"/> to <paramref name="destination"/>, retrying on transient
    /// sharing violations (<see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>)
    /// until <paramref name="budget"/> expires.
    /// </summary>
    /// <param name="deleteSourceOnFinalFailure">Opt-in for temp-then-move sites whose source is a
    /// disposable temp file: when the budget expires, best-effort delete the source so repeated
    /// failures cannot accumulate uniquely-named orphans (second-reader finding on #985). Stays
    /// false by default — a caller like log rollover moves a REAL file, which a failure must never
    /// delete.</param>
    public static void Move(
        string source,
        string destination,
        bool overwrite = false,
        TimeSpan? budget = null,
        bool deleteSourceOnFinalFailure = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var actualBudget = budget ?? DefaultBudget;
        var deadlineTicks = Environment.TickCount64 + (long)actualBudget.TotalMilliseconds;
        var backoffMs = 10.0;

        while (true)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadlineTicks)
                {
                    if (deleteSourceOnFinalFailure)
                    {
                        try
                        {
                            File.Delete(source);
                        }
                        catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                        {
                            // Best-effort only: losing the cleanup must never mask the move failure.
                        }
                    }

                    throw;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(backoffMs));
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
        }
    }

    /// <summary>
    /// Moves a directory from <paramref name="source"/> to <paramref name="destination"/>, retrying on transient
    /// sharing violations (<see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>)
    /// until <paramref name="budget"/> expires.
    /// Creates destination's parent directory if it does not already exist.
    /// </summary>
    public static void MoveDirectory(
        string source,
        string destination,
        TimeSpan? budget = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var actualBudget = budget ?? DefaultBudget;
        var deadlineTicks = Environment.TickCount64 + (long)actualBudget.TotalMilliseconds;
        var backoffMs = 10.0;

        while (true)
        {
            try
            {
                var parentDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadlineTicks)
                {
                    throw;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(backoffMs));
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
        }
    }
}



