using System.Text.Json;
using Aer.Flow.Artifacts;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Tests.Shared;
using Aer.Ui.Core;
using Xunit;

namespace Aer.Daemon.Tests;

/// <summary>
/// #1168: the restart seam end to end. The pieces are individually tested elsewhere
/// (<see cref="RuntimePermissionDaemonTests"/> covers the reconcile's journal/registry effects;
/// ask-time persistence is 0015's durability; the loader's permission state is #1142's tests) — but
/// nothing drove the whole chain a real restart runs: the STARTUP entry point
/// (<see cref="DaemonHost.ReconcilePendingPermissionsAsync"/>, rooms enumeration included, from a
/// cold registry) through to <see cref="RoomProjectionLoader.LoadAsync"/>, the daemon's own load
/// path for what clients render. Green pieces composed wrong is exactly how the projection member
/// itself shipped dropped (#1142's review), so the assertion here is on the projection, not the
/// journal.
/// <para>
/// Scope, precisely (#1168 second reader): this proves the reconcile→projection CHAIN, not
/// production's startup ORDERING — Program.cs fires reconcile fire-and-forget while Kestrel is
/// already serving reads, and no reconcile path broadcasts, so a client can observe the
/// pre-reconcile projection in that window and not be pushed the correction. That gap is #1171,
/// deliberately not smuggled into these facts' claims.
/// </para>
/// </summary>
[Collection(PendingGateRegistryCollection.Name)]
public sealed class RestartGateRepresentationTests : IDisposable
{
    private readonly string _roomsDir;
    private readonly string _roomDir;

    public RestartGateRepresentationTests()
    {
        _roomsDir = Path.Combine(Path.GetTempPath(), $"daemon-restart-gate-{Guid.NewGuid():N}");
        _roomDir = Path.Combine(_roomsDir, "room-a");
        Directory.CreateDirectory(_roomDir);
        PendingGateRegistry.Clear();
    }

    public void Dispose()
    {
        PendingGateRegistry.Clear();
        if (Directory.Exists(_roomsDir))
        {
            DirectoryCleanup.DeleteRecursively(_roomsDir);
        }
    }

    /// <summary>The loader's room contract: a persisted snapshot (UI spec §3.1). One minimal step.</summary>
    private async Task PersistMinimalSnapshotAsync()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            new WorkflowDefinitionSnapshotId("snap-restart"),
            new WorkflowTemplateId("restart-gate-test"),
            1,
            [new WorkflowStepDefinition(
                new StepId("chat"), "chat", [], ["answer.md"], [], new RetryPolicy(3))]);
        await SnapshotBinder.PersistAsync(
            snapshot, Path.Combine(_roomDir, "snapshot.json"), TestContext.Current.CancellationToken);
    }

    private async Task WriteAskFileAsync(string requestId, DateTimeOffset askedAt, int timeoutSeconds)
    {
        var outputDir = Path.Combine(_roomDir, ArtifactManager.ArtifactsDirectoryName, "execution_ex-restart");
        Directory.CreateDirectory(outputDir);
        var askPayload = new { permissionRequestId = requestId, toolName = "Bash", inputJson = "{}", askedAt, timeoutSeconds };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, $"ask-{requestId}.json"),
            JsonSerializer.Serialize(askPayload),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_restart_within_the_asks_deadline_represents_the_gate_in_the_projection_clients_read()
    {
        await PersistMinimalSnapshotAsync();
        var askedAt = DateTimeOffset.UtcNow;
        await WriteAskFileAsync("req-restart-1", askedAt, timeoutSeconds: 300);

        // The restart: process state is gone (a cold PendingGateRegistry — the ctor cleared it),
        // only the disk survives. Run the SAME entry point Program.cs fires after builder.Build().
        // #1171: the startup site passes the WS broadcast — record what a connected client is
        // pushed, since reconcile journaling without pushing was exactly the found gap.
        var broadcasts = new List<(RoomProjection Projection, string RoomDir)>();
        await DaemonHost.ReconcilePendingPermissionsAsync(
            _roomsDir,
            broadcastStateAsync: (proj, dir) => { broadcasts.Add((proj, dir)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var projection = await RoomProjectionLoader.LoadAsync(_roomDir, TestContext.Current.CancellationToken);

        var pending = projection.PendingPermission;
        Assert.NotNull(pending);
        Assert.Equal("req-restart-1", pending!.PermissionRequestId);
        Assert.Equal("Bash", pending.ToolName);
        // The original ask instant survives the restart — a re-raise that re-stamped it would
        // silently extend the gate's lifetime past the deadline the worker recorded.
        Assert.Equal(askedAt, pending.AskedAt);

        Assert.True(PendingGateRegistry.TryGet("req-restart-1", out _));

        // The push a connected client received carries the same re-presented gate (#1171).
        var pushed = Assert.Single(broadcasts);
        Assert.Equal(_roomDir, pushed.RoomDir);
        Assert.Equal("req-restart-1", pushed.Projection.PendingPermission?.PermissionRequestId);
    }

    [Fact]
    public async Task A_restart_past_the_asks_deadline_expires_the_gate_instead_of_resurrecting_it()
    {
        await PersistMinimalSnapshotAsync();
        await WriteAskFileAsync("req-restart-2", DateTimeOffset.UtcNow.AddMinutes(-10), timeoutSeconds: 5);

        var broadcasts = new List<(RoomProjection Projection, string RoomDir)>();
        await DaemonHost.ReconcilePendingPermissionsAsync(
            _roomsDir,
            broadcastStateAsync: (proj, dir) => { broadcasts.Add((proj, dir)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var projection = await RoomProjectionLoader.LoadAsync(_roomDir, TestContext.Current.CancellationToken);

        // No zombie gate — and the expiry is an ANSWER in the history (#1142's family), so the
        // transcript discloses what happened to the ask rather than it vanishing.
        Assert.Null(projection.PendingPermission);
        var answer = Assert.Single(projection.PermissionAnswers);
        Assert.Equal("req-restart-2", answer.PermissionRequestId);
        Assert.True(answer.WasRevoked);
        Assert.Equal("expired_during_shutdown", answer.Reason);

        Assert.False(PendingGateRegistry.TryGet("req-restart-2", out _));
        Assert.True(File.Exists(Path.Combine(
            _roomDir, ArtifactManager.ArtifactsDirectoryName, "execution_ex-restart", "revoked-req-restart-2.json")));

        // The expiry is pushed too (#1171) — a client rendering the stale gate sees it retire.
        var pushed = Assert.Single(broadcasts);
        Assert.Null(pushed.Projection.PendingPermission);
        Assert.True(Assert.Single(pushed.Projection.PermissionAnswers).WasRevoked);
    }
}
