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
    public async Task Reject_writes_the_reason_and_target_to_the_rejected_sibling_and_never_throws()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var path = CancelRequestFile.GetPath(roomDirectory);
            File.WriteAllText(path, "garbage");

            CancelRequestFile.Reject(path, "exec-999", "test reason");

            Assert.False(File.Exists(path));
            var rejectedPath = $"{path}.rejected";
            Assert.True(File.Exists(rejectedPath));
            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal("exec-999", rejected.Target);
            Assert.Equal("test reason", rejected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void DeleteStalePendingRequest_sweeps_a_pending_request_to_swept_and_never_touches_siblings()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var pendingPath = CancelRequestFile.GetPath(roomDirectory);
            var consumedPath = $"{pendingPath}.consumed";
            var rejectedPath = $"{pendingPath}.rejected";
            var sweptPath = $"{pendingPath}.swept";

            File.WriteAllText(pendingPath, """{"Target":"pending-exec"}""");
            File.WriteAllText(consumedPath, """{"Target":"consumed-exec"}""");
            File.WriteAllText(rejectedPath, """{"Target":"rejected-exec","Reason":"prior rejection"}""");
            File.WriteAllText(sweptPath, """{"Target":"prior-swept-exec"}""");

            CancelRequestFile.DeleteStalePendingRequest(roomDirectory);

            Assert.False(File.Exists(pendingPath), "pending cancel.request must be swept");
            Assert.True(File.Exists(sweptPath), "cancel.request.swept must exist");
            Assert.Equal("""{"Target":"pending-exec"}""", File.ReadAllText(sweptPath));

            Assert.True(File.Exists(consumedPath), "consumed sibling must not be touched");
            Assert.Equal("""{"Target":"consumed-exec"}""", File.ReadAllText(consumedPath));

            Assert.True(File.Exists(rejectedPath), "rejected sibling must not be touched");
            Assert.Equal("""{"Target":"rejected-exec","Reason":"prior rejection"}""", File.ReadAllText(rejectedPath));
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
