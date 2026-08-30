namespace Baton.Tests.Shared;

/// <summary>
/// Shared retry core behind the test-cleanup helpers (<see cref="FileCleanup"/> and
/// <see cref="DirectoryCleanup"/>) — and for any other test-side filesystem operation racing the
/// same scanner hold, e.g. a mid-test <c>Directory.Move</c> (#1014). On Windows, Defender (or the search indexer) intermittently holds
/// a brief exclusive handle on a just-written file/directory while scanning it, so a delete that runs
/// immediately after a test writes its fixtures surfaces as <see cref="IOException"/> ("being used by
/// another process") or <see cref="UnauthorizedAccessException"/> (issue #295). A short bounded
/// backoff clears the transient case; a target already gone is treated as success.
/// <para>
/// <paramref name="swallowOnFinal"/> is the one axis that differs between the two call contexts:
/// a <b>teardown</b> delete lives in a <c>finally</c> block, so a persistent lock must not surface there
/// (it would mask the test's real result, or fail a passing test) — a leftover uniquely-named temp file
/// costs nothing. A <b>setup</b> delete must instead surface a persistent failure loudly, because a
/// stale file left in place corrupts the test's premise and can make it pass for the wrong reason. Note
/// the swallow covers only the transient-lock exception types below (<see cref="IOException"/> /
/// <see cref="UnauthorizedAccessException"/>); a programming error like a malformed path
/// (<see cref="NotSupportedException"/>) still propagates from either path, by design.
/// </para>
/// </summary>
internal static class CleanupRetry
{
    // ~1s of headroom (10 attempts x 100ms). Covers the brief Defender/indexer hold, plus the wider
    // overlapped-I/O completion window that SnapshotBinderTests' race-test cleanup documented needing
    // on Windows before it was folded in here (#918). Exceeding it only risks a harmless leftover temp
    // file (teardown swallows), so the budget errs generous rather than tight.
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static void Run(Action delete, bool swallowOnFinal)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return; // already gone — never the transient-lock race, so no point retrying it
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (Exception ex) when (swallowOnFinal && ex is IOException or UnauthorizedAccessException)
            {
                return; // best-effort teardown: a persistent lock must not mask the test's real outcome
            }
            // Setup path (swallowOnFinal == false): the final-attempt exception propagates.
        }
    }
}
