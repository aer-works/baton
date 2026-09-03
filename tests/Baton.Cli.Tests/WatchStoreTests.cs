using Baton.Cli.Tests.TestSupport;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>WatchStore</c> (#1488): the per-watch-file registry under <c>{BATON_HOME}/watches</c>. See
/// <see cref="WatchFireServiceTests"/> for the terminal-detection/notify logic built on top of this.
/// </summary>
public sealed class WatchStoreTests
{
    private static WatchRecord SampleRecord(string watchId = "w1", string roomDir = @"C:\rooms\r1") =>
        new(watchId, roomDir, "https://example.invalid/hook", new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task WriteAsync_ThenTryReadAsync_RoundTripsEveryField()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var record = SampleRecord();

        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);
        var read = await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(record.WatchId, read!.WatchId);
        Assert.Equal(record.RoomDirectoryPath, read.RoomDirectoryPath);
        Assert.Equal(record.NotifyTarget, read.NotifyTarget);
        Assert.Equal(record.CreatedAt, read.CreatedAt);
        Assert.Null(read.FiredAt);
    }

    [Fact]
    public async Task TryReadAsync_MissingWatch_ReturnsNull()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");

        var read = await WatchStore.TryReadAsync(watchesDir, "does-not-exist", TestContext.Current.CancellationToken);

        Assert.Null(read);
    }

    [Fact]
    public async Task ListAsync_MissingDirectory_ReturnsEmpty()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches-never-created");

        var list = await WatchStore.ListAsync(watchesDir, TestContext.Current.CancellationToken);

        Assert.Empty(list);
    }

    [Fact]
    public async Task ListAsync_ReturnsEveryRegisteredWatch()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        await WatchStore.WriteAsync(SampleRecord("w1"), watchesDir, TestContext.Current.CancellationToken);
        await WatchStore.WriteAsync(SampleRecord("w2"), watchesDir, TestContext.Current.CancellationToken);

        var list = await WatchStore.ListAsync(watchesDir, TestContext.Current.CancellationToken);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, w => w.WatchId == "w1");
        Assert.Contains(list, w => w.WatchId == "w2");
    }

    [Fact]
    public async Task ListAsync_SkipsAMalformedFileWithoutFailingTheWholeRead()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        Directory.CreateDirectory(watchesDir);
        await WatchStore.WriteAsync(SampleRecord("good"), watchesDir, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(watchesDir, "corrupt.json"), "{ not json", TestContext.Current.CancellationToken);

        var list = await WatchStore.ListAsync(watchesDir, TestContext.Current.CancellationToken);

        var single = Assert.Single(list);
        Assert.Equal("good", single.WatchId);
    }

    [Fact]
    public async Task TryClaimAsync_UnclaimedWatch_MarksFiredAtAndReturnsTrue()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var record = SampleRecord();
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);
        var firedAt = DateTime.UtcNow;

        var claimed = await WatchStore.TryClaimAsync(
            watchesDir, record.WatchId, firedAt, TestContext.Current.CancellationToken);

        Assert.True(claimed);
        var read = await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken);
        Assert.Equal(firedAt, read!.FiredAt);
    }

    [Fact]
    public async Task TryClaimAsync_AlreadyFiredWatch_ReturnsFalseAndLeavesTheOriginalFiredAtUnchanged()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var record = SampleRecord();
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);
        var firstFire = DateTime.UtcNow;
        Assert.True(await WatchStore.TryClaimAsync(
            watchesDir, record.WatchId, firstFire, TestContext.Current.CancellationToken));

        var secondFire = firstFire.AddMinutes(1);
        var claimedAgain = await WatchStore.TryClaimAsync(
            watchesDir, record.WatchId, secondFire, TestContext.Current.CancellationToken);

        Assert.False(claimedAgain);
        var read = await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken);
        Assert.Equal(firstFire, read!.FiredAt);
    }

    /// <summary>
    /// The double-fire race spec/baton.md §2 rules out: a registration-time check and a daemon sweep
    /// iteration can both reach <see cref="WatchStore.TryClaimAsync"/> for the same already-terminal
    /// watch at effectively the same moment. A sequential "call twice" test cannot exercise this --
    /// each call would already see the prior call's write. This drives many concurrent claims at the
    /// exact same unclaimed watch and asserts exactly one wins, which is what actually fails if either
    /// the per-file lock or the FiredAt check inside it is missing.
    /// </summary>
    [Fact]
    public async Task TryClaimAsync_ManyConcurrentCallers_ExactlyOneWins()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var record = SampleRecord();
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => WatchStore.TryClaimAsync(
                watchesDir, record.WatchId, DateTime.UtcNow, TestContext.Current.CancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(claimed => claimed));
    }

    [Fact]
    public async Task TryClaimAsync_MissingWatch_ReturnsFalse()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");

        var claimed = await WatchStore.TryClaimAsync(
            watchesDir, "does-not-exist", DateTime.UtcNow, TestContext.Current.CancellationToken);

        Assert.False(claimed);
    }

    [Fact]
    public async Task RemoveFiredAsync_RemovesOnlyFiredWatches()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        await WatchStore.WriteAsync(SampleRecord("pending"), watchesDir, TestContext.Current.CancellationToken);
        await WatchStore.WriteAsync(SampleRecord("fired"), watchesDir, TestContext.Current.CancellationToken);
        await WatchStore.TryClaimAsync(watchesDir, "fired", DateTime.UtcNow, TestContext.Current.CancellationToken);

        var removed = await WatchStore.RemoveFiredAsync(watchesDir, TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        var remaining = await WatchStore.ListAsync(watchesDir, TestContext.Current.CancellationToken);
        var single = Assert.Single(remaining);
        Assert.Equal("pending", single.WatchId);
    }

    [Fact]
    public async Task RemoveFiredAsync_MissingDirectory_ReturnsZero()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches-never-created");

        var removed = await WatchStore.RemoveFiredAsync(watchesDir, TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
    }

    /// <summary>
    /// M1 (fix round): the daemon reaper — <see cref="WatchStore.ReapAsync"/> — is what keeps
    /// <see cref="ListAsync"/>'s per-sweep O(n) scan bounded instead of growing with every watch ever
    /// registered. Three cases in one room-backed test, mirroring exactly what the fix round asked for:
    /// a fired watch older than the retention window is removed; a fired watch still inside the window
    /// is kept; a pending watch whose room directory was deleted is removed regardless of age.
    /// </summary>
    [Fact]
    public async Task ReapAsync_FiredAndOldWatch_IsRemoved()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = Path.Combine(home.Path, "rooms", "room-1");
        Directory.CreateDirectory(roomDir);
        var record = new WatchRecord("fired-old", roomDir, "https://example.invalid/hook", DateTime.UtcNow.AddDays(-2));
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);
        await WatchStore.TryClaimAsync(
            watchesDir, record.WatchId, DateTime.UtcNow.AddHours(-25), TestContext.Current.CancellationToken);

        var removed = await WatchStore.ReapAsync(
            watchesDir, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.Null(await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReapAsync_FiredButRecentWatch_IsKept()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = Path.Combine(home.Path, "rooms", "room-1");
        Directory.CreateDirectory(roomDir);
        var record = new WatchRecord("fired-recent", roomDir, "https://example.invalid/hook", DateTime.UtcNow);
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);
        await WatchStore.TryClaimAsync(
            watchesDir, record.WatchId, DateTime.UtcNow.AddMinutes(-5), TestContext.Current.CancellationToken);

        var removed = await WatchStore.ReapAsync(
            watchesDir, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.NotNull(await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReapAsync_PendingWatchOnADeletedRoom_IsRemoved()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = Path.Combine(home.Path, "rooms", "room-deleted");
        Directory.CreateDirectory(roomDir);
        var record = new WatchRecord("pending-orphaned", roomDir, "https://example.invalid/hook", DateTime.UtcNow);
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);
        DirectoryCleanup.EnsureDeletedRecursively(roomDir);

        var removed = await WatchStore.ReapAsync(
            watchesDir, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.Null(await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReapAsync_PendingWatchOnAnExistingRoom_IsKept()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = Path.Combine(home.Path, "rooms", "room-still-here");
        Directory.CreateDirectory(roomDir);
        var record = new WatchRecord("pending-live", roomDir, "https://example.invalid/hook", DateTime.UtcNow);
        await WatchStore.WriteAsync(record, watchesDir, TestContext.Current.CancellationToken);

        var removed = await WatchStore.ReapAsync(
            watchesDir, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.NotNull(await WatchStore.TryReadAsync(watchesDir, record.WatchId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReapAsync_MissingDirectory_ReturnsZero()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches-never-created");

        var removed = await WatchStore.ReapAsync(watchesDir, TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
    }
}
