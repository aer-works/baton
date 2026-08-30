using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// #1374 F2 unit coverage for <see cref="TerminalSentinelWriter"/>'s write atomicity and malformed-read
/// handling, isolated from the real-process wiring <see cref="TerminalSentinelEndToEndTests"/> covers.
/// </summary>
public class TerminalSentinelWriterTests
{
    [Fact]
    public async Task WriteAsync_leaves_no_temp_file_behind_and_the_written_sentinel_round_trips()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-atomic-{Guid.NewGuid():N}");
        try
        {
            var view = new WorkflowStatusView("Succeeded", [], ["C:/room/artifacts/plan"], null);

            await TerminalSentinelWriter.WriteAsync(roomDirectory, view, TestContext.Current.CancellationToken);

            // The temp-then-rename write (#1374 F2) must not leave its own temp sibling behind --
            // exactly one file, and it is the sentinel itself, not a stray "*.tmp".
            var entries = Directory.GetFiles(roomDirectory);
            var entry = Assert.Single(entries);
            Assert.Equal(TerminalSentinelWriter.TerminalSentinelFileName, Path.GetFileName(entry));

            var readBack = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.NotNull(readBack);
            Assert.Equal("Succeeded", readBack!.State);
            Assert.Equal(view.Outputs, readBack.Outputs);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task WriteAsync_overwrites_a_prior_sentinel_leaving_only_the_new_one_on_disk()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-overwrite-{Guid.NewGuid():N}");
        try
        {
            await TerminalSentinelWriter.WriteAsync(
                roomDirectory, new WorkflowStatusView("Failed", [], [], "first"), TestContext.Current.CancellationToken);
            await TerminalSentinelWriter.WriteAsync(
                roomDirectory, new WorkflowStatusView("Succeeded", [], [], null), TestContext.Current.CancellationToken);

            var entries = Directory.GetFiles(roomDirectory);
            var entry = Assert.Single(entries);
            Assert.Equal(TerminalSentinelWriter.TerminalSentinelFileName, Path.GetFileName(entry));

            var readBack = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);
            Assert.Equal("Succeeded", readBack!.State);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task TryReadAsync_treats_a_torn_or_malformed_sentinel_as_absent_rather_than_throwing()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"sentinel-torn-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var path = Path.Combine(roomDirectory, TerminalSentinelWriter.TerminalSentinelFileName);
            // What a reader could observe of a write caught mid-move, or a hand-corrupted file:
            // either way, not valid JSON matching WorkflowStatusView's shape (#1374 F2).
            await File.WriteAllTextAsync(path, "{\"state\":\"Succ", TestContext.Current.CancellationToken);

            var result = await TerminalSentinelWriter.TryReadAsync(roomDirectory, TestContext.Current.CancellationToken);

            Assert.Null(result);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
