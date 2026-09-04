using System.Text.Json;
using Baton.Cli.Daemon;
using Baton.Status;
using Baton.Vendors;
using Xunit;

namespace Baton.Cli.Tests.Daemon;

/// <summary>
/// Covers <see cref="VendorUsageHarvester"/>'s per-tick decision through its internal test seam —
/// which #1869's review found had no caller at all, so the path that seam exists to reach (named on
/// <c>TickOnceAsync</c>'s own null-skip comment) had no red arm. Both halves are faked (the room scan and the vendor
/// source) so no process is spawned and no real room is fabricated; the cadence rules themselves are
/// <see cref="VendorUsageHarvestSchedulerTests"/>'s, not restated here.
/// </summary>
public sealed class VendorUsageHarvesterTests : IDisposable
{
    private readonly string _tempHome;
    private readonly IDisposable _scope;

    public VendorUsageHarvesterTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"baton-harvester-test-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = _tempHome });
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_tempHome))
        {
            DirectoryCleanup.DeleteRecursively(_tempHome);
        }
    }

    /// <summary>Every interval zero, so the very first tick with a live lane is due to harvest.</summary>
    private static VendorUsageHarvestScheduler AlwaysDueScheduler() =>
        new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, jitterSource: () => 0);

    private sealed class FakeSource(string vendor, VendorUsageSnapshot? result) : IVendorUsageSource
    {
        public string Vendor => vendor;

        public int Reads { get; private set; }

        public Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(result);
        }
    }

    private static VendorUsageSnapshot FreshSnapshot() => new(
        "claude",
        new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero),
        Caveat: null,
        [new VendorUsageWindow("session", 8, null, "Current session: 8% used")]);

    [Fact]
    public async Task TickOnce_LiveLaneAndASnapshot_PersistsItWhereFleetStatusReadsIt()
    {
        var source = new FakeSource("claude", FreshSnapshot());
        var harvester = new VendorUsageHarvester(
            [source],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal) { ["claude"] = 1 }));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var path = BatonPaths.VendorUsageSnapshotFile("claude");
        Assert.True(File.Exists(path), $"expected a persisted snapshot at {path}");
        var persisted = JsonSerializer.Deserialize<VendorUsageSnapshot>(
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        Assert.Equal("claude", persisted.Vendor);
        Assert.Equal(8, Assert.Single(persisted.Windows).PercentUsed);
    }

    [Fact]
    public async Task TickOnce_SourceReturnsNull_LeavesTheLastGoodSnapshotOnDisk()
    {
        // #1869 review, MEDIUM: the arm that had no instrument. What the null-skip in
        // VendorUsageHarvester.TickOnceAsync protects is stated at that skip; this asserts it, down
        // to the previous file being byte-for-byte untouched.
        var path = BatonPaths.VendorUsageSnapshotFile("claude");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lastGood = JsonSerializer.Serialize(FreshSnapshot());
        await File.WriteAllTextAsync(path, lastGood, TestContext.Current.CancellationToken);

        var source = new FakeSource("claude", result: null);
        var harvester = new VendorUsageHarvester(
            [source],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal) { ["claude"] = 1 }));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(1, source.Reads); // the harvest really was attempted -- not skipped by the scheduler
        Assert.Equal(lastGood, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TickOnce_SchedulerSaysNo_NeverReadsTheSourceAtAll()
    {
        // Control arm for both tests above: with no live lane the scheduler's idle backoff must stop
        // the tick before any vendor CLI is spawned, so "no file written" there cannot be explained
        // by the harvester simply never running.
        var source = new FakeSource("claude", FreshSnapshot());
        var harvester = new VendorUsageHarvester(
            [source],
            AlwaysDueScheduler(),
            countLiveLanes: _ => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)));

        await harvester.TickOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(0, source.Reads);
        Assert.False(File.Exists(BatonPaths.VendorUsageSnapshotFile("claude")));
    }
}
