using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// 0054 §4/#1307's HTTP surface: <c>POST /api/sessions/send</c>'s addressing half. Mirrors
/// <see cref="OrchestratorReassignEndpointTests"/>'s shape (a real in-process daemon, the same
/// <c>DaemonIntegrationTests</c> collection, and the same hand-seeded second participant) for the
/// same reason -- the scoping pass's ruling 1: today's rooms cap at one participant, so a real
/// tagged/untagged/unknown proof needs synthetic multi-participant metadata rather than a live join
/// gesture, which does not exist yet.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class SessionSendAddressingEndpointTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();

    private string WsBaseUrl => "ws" + _baseUrl["http".Length..];

    public async ValueTask InitializeAsync()
    {
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = new SessionTurnStubAdapter(),
            ["agy"] = new SessionTurnStubAdapter(),
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };

        _daemon = await DaemonTestHost.StartAsync(stubAdapters);
        _baseUrl = _daemon.BaseUrl;

        for (var i = 0; i < 30; i++)
        {
            try
            {
                var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(100, TestContext.Current.CancellationToken); // wait-ok: fast polling for local daemon /api/version readiness check
            }
        }

        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();
    }

    /// <summary>Same deliberate omission as <c>OrchestratorReassignEndpointTests.StartStubSessionAsync</c>: no <c>InitialMessage</c>, so materialization's synchronous participant/orchestrator setup is all that runs -- no background turn racing the second participant seed below.</summary>
    private async Task<SessionMetadata> StartStubSessionAsync()
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            RoomName: "send-addressing-test-" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return metadata;
    }

    /// <summary>Hand-seeds a second participant -- the same synthetic-metadata seam <c>OrchestratorReassignEndpointTests.SeedSecondParticipantAsync</c> establishes. When <paramref name="asOrchestrator"/> is true, the FIRST participant is flipped off orchestrator as part of the same write, so the orchestrator is never list-position 0 -- a resolver that degenerated to <c>FirstOrDefault()</c> would then resolve to the wrong participant instead of passing by coincidence.</summary>
    private async Task<Participant> SeedSecondParticipantAsync(SessionMetadata started, bool asOrchestrator = false)
    {
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: asOrchestrator);

        var metadataPath = Path.Combine(started.RoomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        var existing = asOrchestrator
            ? (metadata.Participants ?? []).Select(p => p with { IsOrchestrator = false })
            : metadata.Participants ?? [];
        var updated = metadata with { Participants = [.. existing, second] };
        await InteractiveSessionMaterializer.SaveMetadataAsync(updated, metadataPath, TestContext.Current.CancellationToken);

        return second;
    }

    /// <summary>Ruling 4: a tag naming an existing participant is accepted, dispatches, and the response reports the SAME id back as ResolvedParticipantId -- a tagged send is never re-routed to the orchestrator.</summary>
    [Fact]
    public async Task Tagged_send_to_a_named_participant_resolves_to_that_participant()
    {
        var started = await StartStubSessionAsync();
        var second = await SeedSecondParticipantAsync(started);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: started.RoomDirectoryPath, Message: "hi", TargetParticipantId: second.Id),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendResponseBody>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(second.Id.Value, body.ResolvedParticipantId);
    }

    /// <summary>Ruling 4: an untagged send resolves daemon-side to the current orchestrator by STRUCTURAL lookup (<c>DaemonHost.ResolveOrchestrator</c>'s <c>IsOrchestrator</c> scan), not by list position -- the orchestrator here is the SECOND participant seeded (see <see cref="SeedSecondParticipantAsync"/>'s remarks), so a resolver that degenerated to <c>Participants.FirstOrDefault()</c> would resolve to the room-starting first participant and fail this assertion.</summary>
    [Fact]
    public async Task Untagged_send_resolves_to_the_current_orchestrator()
    {
        var started = await StartStubSessionAsync();
        var second = await SeedSecondParticipantAsync(started, asOrchestrator: true);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: started.RoomDirectoryPath, Message: "hi"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendResponseBody>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(second.Id.Value, body.ResolvedParticipantId);
    }

    /// <summary>Ruling 4: a tag naming nobody in the room is a 400 -- see <c>DaemonHost.ValidateSendTarget</c>'s own remarks for why.</summary>
    [Fact]
    public async Task Tagged_send_to_an_unknown_participant_returns_bad_request()
    {
        var started = await StartStubSessionAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: started.RoomDirectoryPath, Message: "hi", TargetParticipantId: new WorkerId("never-joined")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Ruling 3: the durable turn keeps the sender's own tag, never the resolved orchestrator -- an untagged send must leave <c>SessionTurn.TargetParticipantId</c> null even though the room in fact has a named orchestrator that answered it.</summary>
    [Fact]
    public async Task Untagged_send_records_a_null_target_on_the_durable_turn_not_the_resolved_orchestrator()
    {
        var started = await StartStubSessionAsync();
        await SeedSecondParticipantAsync(started);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: started.RoomDirectoryPath, Message: "hi"),
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        SessionMetadata? reloaded = null;
        for (var i = 0; i < 50; i++)
        {
            reloaded = await InteractiveSessionMaterializer.LoadMetadataAsync(
                Path.Combine(started.RoomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName),
                TestContext.Current.CancellationToken);
            if (reloaded is { Turns.Count: > 0 })
            {
                break;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: fast polling for the fire-and-forget turn to persist
        }

        Assert.NotNull(reloaded);
        Assert.NotEmpty(reloaded.Turns);
        Assert.Null(reloaded.Turns[^1].TargetParticipantId);
    }

    /// <summary>Ruling 7, the broadcast half (the <c>ToProjection</c> mapping half is <c>WebSocketProjectionFrameTests</c>'s job): <c>DaemonBroadcast.SendStateAsync</c> puts <c>SessionMetadata.Participants</c> onto the wire as the frame's <c>Participants</c> node. Proven against the connect-time snapshot push <see cref="DormantRoomSendTests"/> also relies on, taken AFTER a turn has already landed (so the push reflects the room's real, durable state rather than racing a fire-and-forget dispatch the WS connection has no ordering guarantee against).</summary>
    [Fact]
    public async Task A_pushed_projection_frame_carries_both_participants()
    {
        var started = await StartStubSessionAsync();
        var second = await SeedSecondParticipantAsync(started, asOrchestrator: true);
        var first = started.Participants!.Single(p => p.Id != second.Id);

        var sendResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: started.RoomDirectoryPath, Message: "hi"),
            TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode);

        SessionMetadata? afterSend = null;
        for (var i = 0; i < 100; i++)
        {
            var pollResponse = await _client.GetAsync($"{_baseUrl}/api/sessions/{started.SessionId}", TestContext.Current.CancellationToken);
            afterSend = await pollResponse.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
            if (afterSend is { Turns.Count: > 0 })
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken); // wait-ok: fast polling for the fire-and-forget turn to persist, same shape as SessionSendAddressingEndpointTests' own durable-turn test above
        }

        Assert.NotNull(afterSend);
        Assert.NotEmpty(afterSend.Turns);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // wait-ok: bounded wait for the connect-time snapshot push, same shape as DormantRoomSendTests' own WS reads
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var payload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);

        Assert.True(payload.TryGetProperty("Participants", out var participants));
        var ids = participants.EnumerateArray().Select(p => p.GetProperty("Id").GetString()).ToArray();
        Assert.Contains(first.Id.Value, ids);
        Assert.Contains(second.Id.Value, ids);
    }

    private sealed record SendResponseBody(string SessionId, string RoomDirectoryPath, string? ResolvedParticipantId);
}
