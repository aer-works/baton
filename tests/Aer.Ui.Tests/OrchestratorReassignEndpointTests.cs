using System.Net;
using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Ui.Core;
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

    /// <summary>
    /// Deliberately no <c>InitialMessage</c>: with one, <c>/api/sessions/start</c> fires a
    /// background turn (<c>Task.Run</c>) that re-reads and re-saves <c>SessionMetadata</c> on its
    /// own schedule, last-writer-wins -- a race against <see cref="SeedSecondParticipantAsync"/>'s
    /// direct write that silently dropped the seeded participant when the turn's own save landed
    /// last (measured: <c>Single(p =&gt; p.Id == second.Id)</c> throwing "no matching element").
    /// These tests only need materialization's synchronous first-participant/orchestrator setup,
    /// not a turn, so leaving the message out removes the race instead of racing it out.
    /// </summary>
    private async Task<SessionMetadata> StartStubSessionAsync()
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            RoomName: "orch-reassign-test-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// Second-reader finding on #592: seeds room.json's <c>Participants</c> with a target that has
    /// no matching <c>WorkerJoined</c> in room.jsonl -- the metadata/journal mismatch the endpoint's
    /// own re-check cannot see coming, since it only re-checks <see cref="ConcurrencyGuard.IsHeld"/>,
    /// not journal/metadata agreement. This forces <c>RoomMutationInterface.ReassignOrchestratorAsync</c>
    /// to throw <see cref="InvalidRoomMutationException"/> from its own journal-only target check,
    /// pinning the fix's exception-type distinction: the endpoint's catch must surface this as a real
    /// failure (409) rather than the log-and-continue an <see cref="IOException"/> gets, since by this
    /// point metadata already committed the flip.
    /// </summary>
    [Fact]
    public async Task A_journal_level_refusal_after_metadata_has_already_flipped_surfaces_as_conflict_not_ok()
    {
        var started = await StartStubSessionAsync();
        var ghost = new Participant(new WorkerId("claude-ghost"), "claude-ghost", "claude", "sonnet", null, IsOrchestrator: false);

        var metadataPath = Path.Combine(started.RoomDirectoryPath, ".aer", AerPaths.RoomMetadataFileName);
        var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        // Deliberately no matching WorkerJoined appended to room.jsonl -- metadata claims a
        // participant the journal has never heard of.
        var updated = metadata with { Participants = [.. metadata.Participants ?? [], ghost] };
        await InteractiveSessionMaterializer.SaveMetadataAsync(updated, metadataPath, TestContext.Current.CancellationToken);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/orchestrator/reassign",
            new ReassignOrchestratorRequest(started.RoomDirectoryPath, ghost.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Second-reader finding on #592: <c>MainWindow.OnOrchestratorReassignRequestedAsync</c> must
    /// reload <c>SessionMetadata</c> into <see cref="ChatViewModel"/> after a successful reassign —
    /// ruling 2 means <see cref="RoomClient.LoadAsync"/>'s <c>RoomProjection</c> fetch alone cannot
    /// see the change, since <c>Participants</c> is metadata-only. This exercises the exact sequence
    /// the codebehind handler performs — <see cref="RoomClient.ReassignOrchestratorAsync"/> then
    /// <see cref="RoomClient.LoadSessionMetadataAsync"/>, fed straight into
    /// <see cref="ChatViewModel.LoadFromMetadata"/> — through the real client/daemon pair rather than
    /// the codebehind method itself, which has no headless-Avalonia seam in this suite. A poll or a
    /// timer tick is never awaited: if the fix regressed to relying on the 2s live-refresh tick, this
    /// asserts on the state before any tick could have fired.
    /// </summary>
    [Fact]
    public async Task ReassignThenReloadMetadata_TheSameSequenceTheDesktopHandlerRuns_ReflectsTheNewOrchestratorImmediately()
    {
        var started = await StartStubSessionAsync();
        var second = await SeedSecondParticipantAsync(started);

        var configStore = new LocalUiConfigurationStore(Path.Combine(Path.GetTempPath(), $"orch-reassign-config-{Guid.NewGuid():N}.json"));
        var viewModel = new MainWindowViewModel();
        var roomClient = new RoomClient(
            configStore,
            new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() },
            viewModel,
            bindingsFilePathProvider: () => "",
            mutationStarted: () => { },
            mutationFailed: () => { },
            reopenRoomAsync: (_, _) => Task.CompletedTask,
            daemonUrl: _baseUrl,
            spawnDaemonOnDemand: false);

        var refusal = await roomClient.ReassignOrchestratorAsync(started.RoomDirectoryPath, second.Id.Value, TestContext.Current.CancellationToken);
        Assert.Null(refusal);

        var reloaded = await roomClient.LoadSessionMetadataAsync(started.RoomDirectoryPath, TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        viewModel.Chat.LoadFromMetadata(reloaded, started.RoomDirectoryPath);

        Assert.True(viewModel.Chat.Participants.Single(p => p.Id == second.Id).IsOrchestrator);
        // The chip renders the FIRST participant's status (multi-chip is a later slice) -- the
        // original holder must read false, not just the target true, or a bug that always sets
        // every participant's IsOrchestrator true would pass the assertion above alone.
        Assert.False(viewModel.Chat.WorkerIsOrchestrator);
    }
}
