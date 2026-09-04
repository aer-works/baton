using Baton.Dispatch;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1680's first-verdict canary, read side: <see cref="AgyHookVerdictLedger.CountVerdicts"/> against
/// the file <see cref="AgyHookCheckCommand"/>-equivalent writers append one line to per verdict (the
/// write side lives in <c>Baton.Cli.AgyHookCheckCommandTests</c>, the other side of the mirror
/// contract this class cannot reference directly).
/// </summary>
public class AgyHookVerdictLedgerTests
{
    [Fact]
    public void A_missing_ledger_file_counts_zero_verdicts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agy-hook-verdicts-missing-{Guid.NewGuid():N}.ndjson");

        Assert.False(File.Exists(path));
        Assert.Equal(0, AgyHookVerdictLedger.CountVerdicts(path));
    }

    [Fact]
    public void A_null_or_blank_path_counts_zero_verdicts()
    {
        Assert.Equal(0, AgyHookVerdictLedger.CountVerdicts(null));
        Assert.Equal(0, AgyHookVerdictLedger.CountVerdicts(""));
        Assert.Equal(0, AgyHookVerdictLedger.CountVerdicts("   "));
    }

    [Fact]
    public void Counts_one_line_per_verdict()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agy-hook-verdicts-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllLines(path, ["2026-01-01T00:00:00Z", "2026-01-01T00:00:01Z", "2026-01-01T00:00:02Z"]);

            Assert.Equal(3, AgyHookVerdictLedger.CountVerdicts(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void A_trailing_blank_line_is_not_counted_as_a_verdict()
    {
        // Guards against a torn final write (the process killed mid-append) inflating the count --
        // see the class doc comment's fail-closed-undercount reasoning.
        var path = Path.Combine(Path.GetTempPath(), $"agy-hook-verdicts-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllText(path, "2026-01-01T00:00:00Z\n\n");

            Assert.Equal(1, AgyHookVerdictLedger.CountVerdicts(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void A_torn_partial_final_line_is_counted_not_skipped()
    {
        // #1732 review: unlike a *blank* trailing line above, a torn *partial* write (the process
        // killed mid-append, leaving a non-empty fragment) IS counted -- the class doc used to claim
        // an undercount here, which this pins as false. Correct, not merely harmless: the fragment
        // cannot exist unless the verdict that produced it was already reached.
        var path = Path.Combine(Path.GetTempPath(), $"agy-hook-verdicts-{Guid.NewGuid():N}.ndjson");
        try
        {
            File.WriteAllText(path, "2026-01-01T00:00:00Z\n2026-01-0");

            Assert.Equal(2, AgyHookVerdictLedger.CountVerdicts(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1760: the live-dispatch entry point (<see cref="AgyHookVerdictLedger.CountVerdicts"/>) and the
    /// crash-recovery replay entry point (<see cref="HookVerdictLedger.CountLines"/>, which
    /// <c>MutationInterface</c> now calls directly) must never disagree, because both count from the
    /// same primitive. Before the dedupe the two readers already agreed on every one of these
    /// fixtures -- this pins that fact rather than a fix for a discovered divergence.
    /// </summary>
    private const string MissingFileSentinel = "<missing-file>";

    public static IEnumerable<object[]> LedgerFixtures()
    {
        yield return ["missing file", MissingFileSentinel];
        yield return ["empty file", ""];
        yield return ["one line, no trailing newline", "verdict-1"];
        yield return ["one line with trailing newline", "verdict-1\n"];
        yield return ["several lines with trailing newline", "verdict-1\nverdict-2\nverdict-3\n"];
        yield return ["partial trailing line", "verdict-1\nverdict-"];
        yield return ["CRLF line endings", "verdict-1\r\nverdict-2\r\nverdict-3\r\n"];
        yield return ["blank interior lines", "verdict-1\n\n\nverdict-2\n"];
        yield return ["blank file (whitespace only)", "   \n\t\n"];
    }

    [Theory]
    [MemberData(nameof(LedgerFixtures))]
    public void The_live_and_replay_entry_points_count_every_fixture_identically(string _, string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agy-hook-verdicts-fixture-{Guid.NewGuid():N}.ndjson");
        try
        {
            if (contents != MissingFileSentinel)
            {
                File.WriteAllText(path, contents);
            }

            var liveCount = AgyHookVerdictLedger.CountVerdicts(path);
            var replayCount = HookVerdictLedger.CountLines(path);

            Assert.Equal(liveCount, replayCount);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
