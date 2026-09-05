using System.Text.Json;
using Baton.Cli.Mcp;
using Baton.Cli.Tests.TestSupport;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1848's read side: dispatch reads the snapshot the daemon harvested (#1391/#1869) and never runs a
/// vendor CLI itself. Every failure mode returns null, which <c>RunwayGate</c> turns into a Hold —
/// returning an empty snapshot instead would read as 0% used, i.e. unlimited headroom.
/// </summary>
public class RunwaySnapshotReaderTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baton-usage-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_harvested_snapshot_round_trips_back_into_the_gate_shape()
    {
        var path = TempPath();
        try
        {
            var harvested = ClaudeUsageSlashCommandSource.Parse(
                "Current session: 12% used\nCurrent week (all models): 40% used\n", DateTimeOffset.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(new PersistedVendorUsage(
                harvested.Vendor, harvested.HarvestedAt, harvested.Caveat, harvested.Windows, Rings: null)));

            var read = RunwaySnapshotReader.ReadFrom(path);

            Assert.NotNull(read);
            Assert.Equal("claude", read.Vendor);
            Assert.Equal(40, read.Windows.Single(w => w.Name == "week (all models)").PercentUsed);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_reads_as_null()
    {
        Assert.Null(RunwaySnapshotReader.ReadFrom(TempPath()));
    }

    [Fact]
    public void A_corrupt_file_reads_as_null_rather_than_as_an_empty_snapshot()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ half a snapshot");

            Assert.Null(RunwaySnapshotReader.ReadFrom(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
