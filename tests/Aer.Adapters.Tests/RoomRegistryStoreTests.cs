using Aer.Adapters.Tests.TestSupport;

namespace Aer.Adapters.Tests;

/// <summary>
/// spec/baton.md §8's writer/reader: <see cref="RoomRegistryStore"/> is the machine-local
/// multi-project room registry <c>fleet_status</c> unions with its own directory scan
/// (<c>FleetStatusToolTests</c> covers that union; this file covers the store in isolation).
/// </summary>
public class RoomRegistryStoreTests
{
    private static string TempRegistryPath() =>
        Path.Combine(Path.GetTempPath(), $"aer-room-registry-{Guid.NewGuid():N}.jsonl");

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

            await RoomRegistryStore.AppendAsync(roomDir, projectDir, path, TestContext.Current.CancellationToken);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(entries);
            Assert.Equal(AerPaths.RecordKey(roomDir), entry.RoomPath);
            Assert.Equal(AerPaths.RecordKey(projectDir), entry.ProjectRoot);
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
        var directory = Path.Combine(Path.GetTempPath(), $"aer-registry-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "room-registry.jsonl");
        try
        {
            await RoomRegistryStore.AppendAsync("C:/room", "C:/project", path, TestContext.Current.CancellationToken);

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

            await RoomRegistryStore.AppendAsync(roomDir, firstProject, path, TestContext.Current.CancellationToken);
            await RoomRegistryStore.AppendAsync(roomDir, secondProject, path, TestContext.Current.CancellationToken);
            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            var entry = Assert.Single(entries);
            Assert.Equal(AerPaths.RecordKey(secondProject), entry.ProjectRoot);
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
    /// lines to interleaved, unterminated writes. Many concurrent <c>aer dispatch</c> invocations
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
                    roomDir, "C:/project", path, TestContext.Current.CancellationToken))));

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(writerCount, entries.Count);
            var foundRoomPaths = entries.Select(e => e.RoomPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(roomDirs, roomDir => Assert.Contains(AerPaths.RecordKey(roomDir), foundRoomPaths));
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
            await RoomRegistryStore.AppendAsync(roomDir, projectDir, path, TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(path, "{ not valid json\n", TestContext.Current.CancellationToken);

            var otherRoomDir = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}");
            var otherProjectDir = Path.Combine(Path.GetTempPath(), $"project-{Guid.NewGuid():N}");
            await RoomRegistryStore.AppendAsync(otherRoomDir, otherProjectDir, path, TestContext.Current.CancellationToken);

            var entries = await RoomRegistryStore.ReadDistinctByRoomAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.RoomPath == AerPaths.RecordKey(roomDir));
            Assert.Contains(entries, e => e.RoomPath == AerPaths.RecordKey(otherRoomDir));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
