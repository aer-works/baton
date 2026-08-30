using System.Text;
using System.Text.Json;
using Baton.Flow.Domain;
using Baton.Flow.Store;

namespace Baton.Flow.Tests.Store;

public class RoomEventLogReaderWriterTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;

    public RoomEventLogReaderWriterTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "baton_room_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
    }

    [Fact]
    public async Task RoundTrips_room_events_through_writer_and_reader()
    {
        var laneRef = new HeldWorkRef("lanes/lane-1");
        var citation = new HeldWorkCitation("exec-1", "executionSucceeded", 0);

        await using (var writer = new RoomEventLogWriter(_roomLogPath))
        {
            await writer.AppendAsync(new RoomEvent.HeldWorkDispatched(laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-1"), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new RoomEvent.HeldWorkEscalated(laneRef, "op-supervisor"), TestContext.Current.CancellationToken);
            await writer.AppendAsync(new RoomEvent.HeldWorkResolved(laneRef, citation), TestContext.Current.CancellationToken);
        }

        var reader = new RoomEventLogReader(_roomLogPath);
        var events = await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, events.Count);
        Assert.IsType<RoomEvent.HeldWorkDispatched>(events[0]);
        Assert.IsType<RoomEvent.HeldWorkEscalated>(events[1]);
        Assert.IsType<RoomEvent.HeldWorkResolved>(events[2]);

        var resolved = (RoomEvent.HeldWorkResolved)events[2];
        Assert.Equal(laneRef, resolved.Ref);
        Assert.Equal(citation, resolved.Citation);
    }

    [Fact]
    public async Task Reading_a_malformed_complete_line_throws_FlowEventLogReadException()
    {
        await File.WriteAllTextAsync(_roomLogPath, "{\"owner\":\"room\",\"eventType\":\"heldWorkDispatched\"}\n", TestContext.Current.CancellationToken);

        var reader = new RoomEventLogReader(_roomLogPath);
        await Assert.ThrowsAsync<FlowEventLogReadException>(async () => await reader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_stamps_WriterUtcTimestamp_on_room_events()
    {
        var laneRef = new HeldWorkRef("lanes/lane-1");

        using var buffer = new MemoryStream();
        await using var writer = new RoomEventLogWriter(buffer, leaveOpen: true);

        var before = DateTime.UtcNow;
        await writer.AppendAsync(new RoomEvent.HeldWorkDispatched(laneRef, "shape-1", TimeSpan.FromMinutes(10), "op-1"), TestContext.Current.CancellationToken);
        var after = DateTime.UtcNow;

        var text = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entry = Assert.IsType<LogEntry.RoomLogEntry>(JsonSerializer.Deserialize<LogEntry>(text[0], FlowEventLogJson.Options));

        Assert.NotNull(entry.WriterUtcTimestamp);
        Assert.True(entry.WriterUtcTimestamp >= before && entry.WriterUtcTimestamp <= after);
    }

    /// <summary>
    /// #880, measured on CI before it was fixed: a second writer opening the same room log while the
    /// first still held it threw <see cref="IOException"/> out of the constructor — and in the
    /// daemon's resolve endpoint that construction happens before the lock meant to serialise it, so
    /// it escaped as a 500 rather than the 409 the operator could at least act on.
    /// <para>
    /// The single-writer invariant is NOT relaxed and this test would fail if it were: the second
    /// writer must not open while the first holds the file. What it must do is wait, then proceed.
    /// Elapsed time asserts it genuinely waited, so a run where the release beat the open fails
    /// rather than passing on a technicality.
    /// </para>
    /// <para>
    /// Pool-independent on both sides — dedicated release thread, <c>Thread.Sleep</c> in the retry —
    /// for the reason #872 measured the hard way: a contention test scheduled on the pool stops
    /// discriminating exactly when the machine is busy.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_second_writer_waits_for_the_first_to_release_rather_than_throwing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Measured on the CI Linux leg, which reported "opened in 5ms, inside the 250ms hold":
            // FileShare is only OS-enforced on Windows, so on POSIX the second open just succeeds
            // and there is no violation to wait out. The defect this pins does not exist there, and
            // an arm that cannot fail is worse than one that says it did not run. The uncontended
            // open path stays covered everywhere by the other tests in this class.
            Assert.Skip("FileShare is advisory on POSIX; this contention cannot occur there. See #880.");
            return;
        }

        var hold = TimeSpan.FromMilliseconds(250);
        var first = new RoomEventLogWriter(_roomLogPath);

        var release = new Thread(() =>
        {
            Thread.Sleep(hold);
            first.DisposeAsync().AsTask().GetAwaiter().GetResult();
        })
        {
            IsBackground = true,
            Name = "baton-880-release",
        };

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        release.Start();

        var second = new RoomEventLogWriter(_roomLogPath);
        elapsed.Stop();
        release.Join(TimeSpan.FromSeconds(10));
        await second.DisposeAsync();

        Assert.True(
            elapsed.Elapsed >= hold,
            $"The second writer opened in {elapsed.ElapsedMilliseconds}ms, inside the {hold.TotalMilliseconds}ms " +
            "hold -- the file was never actually contended, so this proves nothing.");
    }

    public void Dispose()
    {
        DirectoryCleanup.DeleteRecursively(_tempDirectory);
    }
}
