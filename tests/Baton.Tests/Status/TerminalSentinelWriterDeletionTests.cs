using Baton.Status;
using Baton.Tests.TestSupport;

namespace Baton.Tests.Status;

/// <summary>
/// #1608 re-review finding 2: <see cref="TerminalSentinelWriter.DeleteStaleSentinel(string, bool)"/>'s
/// two polarities. The first response to review finding 8 put the best-effort swallow in the shared
/// helper, so BOTH call sites fail open — including <c>RunCommand</c>'s pre-pump one, where an
/// un-deleted sentinel is exactly the false "already done" reading the file exists to prevent. These
/// pin that the swallow is now the opt-in and the refusal is the default.
/// </summary>
public class TerminalSentinelWriterDeletionTests
{
    [Fact]
    public void An_undeletable_sentinel_refuses_with_a_typed_exception_naming_it_by_default()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "a delete against an open handle is OS-enforced only on Windows; the Unix arm below pins that it just succeeds there");

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-delete-{Guid.NewGuid():N}");
        try
        {
            var path = SeedSentinel(roomDirectory);
            // No FileShare.Delete -- the concurrent-reader case (a fleet-glass poller) the finding names.
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var ex = Assert.Throws<StaleSentinelDeletionException>(
                () => TerminalSentinelWriter.DeleteStaleSentinel(roomDirectory));

            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(path), "the refusal must leave the room exactly as it found it.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void An_undeletable_sentinel_is_swallowed_when_the_caller_asks_for_best_effort()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "a delete against an open handle is OS-enforced only on Windows; the Unix arm below pins that it just succeeds there");

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-delete-{Guid.NewGuid():N}");
        try
        {
            var path = SeedSentinel(roomDirectory);
            using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Program.cs's post-`resolve` call site: the resolution is already durable, so this must
            // warn and return rather than report a succeeded mutation as failed.
            TerminalSentinelWriter.DeleteStaleSentinel(roomDirectory, bestEffort: true);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void A_deletable_sentinel_is_deleted_on_both_settings()
    {
        // The control arm: without this, both tests above would pass against a helper that refused
        // unconditionally, or one that never deleted anything at all.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-delete-{Guid.NewGuid():N}");
        try
        {
            var path = SeedSentinel(roomDirectory);
            TerminalSentinelWriter.DeleteStaleSentinel(roomDirectory);
            Assert.False(File.Exists(path));

            SeedSentinel(roomDirectory);
            TerminalSentinelWriter.DeleteStaleSentinel(roomDirectory, bestEffort: true);
            Assert.False(File.Exists(path));

            // Absent is a silent no-op on both settings, unchanged by this finding.
            TerminalSentinelWriter.DeleteStaleSentinel(roomDirectory);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void An_open_handle_does_not_block_the_delete_off_Windows()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "Windows OS-enforces the sharing violation; the refusal/best-effort arms above pin that half");

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-delete-{Guid.NewGuid():N}");
        try
        {
            var path = SeedSentinel(roomDirectory);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                TerminalSentinelWriter.DeleteStaleSentinel(roomDirectory);
            }

            Assert.False(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static string SeedSentinel(string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var path = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
        File.WriteAllText(path, "{}");
        return path;
    }
}
