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
    public async Task DeleteStalePendingRequest_sweeps_a_legacy_pending_request_with_no_metadata_and_never_touches_siblings()
    {
        // No WriterPid/WrittenAtUtc recorded (a pre-#1649 write, or corruption) -- nothing to
        // discriminate on, so this keeps the pre-#1649 unconditional-sweep behaviour.
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

            await CancelRequestFile.DeleteStalePendingRequestAsync(
                roomDirectory, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

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

    /// <summary>
    /// #1649 polarity pair (control arm): a request genuinely left behind by a crashed prior pump —
    /// written well before this invocation started, by a pid that resolves to no running process — IS
    /// swept. Paired with
    /// <see cref="DeleteStalePendingRequest_leaves_a_request_written_after_this_invocation_started_alone"/>
    /// and <see cref="DeleteStalePendingRequest_leaves_a_request_whose_writer_is_still_alive_alone"/>,
    /// which flip one condition each and must NOT sweep — proving the AND, not just one arm of it.
    /// </summary>
    [Fact]
    public async Task DeleteStalePendingRequest_sweeps_a_request_that_predates_this_invocation_from_a_dead_writer()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var pendingPath = CancelRequestFile.GetPath(roomDirectory);
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            var invocationStartUtc = DateTimeOffset.UtcNow;
            var writtenAtUtc = invocationStartUtc.AddMinutes(-10);
            File.WriteAllText(
                pendingPath,
                $$"""{"Target":"stale-exec","WriterPid":999999,"WriterProcessStartTimeUtc":"{{writtenAtUtc:O}}","WrittenAtUtc":"{{writtenAtUtc:O}}"}""");

            await CancelRequestFile.DeleteStalePendingRequestAsync(
                roomDirectory, invocationStartUtc, TestContext.Current.CancellationToken, roomLogPath);

            Assert.False(File.Exists(pendingPath), "a genuinely stale request must still be swept");
            Assert.True(File.Exists($"{pendingPath}.swept"));

            // #1530: the swept request is neither delivered nor rejected -- room.jsonl is its only
            // durable record beyond the renamed .swept sibling.
            var roomReader = new Baton.Store.RoomEventLogReader(roomLogPath);
            var roomEvents = await roomReader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var expired = Assert.Single(roomEvents.OfType<Baton.Domain.RoomEvent.ArrestRequestExpired>());
            Assert.Equal("stale-exec", expired.Target);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Polarity control (same shape CancelRequestPollerTests' own room-log-path controls take): omit
    // roomLogPath, assert silence.
    [Fact]
    public async Task DeleteStalePendingRequest_with_no_room_log_path_given_does_not_write_room_jsonl()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var pendingPath = CancelRequestFile.GetPath(roomDirectory);
            var invocationStartUtc = DateTimeOffset.UtcNow;
            var writtenAtUtc = invocationStartUtc.AddMinutes(-10);
            File.WriteAllText(
                pendingPath,
                $$"""{"Target":"stale-exec","WriterPid":999999,"WriterProcessStartTimeUtc":"{{writtenAtUtc:O}}","WrittenAtUtc":"{{writtenAtUtc:O}}"}""");

            await CancelRequestFile.DeleteStalePendingRequestAsync(
                roomDirectory, invocationStartUtc, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(pendingPath));
            Assert.False(File.Exists(Path.Combine(roomDirectory, "room.jsonl")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>#1649: written at/after this invocation's own start -- could be a concurrent writer racing the provisioning-to-sweep window, so it must be left for the poller regardless of writer liveness.</summary>
    [Fact]
    public async Task DeleteStalePendingRequest_leaves_a_request_written_after_this_invocation_started_alone()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var pendingPath = CancelRequestFile.GetPath(roomDirectory);
            var invocationStartUtc = DateTimeOffset.UtcNow;
            // Predates nothing -- written after invocationStartUtc, and by the same dead pid as the
            // control arm above, so only the timestamp condition differs between the two tests.
            var writtenAtUtc = invocationStartUtc.AddMinutes(10);
            File.WriteAllText(
                pendingPath,
                $$"""{"Target":"fresh-exec","WriterPid":999999,"WriterProcessStartTimeUtc":"{{writtenAtUtc:O}}","WrittenAtUtc":"{{writtenAtUtc:O}}"}""");

            await CancelRequestFile.DeleteStalePendingRequestAsync(
                roomDirectory, invocationStartUtc, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(pendingPath), "a request written at/after this invocation's own start must be left for the poller");
            Assert.False(File.Exists($"{pendingPath}.swept"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>#1649: predates this invocation, but its writer process is still confirmed running -- not provably a crashed prior pump's leftover, so it must be left for the poller.</summary>
    [Fact]
    public async Task DeleteStalePendingRequest_leaves_a_request_whose_writer_is_still_alive_alone()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var pendingPath = CancelRequestFile.GetPath(roomDirectory);
            var invocationStartUtc = DateTimeOffset.UtcNow;
            var writtenAtUtc = invocationStartUtc.AddMinutes(-10);
            var thisProcessStartUtc = new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime).ToUniversalTime();
            File.WriteAllText(
                pendingPath,
                $$"""{"Target":"live-writer-exec","WriterPid":{{Environment.ProcessId}},"WriterProcessStartTimeUtc":"{{thisProcessStartUtc:O}}","WrittenAtUtc":"{{writtenAtUtc:O}}"}""");

            await CancelRequestFile.DeleteStalePendingRequestAsync(
                roomDirectory, invocationStartUtc, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(pendingPath), "a request from a still-alive writer must be left for the poller");
            Assert.False(File.Exists($"{pendingPath}.swept"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1530 fix: see <see cref="CancelRequestFile.DeleteStalePendingRequestAsync"/>'s own remarks on
    /// its malformed-content arm for the mtime-vs-rename ordering bug this pins. A real file on disk
    /// (not a fabricated timestamp) is required to reproduce it: it is specifically the OS mtime
    /// lookup racing the rename that regresses.
    /// </summary>
    [Fact]
    public async Task DeleteStalePendingRequest_records_the_real_mtime_for_malformed_content_not_the_missing_file_sentinel()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var pendingPath = CancelRequestFile.GetPath(roomDirectory);
            var roomLogPath = Path.Combine(roomDirectory, "room.jsonl");
            var beforeWriteUtc = DateTime.UtcNow;
            File.WriteAllText(pendingPath, "not valid json");

            await CancelRequestFile.DeleteStalePendingRequestAsync(
                roomDirectory, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, roomLogPath);

            Assert.True(File.Exists($"{pendingPath}.swept"));

            var roomReader = new Baton.Store.RoomEventLogReader(roomLogPath);
            var roomEvents = await roomReader.ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
            var expired = Assert.Single(roomEvents.OfType<Baton.Domain.RoomEvent.ArrestRequestExpired>());
            Assert.True(
                expired.RequestedAtUtc >= beforeWriteUtc.AddSeconds(-2),
                $"expected RequestedAtUtc near {beforeWriteUtc:O}, got {expired.RequestedAtUtc:O} -- the missing-file sentinel (1601-01-01) means the mtime was read after the rename");
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
