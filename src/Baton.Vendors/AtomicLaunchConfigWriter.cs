namespace Baton.Vendors;

/// <summary>
/// Writes an AER-owned worker launch-configuration file (claude's <c>claude-settings.json</c>,
/// agy's <c>.agents/hooks.json</c>) so that a worker starting mid-write never reads a torn file.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="ClaudeWorkerAdapter"/> by #554, when
/// <see cref="AgyWorkerAdapter"/> needed the same guarantee. It was first written as a local
/// copy whose doc comment claimed to "mirror" the claude implementation and did not: it derived its
/// temp name from <see cref="Environment.ProcessId"/> — constant for the process, so two concurrent
/// writers in one process collide on the same temp path — and carried no retry. Caught by an
/// independent reviewer. Shared rather than duplicated so the claim is structural instead of
/// aspirational; a second copy of a concurrency-sensitive writer is exactly the shape that drifts.
/// </para>
/// <para>
/// <b>Why the temp-plus-rename at all:</b> <see cref="File.Move(string, string, bool)"/>'s overwrite
/// is a same-volume rename and therefore atomic on both Windows and POSIX, which a direct
/// <see cref="File.WriteAllText(string, string)"/> onto the final path does not guarantee when two
/// callers race to rewrite it. On agy the stakes are specific: an unparseable
/// <c>hooks.json</c> is not an error but a **silently ungated worker**
/// (<c>agy.hook-malformed-stdout-fails-open</c> measured that a hook producing nothing is read as an
/// allow), so a torn read is a permission failure rather than a cosmetic one.
/// </para>
/// <para>
/// <b>Why the retry:</b> the rename itself can still collide. Two chat sessions starting their first
/// turn from the same daemon process is a genuine, expected race (#533). Measured under
/// #543's own parallel test run: a concurrent <see cref="File.Move(string, string, bool)"/> onto the
/// same destination throws <see cref="UnauthorizedAccessException"/> on Windows — a transient
/// sharing violation, not a real permissions problem — while another thread's move or read briefly
/// holds the destination open.
/// </para>
/// <para>
/// Retrying is correct here rather than papering over a disagreement: every racing writer in one
/// process produces byte-identical content (a deterministic function of
/// <see cref="AppContext.BaseDirectory"/>, constant for the process's lifetime), so whichever
/// attempt wins, the file ends up holding the one content every writer wanted anyway.
/// </para>
/// <para>
/// <b>Skipped when the content already matches (#667):</b> the same determinism makes a rewrite on
/// every resolve pure contention. The reader it costs is the vendor CLI, which opens
/// <c>--settings</c> once at spawn with no retry; before the skip, 4239 of 424091 unretried reads
/// failed a sharing violation under four concurrent resolvers.
/// </para>
/// <para>
/// <b>A losing rename is not a losing writer (#682):</b> the first resolve against a fresh or
/// drifted file still writes, and enough concurrent cold-start writers exhausted the retry budget
/// before every failed rename re-checked <see cref="AlreadyHolds"/> -- the content-identity argument
/// above applies to the loser too, not only to the skip. Measured on the same platform, and under
/// the same "Why the retry" sharing violation, as the paragraph above. That fix narrowed the window
/// without closing it -- the probe read can lose the same race on every re-check -- and #840 measured
/// exactly that; the post-exhaustion settle loop in <see cref="Write"/> is what finally closes it
/// for a file that becomes readable at all.
/// </para>
/// <para>
/// <b>The budget is wall-clock, not attempt-count:</b> a fixed attempt count with per-attempt backoff
/// burns its whole budget in far less wall-clock time than a foreign holder (OS Search indexer, AV
/// scanner) needs to release, and under full-suite CPU starvation the attempts themselves are starved
/// -- so the count exhausts while barely any real time has passed. Bounding the retry (and the settle
/// window) by elapsed time keeps retrying for a real interval regardless of scheduling. This is the
/// anti-whack-a-mole form of the old "raising the attempt count only moves the threshold" note: the
/// deadline removes the threshold rather than relocating it.
/// </para>
/// </remarks>
internal static class AtomicLaunchConfigWriter
{
    /// <summary>
    /// Wall-clock retry budget for the rename, bounded by elapsed time rather than a fixed attempt
    /// count: under full-suite CPU starvation a small attempt count with per-attempt backoff burns its
    /// whole budget in far less wall-clock time than a foreign holder needs to release, so a deadline
    /// keeps retrying for a real interval however scheduling starves the attempts. The happy path (an
    /// <see cref="AlreadyHolds"/> skip or a first-attempt rename) never pays it.
    /// </summary>
    private static readonly TimeSpan DefaultRetryBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Post-exhaustion settle window (#840): after the retry budget is spent, the herd member that lost
    /// every rename AND every probe read waits for the winners' renames to drain and reads again. Only
    /// the path that previously threw outright pays it; the happy path never does. Wall-clock, for the
    /// same reason as the retry budget.
    /// </summary>
    private static readonly TimeSpan DefaultSettleBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>Per-attempt backoff ceiling, so even a long budget keeps its retries frequent.</summary>
    private const double MaxBackoffMs = 250;

    public static void Write(string path, string content)
        => Write(path, content, DefaultRetryBudget, DefaultSettleBudget);

    /// <summary>
    /// The <see cref="Write(string, string)"/> overload that takes explicit budgets. Kept internal only
    /// for the failure-injection tests, which pass tiny values to reach the settle-then-rethrow path
    /// without waiting out the production defaults the public overload supplies.
    /// </summary>
    internal static void Write(string path, string content, TimeSpan retryBudget, TimeSpan settleBudget)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        if (AlreadyHolds(path, content))
        {
            return;
        }

        // Wall-clock bounded (not attempt-count): a zero budget still makes exactly one rename attempt,
        // because the deadline is only checked after a failure.
        var deadlineTicks = Environment.TickCount64 + (long)retryBudget.TotalMilliseconds;
        var backoffMs = 10.0;
        while (true)
        {
            // Unique per attempt, never per process: a process-keyed name makes two concurrent
            // writers in one process race for the same temp file, which is the defect this
            // extraction fixed.
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, content);
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: TryDeleteTemp can never throw, so it can never replace or mask the
                // exception this catch is handling -- a leftover .tmp file is a far smaller problem
                // than losing the reason a retry was needed.
                TryDeleteTemp(tempPath);

                // #682: a losing rename did not need to win -- if some other writer's identical
                // content already landed, this attempt's goal is already satisfied, regardless of how
                // much budget remains. Checked on every failure, not only at exhaustion. AlreadyHolds'
                // own read can lose the same sharing-violation race (its catch returns false); the
                // settle loop below is what catches the herd member that loses it on every attempt (#840).
                if (AlreadyHolds(path, content))
                {
                    return;
                }

                if (Environment.TickCount64 >= deadlineTicks)
                {
                    // #840: the herd member that lost every rename AND every probe read still has one
                    // honest way to be satisfied -- wait for the winners' renames to drain and read
                    // again. Bounded and loud: a file that never becomes readable, or holds someone
                    // else's content, still surfaces the original exception.
                    var settleDeadlineTicks = Environment.TickCount64 + (long)settleBudget.TotalMilliseconds;
                    while (Environment.TickCount64 < settleDeadlineTicks)
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(20));
                        if (AlreadyHolds(path, content))
                        {
                            return;
                        }
                    }

                    throw;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(backoffMs));
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
            catch
            {
                TryDeleteTemp(tempPath);
                throw;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> already holds exactly <paramref name="content"/>, so there
    /// is nothing to write.
    /// </summary>
    /// <remarks>
    /// Content, not existence: #543 reversed "never overwrite" so that a stale or tampered file cannot
    /// stay installed with the gate silently off, and comparing content keeps that.
    /// <para>
    /// <b>Unreadable counts as differing</b> -- the cost of a redundant write is contention, the cost
    /// of a skipped one is an ungated worker. Deliberately not a blanket catch: a worker can write
    /// this file through its own <c>--add-dir</c> grant, so a pathologically large one escapes as
    /// <see cref="OutOfMemoryException"/>. Left loud rather than guarded by a guessed size threshold.
    /// </para>
    /// </remarks>
    private static bool AlreadyHolds(string path, string content)
    {
        try
        {
            return File.Exists(path)
                && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch
        {
            // Best-effort cleanup only -- see this type's own remarks for why a failed delete here
            // must never surface in place of the real exception.
        }
    }
}
