using Baton.Cli.Tests.TestSupport;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// <c>WatchFireService</c> (#1488, spec/baton.md §2): terminal detection (reusing
/// <see cref="TerminalSentinelWriter"/> — no second definition), the exactly-once claim, and the
/// notify hand-off. <see cref="SweepAsync_TerminalRoom_FiresExactlyOnceAcrossTwoSweeps"/> is the
/// scope's fire-once test; the actual red-first proof for the exactly-once guard lives in
/// <c>WatchStoreTests.TryClaimAsync_ManyConcurrentCallers_ExactlyOneWins</c> (a sequential
/// call-twice test cannot go red against a missing guard, since the second call already observes the
/// first call's write — only genuine concurrency exercises the race) — see <c>changes.md</c> for the
/// captured red output.
/// </summary>
public sealed class WatchFireServiceTests
{
    private static string CreateRoomDirectory(string homePath, string name)
    {
        var roomDir = Path.Combine(homePath, "rooms", name);
        Directory.CreateDirectory(roomDir);
        return roomDir;
    }

    private static Task WriteTerminalSentinelAsync(
        string roomDir, string state, IReadOnlyList<string>? outputs, CancellationToken cancellationToken) =>
        TerminalSentinelWriter.WriteAsync(roomDir, new WorkflowStatusView(state, [], outputs ?? [], null), cancellationToken);

    private static WatchRecord SampleWatch(string roomDir, string watchId = "w1") =>
        new(watchId, roomDir, "https://example.invalid/hook", DateTime.UtcNow);

    [Fact]
    public async Task TryFireIfTerminalAsync_AlreadyTerminalRoom_ClaimsAndNotifiesOnce()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-1");
        await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, null, TestContext.Current.CancellationToken);

        var watch = SampleWatch(roomDir);
        await WatchStore.WriteAsync(watch, watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        var fired = await WatchFireService.TryFireIfTerminalAsync(
            watchesDir, watch, notifier, TestContext.Current.CancellationToken);

        Assert.True(fired);
        var (target, payload) = Assert.Single(notifier.Calls);
        Assert.Equal(watch.NotifyTarget, target);
        Assert.Equal(roomDir, payload.Room);
        Assert.Equal(WorkflowOutcome.Succeeded, payload.State);

        var stored = await WatchStore.TryReadAsync(watchesDir, watch.WatchId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored!.FiredAt);
    }

    [Fact]
    public async Task TryFireIfTerminalAsync_NonTerminalRoom_DoesNotFire()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-running"); // no terminal.json written

        var watch = SampleWatch(roomDir);
        await WatchStore.WriteAsync(watch, watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        var fired = await WatchFireService.TryFireIfTerminalAsync(
            watchesDir, watch, notifier, TestContext.Current.CancellationToken);

        Assert.False(fired);
        Assert.Empty(notifier.Calls);

        var stored = await WatchStore.TryReadAsync(watchesDir, watch.WatchId, TestContext.Current.CancellationToken);
        Assert.Null(stored!.FiredAt);
    }

    [Fact]
    public async Task TryFireIfTerminalAsync_AlreadyFiredWatch_NeverCallsTheNotifierAgain()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-1");
        await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, null, TestContext.Current.CancellationToken);

        var watch = SampleWatch(roomDir) with { FiredAt = DateTime.UtcNow.AddMinutes(-1) };
        await WatchStore.WriteAsync(watch, watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        var fired = await WatchFireService.TryFireIfTerminalAsync(
            watchesDir, watch, notifier, TestContext.Current.CancellationToken);

        Assert.False(fired);
        Assert.Empty(notifier.Calls);
    }

    /// <summary>
    /// The scope's fire-once test, driven the way the daemon actually drives it: two independent
    /// <see cref="WatchFireService.SweepAsync"/> passes over the same watches directory, simulating two
    /// <c>WatchSweep</c> iterations. Only the first may notify.
    /// </summary>
    [Fact]
    public async Task SweepAsync_TerminalRoom_FiresExactlyOnceAcrossTwoSweeps()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-1");
        await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, null, TestContext.Current.CancellationToken);
        await WatchStore.WriteAsync(SampleWatch(roomDir), watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        var firstSweepFiredCount = await WatchFireService.SweepAsync(watchesDir, notifier, TestContext.Current.CancellationToken);
        var secondSweepFiredCount = await WatchFireService.SweepAsync(watchesDir, notifier, TestContext.Current.CancellationToken);

        Assert.Equal(1, firstSweepFiredCount);
        Assert.Equal(0, secondSweepFiredCount);
        Assert.Single(notifier.Calls); // the positive-polarity control: exactly one notify, not zero
    }

    [Fact]
    public async Task SweepAsync_NonTerminalRoom_NeverFiresAcrossRepeatedSweeps()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-running");
        await WatchStore.WriteAsync(SampleWatch(roomDir), watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        await WatchFireService.SweepAsync(watchesDir, notifier, TestContext.Current.CancellationToken);
        await WatchFireService.SweepAsync(watchesDir, notifier, TestContext.Current.CancellationToken);

        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task TryFireIfTerminalAsync_OutputsIncludeAVerdictJson_CarriesItsContentInThePayload()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-1");
        var verdictPath = Path.Combine(roomDir, "verdict.json");
        await File.WriteAllTextAsync(verdictPath, """{"pass":true,"summary":"looks good"}""", TestContext.Current.CancellationToken);
        await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, [verdictPath], TestContext.Current.CancellationToken);
        var watch = SampleWatch(roomDir);
        await WatchStore.WriteAsync(watch, watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        await WatchFireService.TryFireIfTerminalAsync(watchesDir, watch, notifier, TestContext.Current.CancellationToken);

        var (_, payload) = Assert.Single(notifier.Calls);
        Assert.NotNull(payload.Verdict);
        Assert.True(payload.Verdict!.Value.GetProperty("pass").GetBoolean());
        Assert.Equal("looks good", payload.Verdict.Value.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task TryFireIfTerminalAsync_NoVerdictJsonAmongOutputs_LeavesVerdictNull()
    {
        using var home = new IsolatedBatonHome();
        var watchesDir = Path.Combine(home.Path, "watches");
        var roomDir = CreateRoomDirectory(home.Path, "room-1");
        var reportPath = Path.Combine(roomDir, "report.md");
        await File.WriteAllTextAsync(reportPath, "# report", TestContext.Current.CancellationToken);
        await WriteTerminalSentinelAsync(roomDir, WorkflowOutcome.Succeeded, [reportPath], TestContext.Current.CancellationToken);
        var watch = SampleWatch(roomDir);
        await WatchStore.WriteAsync(watch, watchesDir, TestContext.Current.CancellationToken);
        var notifier = new RecordingWatchNotifier();

        await WatchFireService.TryFireIfTerminalAsync(watchesDir, watch, notifier, TestContext.Current.CancellationToken);

        var (_, payload) = Assert.Single(notifier.Calls);
        Assert.Null(payload.Verdict);
        Assert.Equal([reportPath], payload.Outputs);
    }
}
