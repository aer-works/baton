namespace Baton.Tests;

/// <summary>
/// Proves the <see cref="Baton.Tests.Shared.FileCleanup"/> helpers (and the shared
/// <c>CleanupRetry</c> core behind them and <see cref="Baton.Tests.Shared.DirectoryCleanup"/>) do what
/// the #918 sweep relies on: retry past a transient Windows share-lock, and split on a persistent one
/// — teardown (<see cref="Baton.Tests.Shared.FileCleanup.Delete"/>) swallows it so it can't mask a test
/// result, setup (<see cref="Baton.Tests.Shared.FileCleanup.EnsureDeleted"/>) surfaces it.
/// </summary>
public class CleanupHelpersTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"cleanup-{Guid.NewGuid():N}.tmp");

    [Fact]
    public async Task Delete_removes_an_existing_file_and_no_ops_on_a_missing_one()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);

        FileCleanup.Delete(path);
        Assert.False(File.Exists(path));

        FileCleanup.Delete(path); // already gone — must not throw
    }

    [Fact]
    public async Task EnsureDeleted_removes_an_existing_file_and_no_ops_on_a_missing_one()
    {
        var path = TempPath();
        await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);

        FileCleanup.EnsureDeleted(path);
        Assert.False(File.Exists(path));

        FileCleanup.EnsureDeleted(path); // already gone — must not throw
    }

    [Fact]
    public async Task Delete_retries_past_a_transient_lock_and_succeeds_once_the_holder_releases()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX unlink ignores open handles; the transient share-lock is Windows-only (#295).");
        }

        var path = TempPath();
        await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);

        var held = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            // No FileShare.Delete, so the file cannot be deleted while this handle is open.
            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            held.Set();
            Thread.Sleep(120); // release well within FileCleanup's ~500ms retry budget
        }, TestContext.Current.CancellationToken);

        held.Wait(TestContext.Current.CancellationToken); // the lock is definitely held before the first attempt
        FileCleanup.Delete(path); // first attempt fails; a later one wins after the holder releases
        await holder;

        Assert.False(File.Exists(path)); // the transient lock was cleared and the file removed
    }

    [Fact]
    public async Task Under_a_persistent_lock_Delete_swallows_but_EnsureDeleted_and_a_bare_delete_surface()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX unlink ignores open handles; the transient share-lock is Windows-only (#295).");
        }

        var path = TempPath();
        await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);

        // Held for the whole test — longer than the retry budget, so every attempt hits the lock.
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Control: the lock is real — an unretried delete throws.
        Assert.ThrowsAny<Exception>(() => File.Delete(path));

        // Teardown form swallows the persistent failure (must never mask a test's real result)...
        FileCleanup.Delete(path);
        Assert.True(File.Exists(path)); // genuinely could not be deleted, and the failure was swallowed

        // ...setup form surfaces it (a stale file must not silently corrupt a test premise).
        Assert.ThrowsAny<Exception>(() => FileCleanup.EnsureDeleted(path));
    }
}
