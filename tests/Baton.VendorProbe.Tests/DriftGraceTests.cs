using System.Text.Json;

namespace Baton.VendorProbe.Tests;

/// <summary>
/// #1487: the grace window that stops a self-updated vendor CLI hard-failing <c>gates</c> the instant
/// it is noticed. Pure-function tests against a throwaway bookkeeping file — no <c>Cli.Invoke</c>, no
/// vendor process, safe in CI, and each test gets its own temp path so they cannot interfere.
/// </summary>
public sealed class DriftGraceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aer-drift-grace-tests-").FullName;

    private string BookkeepingPath => Path.Combine(_dir, "drift.local.json");

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_dir);

    [Fact]
    public void No_drift_with_no_bookkeeping_is_a_clean_pass()
    {
        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: false, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.NoDrift, result.Verdict);
        Assert.False(result.Fatal);
        Assert.False(File.Exists(BookkeepingPath));
    }

    [Fact]
    public void Fresh_drift_records_the_instant_warns_and_passes()
    {
        var now = DateTimeOffset.Now;

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, now);

        Assert.Equal(DriftGrace.Verdict.FreshWarn, result.Verdict);
        Assert.False(result.Fatal);
        Assert.Contains("grace window", result.Message);
        Assert.True(File.Exists(BookkeepingPath), "first detection must record a bookkeeping file");

        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.NotNull(recorded);
        Assert.Equal(now, recorded!.FirstDetectedAt);
    }

    [Fact]
    public void Drift_still_within_the_window_on_a_later_run_keeps_the_original_instant_and_still_passes()
    {
        var firstSeen = DateTimeOffset.Now.AddDays(-3);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(firstSeen)));

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.FreshWarn, result.Verdict);
        Assert.False(result.Fatal);

        // The instant must not have moved -- a re-run within the window is not a second "first seen".
        var recorded = JsonSerializer.Deserialize<DriftGrace.Bookkeeping>(File.ReadAllText(BookkeepingPath));
        Assert.Equal(firstSeen, recorded!.FirstDetectedAt);
    }

    [Fact]
    public void Drift_past_the_grace_window_hard_fails()
    {
        var firstSeen = DateTimeOffset.Now - DriftGrace.Window - TimeSpan.FromHours(1);
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(firstSeen)));

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.StaleFail, result.Verdict);
        Assert.True(result.Fatal);
        Assert.Contains("grace window", result.Message);
    }

    [Fact]
    public void Corrupt_bookkeeping_fails_closed_rather_than_reopening_the_window()
    {
        File.WriteAllText(BookkeepingPath, "{ not valid json");

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.CorruptFail, result.Verdict);
        Assert.True(result.Fatal);

        // Failing closed also means it must not have overwritten the corrupt file with a fresh
        // "detected now" record -- that would silently convert unreadable bookkeeping into a clean
        // restart of the clock, which is exactly the failure mode this test guards against.
        Assert.Equal("{ not valid json", File.ReadAllText(BookkeepingPath));
    }

    [Fact]
    public void Empty_bookkeeping_file_also_fails_closed()
    {
        File.WriteAllText(BookkeepingPath, string.Empty);

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: true, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.CorruptFail, result.Verdict);
        Assert.True(result.Fatal);
    }

    [Fact]
    public void Cleared_drift_deletes_the_bookkeeping_file()
    {
        File.WriteAllText(
            BookkeepingPath,
            JsonSerializer.Serialize(new DriftGrace.Bookkeeping(DateTimeOffset.Now.AddDays(-2))));

        var result = DriftGrace.Evaluate(BookkeepingPath, driftDetected: false, DateTimeOffset.Now);

        Assert.Equal(DriftGrace.Verdict.NoDrift, result.Verdict);
        Assert.False(result.Fatal);
        Assert.False(File.Exists(BookkeepingPath), "a re-pinned probe must clear the recorded drift instant");
    }
}
