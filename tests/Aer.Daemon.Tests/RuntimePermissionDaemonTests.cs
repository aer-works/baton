using System.Net.Http.Json;
using System.Text.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Daemon.Tests;

public sealed class RuntimePermissionDaemonTests : IDisposable
{
    private readonly string _tempRoomDir;

    public RuntimePermissionDaemonTests()
    {
        _tempRoomDir = Path.Combine(Path.GetTempPath(), $"daemon-perm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoomDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoomDir))
        {
            DirectoryCleanup.DeleteRecursively(_tempRoomDir);
        }
    }

    [Fact]
    public async Task SetMode_WhileRoomEventsLockHeld_Returns503_AndBindingsUnchanged()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var sessionId = "sess-mode-lock-" + Guid.NewGuid().ToString("N");
            var roomDir = InteractiveSessionMaterializer.ResolveRoomDirectoryPath(sessionId, null, null);
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId, roomDir, "claude", null, _tempRoomDir, null, 100, InteractiveSessionMaterializer.GrantForMode("interactive"), TestContext.Current.CancellationToken);

            var bindingsPath = Path.Combine(roomDir, "bindings.json");
            var initialBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);

            try
            {
                // Hold room-events lock
                using var roomEventsGuard = Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test mode lock hold");

                var response = await client.PostAsJsonAsync($"{baseUrl}/api/sessions/{sessionId}/mode", new { mode = "auto" }, TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);

                var afterBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
                Assert.Equal(initialBindingsText, afterBindingsText);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SendTurn_WhileRoomEventsLockHeld_FailsBeforeRewritingBindings_AndNamesTheLock()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var sessionId = "sess-turn-lock-" + Guid.NewGuid().ToString("N");
            var roomDir = InteractiveSessionMaterializer.ResolveRoomDirectoryPath(sessionId, null, null);
            await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                sessionId, roomDir, "claude", null, _tempRoomDir, null, 100, InteractiveSessionMaterializer.GrantForMode("interactive"), TestContext.Current.CancellationToken);

            var bindingsPath = Path.Combine(roomDir, "bindings.json");
            var initialBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);

            try
            {
                // #1110: the per-turn bindings rewrite in ExecuteSessionTurnCoreAsync must refuse
                // (bounded acquire, then WorkflowLockedException) rather than rewrite bindings.json
                // while another room-events holder is live. The send endpoint is fire-and-forget
                // (200 up front, #341), so the failure surfaces in .aer/turn-errors.log — and since
                // 0053 the lock message names the contended lock file, which is what pins this
                // failure to the room-events lock rather than any other turn error.
                using var roomEventsGuard = Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test turn lock hold");

                var response = await client.PostAsJsonAsync($"{baseUrl}/api/sessions/send",
                    new { sessionId, message = "hello" }, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

                var errorLogPath = Path.Combine(roomDir, ".aer", "turn-errors.log");
                var errorDeadline = DateTime.UtcNow.AddSeconds(20);
                string errorText = "";
                while (DateTime.UtcNow < errorDeadline)
                {
                    if (File.Exists(errorLogPath))
                    {
                        // #1120: the daemon's AppendTurnErrorAsync can be mid-append when this poll
                        // reads — on Windows that is a sharing violation, not a failure. A locked
                        // file just means "try again next tick" inside the same bounded deadline.
                        try
                        {
                            errorText = await File.ReadAllTextAsync(errorLogPath, TestContext.Current.CancellationToken);
                        }
                        catch (IOException)
                        {
                            errorText = "";
                        }
                        if (errorText.Contains(Aer.Flow.Concurrency.ConcurrencyGuard.RoomEventsLockFileName))
                        {
                            break;
                        }
                    }
                    await Task.Delay(100, TestContext.Current.CancellationToken); // wait-ok: bounded poll for the fire-and-forget turn's persisted error
                }

                Assert.Contains(Aer.Flow.Concurrency.ConcurrencyGuard.RoomEventsLockFileName, errorText);

                var afterBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
                Assert.Equal(initialBindingsText, afterBindingsText);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1238: the endpoint that gives a standing permission back — see <c>RevokeAsync</c> for what
    /// answering a persisting rung used to leave a person stuck with.
    /// </summary>
    /// <remarks>
    /// Deliberately drives the round trip through HTTP rather than calling the primitive: what the
    /// primitive's own tests cannot see is whether the endpoint reaches it at all, with the right
    /// worker, under the room-events guard — the same seam #1240's reviewer found unwired on a
    /// sibling endpoint. The three arms are one call each: the withdrawal, the second withdrawal that
    /// must not read as an error, and the refusal of a kind that does not exist.
    /// </remarks>
    [Fact]
    public async Task RevokePermission_TakesTheStandingShellPermissionBack_AndSaysWhichHappened()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-revoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var revoked = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Contains(
                    nameof(PermissionRevokeOutcome.Revoked),
                    await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                // The file, not just the response: the shell is genuinely gone from the binding the
                // next turn reads.
                var afterGrant = (await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken))
                    [InteractiveSessionMaterializer.DefaultWorkerName].PermissionGrant;
                Assert.False(afterGrant!.RunShellCommands);

                // Again — still 200, and it says nothing was left to take back rather than reporting a
                // withdrawal that did not happen.
                var again = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.True(again.IsSuccessStatusCode);
                Assert.Contains(
                    nameof(PermissionRevokeOutcome.NothingToRevoke),
                    await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                // And a kind that does not exist is refused by name, not quietly treated as a nearby
                // one — DenyAlways in particular, since lifting a standing refusal is exactly what
                // revocation must never become.
                var unknown = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionDecisionKind.DenyAlways },
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.BadRequest, unknown.StatusCode);
                Assert.Contains(
                    PermissionRevokeKind.RoomShell,
                    await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                // The two CouldNotPersist situations get different sentences. A room with bindings but
                // no such worker names the worker; a room with no bindings at all must not, or it sends
                // the person hunting for a worker in a room that has no worker setup to look in.
                var unknownWorker = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell, workerName = "not-in-this-room" },
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.BadRequest, unknownWorker.StatusCode);
                Assert.Contains(
                    "not-in-this-room",
                    await unknownWorker.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                FileCleanup.EnsureDeleted(bindingsPath);
                var noBindings = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.BadRequest, noBindings.StatusCode);
                var noBindingsText = await noBindings.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Contains("no worker setup", noBindingsText);
                Assert.DoesNotContain(InteractiveSessionMaterializer.DefaultWorkerName, noBindingsText);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1251: a revoke that actually withdrew a standing permission is journaled — the room's own
    /// record can now answer "when did this worker stop being allowed to run shell commands, and
    /// who took it away?", which #1251 measured that nothing previously could. A repeat revoke that
    /// resolves to <c>NothingToRevoke</c> must NOT add a second entry: nothing was actually taken
    /// back the second time, and journaling it would record a withdrawal that never happened.
    /// </summary>
    [Fact]
    public async Task RevokePermission_ThatActuallyWithdraws_IsJournaled_AndARepeatRevokeIsNot()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-revoke-journal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var revoked = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);
                Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                var again = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);
                Assert.True(again.IsSuccessStatusCode);
                Assert.Contains(
                    nameof(PermissionRevokeOutcome.NothingToRevoke),
                    await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                var roomLogPath = Path.Combine(roomDir, "room.jsonl");
                var events = await new RoomEventLogReader(roomLogPath).ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
                var revokedEvents = events.OfType<RoomEvent.StandingPermissionRevoked>().ToList();

                var entry = Assert.Single(revokedEvents);
                Assert.Equal(InteractiveSessionMaterializer.DefaultWorkerName, entry.WorkerName);
                Assert.Equal(PermissionRevokeKind.RoomShell, entry.RevokeKind);
                Assert.Null(entry.ShellCommandPattern);
                Assert.Equal("human", entry.RevokedBy);
                Assert.True(entry.RevokedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1251's second reader — see the catch block around the journal write in the revoke route for
    /// why. Forces the failure with <c>room.jsonl</c> held open exclusively from outside the
    /// daemon's own locking, rather than mocking the writer.
    /// </summary>
    [Fact]
    public async Task RevokePermission_WhenJournalingFails_StillReportsTheRealWithdrawal()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-revoke-journal-fail-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                // RoomEventLogWriter's own open takes FileShare.Read (#880), so holding the file with
                // FileShare.None from outside it forces its retry budget to expire and rethrow —
                // exactly the "journaling failed" case, without mocking the writer.
                var roomLogPath = Path.Combine(roomDir, "room.jsonl");
                await using (new FileStream(roomLogPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var revoked = await client.PostAsJsonAsync(
                        $"{baseUrl}/api/rooms/permissions/revoke",
                        new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                        TestContext.Current.CancellationToken);

                    Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                    Assert.Contains(
                        nameof(PermissionRevokeOutcome.Revoked),
                        await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                }

                // The withdrawal is real regardless of the journal's fate.
                var afterGrant = (await WorkerBindingConfigParser.LoadFromFileAsync(bindingsPath, TestContext.Current.CancellationToken))
                    [InteractiveSessionMaterializer.DefaultWorkerName].PermissionGrant;
                Assert.False(afterGrant!.RunShellCommands);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1238's second reader: the sibling writers of a live room's <c>bindings.json</c> each have a
    /// test that holds the room-events lock and pins the refusal; this endpoint did not.
    /// </summary>
    /// <remarks>
    /// The failure it exists to catch is the specific one the endpoint's own comment names: a change
    /// that swallowed the lost guard and answered 200 would tell an operator a permission is withdrawn
    /// while it is still in force — the one thing a revocation must never say. So both halves are
    /// asserted: the status, and that the file is byte-identical afterwards.
    /// </remarks>
    [Fact]
    public async Task RevokePermission_WhileRoomEventsLockHeld_Returns503_AndBindingsUnchanged()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-revoke-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var initialBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);

                using (Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test revoke lock hold"))
                {
                    var response = await client.PostAsJsonAsync(
                        $"{baseUrl}/api/rooms/permissions/revoke",
                        new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                        TestContext.Current.CancellationToken);

                    Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);

                    var afterBindingsText = await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken);
                    Assert.Equal(initialBindingsText, afterBindingsText);
                }

                // The control arm: with the lock released the identical call succeeds, so the 503
                // above is the lock and not something else about this room refusing every attempt.
                var afterRelease = await client.PostAsJsonAsync(
                    $"{baseUrl}/api/rooms/permissions/revoke",
                    new { directoryPath = roomDir, revokeKind = PermissionRevokeKind.RoomShell },
                    TestContext.Current.CancellationToken);

                Assert.True(afterRelease.IsSuccessStatusCode);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1249: verifies GET /api/rooms/permissions reads back standing permissions amended through
    /// the real ladder path, returning all three kinds (room shell boolean, allowed command pattern,
    /// and standing refusals list).
    /// </summary>
    [Fact]
    public async Task GetPermissions_ReadsBackLadderGrants_AssertingAllThreeKindsAndDiscriminatingRefusals()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-getperm-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                var defaultWorker = InteractiveSessionMaterializer.DefaultWorkerName;

                // Seeded directly with the shape a standing ladder grant takes (a pre-cleared lane's
                // bindings.json, #1417 -- there is no runtime ladder-answer path left to build it).
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [defaultWorker] = new(
                            "claude",
                            new WorkerContract(defaultWorker, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(
                                RunShellCommands: true,
                                ShellCommandPatterns: ["git *"],
                                DeniedShellCommandPatterns: ["rm *"])),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                // Read back via GET /api/rooms/permissions
                var response = await client.GetAsync(
                    $"{baseUrl}/api/rooms/permissions?directoryPath={Uri.EscapeDataString(roomDir)}",
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

                var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                Assert.Equal(nameof(StandingPermissionReadOutcome.Configured), root.GetProperty("outcome").GetString());
                Assert.True(root.GetProperty("runShellCommands").GetBoolean());

                var allowedPatterns = root.GetProperty("shellCommandPatterns").EnumerateArray().Select(e => e.GetString()).ToList();
                var deniedPatterns = root.GetProperty("deniedShellCommandPatterns").EnumerateArray().Select(e => e.GetString()).ToList();

                // Discriminator assertion: assert BOTH allow and refusal are present in the same response
                Assert.Contains("git *", allowedPatterns);
                Assert.Contains("rm *", deniedPatterns);
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1249: verifies GET /api/rooms/permissions distinguishes a room with no bindings file from
    /// a room whose bindings file lacks the requested worker, and neither is an error.
    /// </summary>
    [Fact]
    public async Task GetPermissions_NoBindingsFileAndMissingWorker_AnswerDistinguishably()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-getperm-dist-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                // 1. Room with NO bindings file at all
                var responseNoBindings = await client.GetAsync(
                    $"{baseUrl}/api/rooms/permissions?directoryPath={Uri.EscapeDataString(roomDir)}",
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.OK, responseNoBindings.StatusCode);
                var jsonNoBindings = await responseNoBindings.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                using var docNoBindings = JsonDocument.Parse(jsonNoBindings);
                Assert.Equal(nameof(StandingPermissionReadOutcome.NoWorkerSetup), docNoBindings.RootElement.GetProperty("outcome").GetString());
                Assert.False(docNoBindings.RootElement.GetProperty("runShellCommands").GetBoolean());
                Assert.Empty(docNoBindings.RootElement.GetProperty("shellCommandPatterns").EnumerateArray());
                Assert.Empty(docNoBindings.RootElement.GetProperty("deniedShellCommandPatterns").EnumerateArray());

                // 2. Room with bindings file, but missing the requested worker
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        ["other-worker"] = new(
                            "claude",
                            new WorkerContract("other-worker", [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant()),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var responseWorkerMissing = await client.GetAsync(
                    $"{baseUrl}/api/rooms/permissions?directoryPath={Uri.EscapeDataString(roomDir)}&workerName=chat-worker",
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.OK, responseWorkerMissing.StatusCode);
                var jsonWorkerMissing = await responseWorkerMissing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                using var docWorkerMissing = JsonDocument.Parse(jsonWorkerMissing);
                Assert.Equal(nameof(StandingPermissionReadOutcome.WorkerNotConfigured), docWorkerMissing.RootElement.GetProperty("outcome").GetString());
                Assert.False(docWorkerMissing.RootElement.GetProperty("runShellCommands").GetBoolean());
                Assert.Empty(docWorkerMissing.RootElement.GetProperty("shellCommandPatterns").EnumerateArray());
                Assert.Empty(docWorkerMissing.RootElement.GetProperty("deniedShellCommandPatterns").EnumerateArray());
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1249: the read takes the room-events guard, because the write it races truncates before it
    /// writes. Without the guard this route can answer "grants nothing" out of a half-written file.
    /// Second half of the test is the control arm: released, the identical call succeeds.
    /// </summary>
    [Fact]
    public async Task GetPermissions_WhileRoomEventsLockHeld_Returns503_RatherThanReadingATornFile()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var roomDir = Path.Combine(Path.GetTempPath(), $"daemon-getperm-lock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(roomDir);
            try
            {
                var bindingsPath = Path.Combine(roomDir, "bindings.json");
                await WorkerBindingConfigWriter.SaveToFileAsync(
                    new Dictionary<string, WorkerBindingConfigEntry>
                    {
                        [InteractiveSessionMaterializer.DefaultWorkerName] = new(
                            "claude",
                            new WorkerContract(InteractiveSessionMaterializer.DefaultWorkerName, [], [], []),
                            "Chat.",
                            TimeSpan.FromMinutes(5),
                            PermissionGrant: new PermissionGrant(RunShellCommands: true)),
                    },
                    bindingsPath,
                    TestContext.Current.CancellationToken);

                var url = $"{baseUrl}/api/rooms/permissions?directoryPath={Uri.EscapeDataString(roomDir)}";

                using (Aer.Flow.Concurrency.ConcurrencyGuard.AcquireRoomEvents(roomDir, "test read lock hold"))
                {
                    var held = await client.GetAsync(url, TestContext.Current.CancellationToken);
                    Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, held.StatusCode);
                }

                var afterRelease = await client.GetAsync(url, TestContext.Current.CancellationToken);
                Assert.Equal(System.Net.HttpStatusCode.OK, afterRelease.StatusCode);

                // Asserted, not assumed: the control arm has to prove the read reached the real grant,
                // or a route that always answered "grants nothing" would satisfy it too.
                using var doc = JsonDocument.Parse(await afterRelease.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.True(doc.RootElement.GetProperty("runShellCommands").GetBoolean());
            }
            finally
            {
                DirectoryCleanup.DeleteRecursively(roomDir);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// #1249: a room that does not exist is answered without being created. Acquiring a room-events
    /// lock calls Directory.CreateDirectory, so a guard taken before the bindings-file check would
    /// make this GET materialise the very room it was asked about.
    /// </summary>
    [Fact]
    public async Task GetPermissions_ForARoomThatDoesNotExist_DoesNotCreateIt()
    {
        var appBuilt = new TaskCompletionSource<Microsoft.AspNetCore.Builder.WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemonTask = DaemonHost.RunDaemonAsync(["--port", "0", "--no-mutex"], null, onBuilt: app => appBuilt.TrySetResult(app));
        var app = await appBuilt.Task;
        try
        {
            string baseUrl = "";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var server = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>();
                var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
                if (addresses is { Count: > 0 })
                {
                    baseUrl = addresses.First().TrimEnd('/');
                    break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon server binding
            }

            using var client = new HttpClient();
            var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
            if (File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            var absentRoomDir = Path.Combine(Path.GetTempPath(), $"daemon-getperm-absent-{Guid.NewGuid():N}");
            Assert.False(Directory.Exists(absentRoomDir));

            try
            {
                var response = await client.GetAsync(
                    $"{baseUrl}/api/rooms/permissions?directoryPath={Uri.EscapeDataString(absentRoomDir)}",
                    TestContext.Current.CancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(
                    nameof(StandingPermissionReadOutcome.NoWorkerSetup),
                    doc.RootElement.GetProperty("outcome").GetString());

                Assert.False(Directory.Exists(absentRoomDir));
            }
            finally
            {
                if (Directory.Exists(absentRoomDir))
                {
                    DirectoryCleanup.DeleteRecursively(absentRoomDir);
                }
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

