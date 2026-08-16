using System.Net;
using System.Net.Http.Json;
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

    /// <summary>Hand-seeds a second, non-orchestrator participant -- the same synthetic-metadata seam <c>OrchestratorReassignEndpointTests.SeedSecondParticipantAsync</c> establishes.</summary>
    private async Task<Participant> SeedSecondParticipantAsync(SessionMetadata started)
    {
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);

        var metadataPath = Path.Combine(started.RoomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        var updated = metadata with { Participants = [.. metadata.Participants ?? [], second] };
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

    /// <summary>Ruling 4: an untagged send resolves daemon-side to the current orchestrator -- the structural lookup <c>DaemonHost.ResolveOrchestrator</c> owns, proven here against synthetic metadata where the orchestrator is NOT the only participant, so the assertion cannot pass by there being nothing else it could resolve to.</summary>
    [Fact]
    public async Task Untagged_send_resolves_to_the_current_orchestrator()
    {
        var started = await StartStubSessionAsync();
        await SeedSecondParticipantAsync(started);
        var orchestratorId = started.Participants!.Single(p => p.IsOrchestrator).Id;

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: started.RoomDirectoryPath, Message: "hi"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendResponseBody>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(orchestratorId.Value, body.ResolvedParticipantId);
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

    private sealed record SendResponseBody(string SessionId, string RoomDirectoryPath, string? ResolvedParticipantId);
}
