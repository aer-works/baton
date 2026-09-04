using Baton.Vendors.Tests.TestSupport;
using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// spec/baton.md §8's writer/reader: <see cref="RoomRegistryStore"/> is the machine-local
/// multi-project room registry <c>fleet_status</c> unions with its own directory scan
/// (<c>FleetStatusToolTests</c> covers that union; this file covers the store in isolation).
/// </summary>
[Collection(ConsoleErrorCaptureCollection.Name)]
public class RoomRegistryStoreTests
{
    private static string TempRegistryPath() =>
        Path.Combine(Path.GetTempPath(), $"baton-room-registry-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task Reading_a_missing_file_resolves_to_an_empty_list()
    {
        var path = TempRegistryPath();

        var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task Appending_then_reading_round_trips_the_room_and_project_root()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
            Assert.Equal(BatonPaths.RecordKey(projectDir), entry.ProjectRoot);
            Assert.True(entry.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Appending_creates_the_parent_directory_if_it_does_not_exist_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"baton-registry-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "room-registry.jsonl");
        try
        {
            await RoomRegistryStore.AppendAsync(
                "C:/room", "C:/project", path, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task Repeated_registrations_of_the_same_room_fold_to_the_last_write()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var firstProject = Path.Combine(Path.GetTempPath(), $"project-a-{Guid.NewGuid():N}");
            var secondProject = Path.Combine(Path.GetTempPath(), $"project-b-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, firstProject, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(
                roomDir, secondProject, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(secondProject), entry.ProjectRoot);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// The reason <see cref="RoomRegistryStore"/> serializes every access behind a named
    /// <see cref="Mutex"/>: <c>FileMode.Append</c> alone is not atomic across concurrent writers on
    /// Windows — measured directly during review, six separate processes each appending under
    /// <c>FileMode.Append</c>/<c>FileShare.ReadWrite</c> with no lock lost roughly a fifth of their
    /// lines to interleaved, unterminated writes. Many concurrent <c>baton dispatch</c> invocations
    /// writing to one shared registry file is exactly the scenario the registry exists to serve, so
    /// this drives a real, if in-process, instance of that concurrency at the store's public API and
    /// asserts every registration survives.
    /// </summary>
    [Fact]
    public async Task Concurrent_appends_from_many_tasks_lose_no_entries()
    {
        var path = TempRegistryPath();
        try
        {
            const int writerCount = 50;
            var roomDirs = Enumerable.Range(0, writerCount)
                .Select(i => Path.Combine(Path.GetTempPath(), $"room-concurrent-{i}-{Guid.NewGuid():N}"))
                .ToList();

            await Task.WhenAll(roomDirs.Select(roomDir => Task.Run(() =>
                RoomRegistryStore.AppendAsync(
                    roomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken))));

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(writerCount, entries.Count);
            var foundRoomPaths = entries.Select(e => e.RoomPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(roomDirs, roomDir => Assert.Contains(BatonPaths.RecordKey(roomDir), foundRoomPaths));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task A_malformed_line_is_skipped_without_hiding_the_well_formed_entries_around_it()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(path, "{ not valid json\n", TestContext.Current.CancellationToken);

            var otherRoomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var otherProjectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(
                otherRoomDir, otherProjectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.RoomPath == BatonPaths.RecordKey(roomDir));
            Assert.Contains(entries, e => e.RoomPath == BatonPaths.RecordKey(otherRoomDir));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1657: the mechanism the issue reports — a lane's manual repro room under <c>%TEMP%</c> ends up
    /// on the fleet glass with nothing that will ever drive it. Skipping the write at the source is the
    /// registry-side half of the fix; the reader-side half (a registry entry whose room directory no
    /// longer exists is dropped) is <c>FleetStatusToolTests.RegistryEntry_WhoseRoomDirectoryWasDeleted_IsSkippedRatherThanErroring</c>
    /// in <c>Baton.Cli.Tests</c>.
    /// </summary>
    [Fact]
    public async Task Appending_a_room_under_the_temp_directory_is_skipped_and_reported_on_stderr()
    {
        var path = TempRegistryPath();
        var originalError = Console.Error;
        try
        {
            var stderr = new StringWriter();
            Console.SetError(stderr);

            var roomDir = Path.Combine(Path.GetTempPath(), $"manual-repro-{Guid.NewGuid():N}", "task");

            await RoomRegistryStore.AppendAsync(
                roomDir, "C:/project", path, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            Assert.Empty(entries);
            Assert.Contains("Room registry: skipping", stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// A project's own <c>.scratch*</c>/<c>.baton</c> directory (e.g. a bare <c>baton run</c>'s
    /// default room directory, <c>{cwd}/.baton/{workflow}</c>) is the second throwaway shape the issue
    /// names — <c>w1513\.baton\test-room</c> was one of the thirteen hand-pruned entries.
    /// </summary>
    [Theory]
    [InlineData(".baton")]
    [InlineData(".scratch-vp")]
    [InlineData(".scratch-verify-pack")]
    public async Task Appending_a_room_under_a_scratch_or_baton_project_directory_is_skipped(string scratchSegment)
    {
        var path = TempRegistryPath();
        try
        {
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            var roomDir = Path.Combine(projectDir, scratchSegment, "task");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            Assert.Empty(entries);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Appending_a_throwaway_repro_room_with_explicitRegister_is_recorded()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"manual-repro-{Guid.NewGuid():N}", "task");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(roomDir), entry.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// A room under <see cref="BatonPaths.Rooms"/> is never a repro, however its default temp-backed
    /// test isolation is exercised elsewhere: this specifically pins that a literal <c>.baton/rooms</c>
    /// path segment (which every home room carries) does not itself trip the <c>.baton</c> scratch
    /// exclusion.
    /// </summary>
    [Fact]
    public async Task Appending_a_room_under_the_home_rooms_directory_is_never_skipped()
    {
        var path = TempRegistryPath();
        var homeRoom = Path.Combine(BatonPaths.Rooms, $"room-{Guid.NewGuid():N}");
        try
        {
            await RoomRegistryStore.AppendAsync(
                homeRoom, "C:/project", path, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);
            Assert.Equal(BatonPaths.RecordKey(homeRoom), entry.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    /// <summary>
    /// #1657: the registry "also does not dedupe" — the same (room, project) pair appended twice grew
    /// the file by one line every time. A project-root *change* for the same room path is still a real
    /// update and still appends (<see cref="Repeated_registrations_of_the_same_room_fold_to_the_last_write"/>).
    /// </summary>
    [Fact]
    public async Task Appending_an_identical_room_and_project_twice_writes_only_one_line()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(
                roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var lineCount = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
                .Count(line => !string.IsNullOrWhiteSpace(line));
            Assert.Equal(1, lineCount);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            Assert.Single(entries);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    // #1659: RemoveByRoomPathAsync backs `baton room delete`'s registry-line removal.
    [Fact]
    public async Task RemoveByRoomPathAsync_RemovesEveryLineForThatRoom_AndReturnsTheCount()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var otherRoomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var projectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");

            // Two lines for roomDir (a project-root change re-appends, #1657) plus one unrelated room.
            await RoomRegistryStore.AppendAsync(roomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(roomDir, projectDir + "2", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(otherRoomDir, projectDir, path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var removedCount = await RoomRegistryStore.RemoveByRoomPathAsync(path, roomDir, TestContext.Current.CancellationToken);

            Assert.Equal(2, removedCount);
            var remaining = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var survivor = Assert.Single(remaining);
            Assert.Equal(BatonPaths.RecordKey(otherRoomDir), survivor.RoomPath);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveByRoomPathAsync_NoMatchingLine_ReturnsZero_AndLeavesTheFileUntouched()
    {
        var path = TempRegistryPath();
        try
        {
            var roomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(roomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var removedCount = await RoomRegistryStore.RemoveByRoomPathAsync(
                path, Path.Combine(Path.GetTempPath(), "no-such-room"), TestContext.Current.CancellationToken);

            Assert.Equal(0, removedCount);
            Assert.Single(await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveByRoomPathAsync_MissingFile_ReturnsZero_NeverThrows()
    {
        var path = TempRegistryPath();
        var removedCount = await RoomRegistryStore.RemoveByRoomPathAsync(path, "C:/no-such-room", TestContext.Current.CancellationToken);
        Assert.Equal(0, removedCount);
    }

    // #1659: CompactAsync backs `baton rooms prune`'s unconditional registry-hygiene pass —
    // spec/baton.md §8's "left undone" compaction.
    [Fact]
    public async Task CompactAsync_DedupesAndDropsMissingDirectories_AndRewritesTheFile()
    {
        var path = TempRegistryPath();
        var keptRoomDir = Path.Combine(Path.GetTempPath(), $"baton-registry-kept-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keptRoomDir);
        try
        {
            var missingRoomDir = Path.Combine(Path.GetTempPath(), $"baton-registry-missing-{Guid.NewGuid():N}");
            // Two raw lines for keptRoomDir (a duplicate registration, #1657's "does not dedupe" gap)
            // plus one line for a directory that was never created — CompactAsync must fold the first
            // pair to one survivor and drop the second entirely.
            await RoomRegistryStore.AppendAsync(keptRoomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(keptRoomDir, "C:/project2", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(missingRoomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var (dedupedCount, missingDirectoryCount) = await RoomRegistryStore.CompactAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(1, dedupedCount);
            Assert.Equal(1, missingDirectoryCount);
            var remaining = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);
            var survivor = Assert.Single(remaining);
            Assert.Equal(BatonPaths.RecordKey(keptRoomDir), survivor.RoomPath);
            var lineCount = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Count(line => !string.IsNullOrWhiteSpace(line));
            Assert.Equal(1, lineCount);
        }
        finally
        {
            FileCleanup.Delete(path);
            DirectoryCleanup.DeleteRecursively(keptRoomDir);
        }
    }

    [Fact]
    public async Task PreviewCompactionAsync_ReportsTheSameCounts_ButNeverWritesTheFile()
    {
        var path = TempRegistryPath();
        try
        {
            var missingRoomDir = Path.Combine(Path.GetTempPath(), $"baton-registry-missing-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(missingRoomDir, "C:/project", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(missingRoomDir, "C:/project2", path, explicitRegister: true, cancellationToken: TestContext.Current.CancellationToken);

            var beforeText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var (dedupedCount, missingDirectoryCount) = await RoomRegistryStore.PreviewCompactionAsync(path, TestContext.Current.CancellationToken);
            var afterText = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(1, dedupedCount);
            Assert.Equal(1, missingDirectoryCount);
            Assert.Equal(beforeText, afterText);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
