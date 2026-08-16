using System.Net;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #592's HTTP surface: <c>POST /api/rooms/orchestrator/reassign</c>. Mirrors
/// <see cref="HeldWorkResolveEndpointTests"/>'s shape (a real in-process daemon, the same
/// <c>DaemonIntegrationTests</c> collection) for the same reason -- both are read-modify-write
/// endpoints whose refusal behaviour only a real daemon, not a unit test against the mutation
/// interface alone, can prove end to end.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class OrchestratorReassignEndpointTests : IAsyncLifetime
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

    private async Task<SessionMetadata> StartStubSessionAsync()
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            RoomName: "orch-reassign-test-" + Guid.NewGuid().ToString("N"),
            InitialMessage: "hello");

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return metadata;
    }

    /// <summary>
    /// Hand-seeds a second participant -- room.json's Participants half plus room.jsonl's
    /// WorkerJoined half -- the way #1305 lands them together at materialization. There is no live
    /// join gesture yet (multi-participant rooms are a later slice, 0054's build-slices table); this
    /// is what makes the reassignment endpoint's real-target arm testable ahead of it, exactly as
    /// the scoping pass's own change list expects ("Chat.Participants" being empty everywhere else
    /// today, per ruling 3).
    /// </summary>
    private async Task<Participant> SeedSecondParticipantAsync(SessionMetadata started)
    {
        var second = new Participant(new WorkerId("claude-2"), "claude-2", "claude", "sonnet", null, IsOrchestrator: false);

        var metadataPath = Path.Combine(started.RoomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        var updated = metadata with { Participants = [.. metadata.Participants ?? [], second] };
        await InteractiveSessionMaterializer.SaveMetadataAsync(updated, metadataPath, TestContext.Current.CancellationToken);

        var roomLogPath = Path.Combine(started.RoomDirectoryPath, "room.jsonl");
        await using var writer = new RoomEventLogWriter(roomLogPath);
        await writer.AppendAsync(
            new RoomEvent.WorkerJoined(second.Id, second.Name, second.Vendor, second.Model, second.Effort, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        return second;
    }

    private Task<int> OrchestratorAssignedCountAsync(string roomDirectoryPath)
        => CountAsync(roomDirectoryPath);

    private static async Task<int> CountAsync(string roomDirectoryPath)
    {
        var events = await new RoomEventLogReader(Path.Combine(roomDirectoryPath, "room.jsonl"))
            .ReadAllRoomEventsAsync(TestContext.Current.CancellationToken);
        return events.OfType<RoomEvent.OrchestratorAssigned>().Count();
    }

    [Fact]
    public async Task Missing_room_directory_path_returns_bad_request()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest("", "claude"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Nonexistent_room_directory_returns_bad_request()
    {
        var nonexistentRoom = Path.Combine(Path.GetTempPath(), "aer_orch_reassign_missing_" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest(nonexistentRoom, "claude"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Ruling 3: an unknown WorkerId is a bad request, not a conflict -- it is not a legal reassignment the room is refusing, it is not a room at all.</summary>
    [Fact]
    public async Task Unknown_worker_id_returns_bad_request()
    {
        var started = await StartStubSessionAsync();

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest(started.RoomDirectoryPath, "never-joined"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The endpoint's half of <c>OrchestratorReassignmentMutationTests</c>' no-op case: the HTTP call itself succeeds without touching metadata or journal.</summary>
    [Fact]
    public async Task Reassigning_to_the_current_holder_succeeds_without_a_new_journal_event()
    {
        var started = await StartStubSessionAsync();
        var currentHolderId = started.Participants!.Single().Id.Value;
        var beforeCount = await OrchestratorAssignedCountAsync(started.RoomDirectoryPath);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest(started.RoomDirectoryPath, currentHolderId),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(beforeCount, await OrchestratorAssignedCountAsync(started.RoomDirectoryPath));
    }

    /// <summary>The endpoint's half of the same polarity pair (see <c>OrchestratorReassignmentMutationTests</c>): a real reassignment flips metadata too, which only the HTTP path can prove.</summary>
    [Fact]
    public async Task Reassigning_to_a_different_participant_flips_metadata_and_appends_one_event()
    {
        var started = await StartStubSessionAsync();
        var second = await SeedSecondParticipantAsync(started);
        var beforeCount = await OrchestratorAssignedCountAsync(started.RoomDirectoryPath);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest(started.RoomDirectoryPath, second.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(beforeCount + 1, await OrchestratorAssignedCountAsync(started.RoomDirectoryPath));

        var metadataPath = Path.Combine(started.RoomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var reloaded = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.Participants!.Single(p => p.Id == second.Id).IsOrchestrator);
        Assert.False(reloaded.Participants!.Single(p => p.Id != second.Id).IsOrchestrator);
    }

    /// <summary>The refusal arm: a room whose pump is alive refuses reassignment with a 409, the same shape as the workflow switch's own refusal.</summary>
    [Fact]
    public async Task A_room_whose_pump_is_alive_refuses_with_conflict()
    {
        var started = await StartStubSessionAsync();
        var second = await SeedSecondParticipantAsync(started);
        using var liveRun = ConcurrencyGuard.Acquire(started.RoomDirectoryPath, "a live pump");

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest(started.RoomDirectoryPath, second.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
