namespace Baton.Cli.Tests;

/// <summary>
/// #1495's request-file parsing: valid explicit id, the <c>latest</c> literal, and every malformed
/// shape <see cref="CancelRequestPoller"/> must reject rather than crash on.
/// </summary>
public class CancelRequestFileTests
{
    [Fact]
    public async Task An_explicit_execution_id_round_trips()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        try
        {
            await CancelRequestFile.WriteAsync(roomDirectory, "exec-123", TestContext.Current.CancellationToken);

            var content = await CancelRequestFile.TryReadAsync(
                CancelRequestFile.GetPath(roomDirectory), TestContext.Current.CancellationToken);

            Assert.NotNull(content);
            Assert.Equal("exec-123", content.Target);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task The_latest_literal_round_trips()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        try
        {
            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var content = await CancelRequestFile.TryReadAsync(
                CancelRequestFile.GetPath(roomDirectory), TestContext.Current.CancellationToken);

            Assert.NotNull(content);
            Assert.Equal("latest", content.Target);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Invalid_JSON_is_rejected_as_null_not_an_exception()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var path = CancelRequestFile.GetPath(roomDirectory);
            await File.WriteAllTextAsync(path, "{ not valid json", TestContext.Current.CancellationToken);

            var content = await CancelRequestFile.TryReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Null(content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_blank_target_is_rejected_as_null()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var path = CancelRequestFile.GetPath(roomDirectory);
            await File.WriteAllTextAsync(path, """{"Target":"   "}""", TestContext.Current.CancellationToken);

            var content = await CancelRequestFile.TryReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Null(content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_missing_file_is_rejected_as_null()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        var content = await CancelRequestFile.TryReadAsync(
            CancelRequestFile.GetPath(roomDirectory), TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public void Reject_renames_the_file_to_a_rejected_sibling_and_never_throws()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var path = CancelRequestFile.GetPath(roomDirectory);
            File.WriteAllText(path, "garbage");

            CancelRequestFile.Reject(path, "test reason");

            Assert.False(File.Exists(path));
            Assert.True(File.Exists($"{path}.rejected"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Consume_renames_the_file_to_a_consumed_sibling()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var path = CancelRequestFile.GetPath(roomDirectory);
            File.WriteAllText(path, """{"Target":"latest"}""");

            CancelRequestFile.Consume(path);

            Assert.False(File.Exists(path));
            Assert.True(File.Exists($"{path}.consumed"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
