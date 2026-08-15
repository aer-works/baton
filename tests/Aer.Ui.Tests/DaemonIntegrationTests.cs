using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Templates;
using Aer.Tests.Shared;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aer.Ui.Tests;

[Collection("DaemonIntegrationTests")]
public class DaemonIntegrationTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();
    private string? _tempRoomDirectory;

    /// <summary>Reads a fleet response the way a real client does — with the string-enum converter
    /// (#1049): the daemon serializes RoomFleetItem.Status as its enum name ("NeedsYou", …), so a
    /// default-options deserialize throws on it. Mirrors RoomClient.DefaultJsonOptions.</summary>
    private static readonly JsonSerializerOptions FleetReadOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The daemon's dynamically-assigned base URL (issue #296), reused for the WebSocket
    /// endpoints below rather than the old hardcoded "ws://localhost:5050".</summary>
    private string WsBaseUrl => "ws" + _baseUrl["http".Length..];

    public async ValueTask InitializeAsync()
    {
        // Start Daemon on a dynamically OS-assigned port (issue #296) — a hardcoded port collides
        // whenever two test runs happen to overlap.
        _daemon = await DaemonTestHost.StartAsync();
        _baseUrl = _daemon.BaseUrl;

        // Wait for daemon to spin up
        for (int i = 0; i < 30; i++)
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
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        // Configure client authorization header. The daemon wrote its token under the redirected
        // AER_HOME root (see tests/Shared/AerHomeRedirect.cs), so read it back through AerPaths --
        // reading the literal ~/.aer here would look at the real per-user dir the redirect avoids.
        var aerDir = AerPaths.Root;
        var tokenFile = Path.Combine(aerDir, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        // Create a temporary room directory for testing
        _tempRoomDirectory = Path.Combine(Path.GetTempPath(), "aer_daemon_test_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoomDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop this test's own daemon (DaemonTestInstance stops the app it captured, not the shared
        // static DaemonHost.App, which can point at a superseded instance by now).
        if (_daemon != null)
        {
            await _daemon.DisposeAsync();
        }

        _client.Dispose();

        if (_tempRoomDirectory != null && Directory.Exists(_tempRoomDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_tempRoomDirectory);
        }
    }

    // M21 Phase 3 (issue #234): the Enable Remote Access view's toggle reads this field to know
    // whether the daemon it's already talking to is bound loopback-only or --remote — this test
    // daemon is started with neither flag (InitializeAsync's --port/--no-mutex only), so it must
    // report false.
    [Fact]
    public async Task GetVersion_ReportsIsRemote_FalseForALoopbackOnlyDaemon()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/version", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var meta = await response.Content.ReadFromJsonAsync<DaemonVersionInfo>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(meta);
        Assert.False(meta.IsRemote);
    }

    [Fact]
    public async Task GetRecentRooms_ReturnsOk()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/rooms/recent", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        var recent = await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(recent);
    }

    [Fact]
    public async Task OpenRoom_WithMissingDirectory_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/rooms/open", new OpenRoomRequest(""), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenRoom_WithInvalidDirectory_ReturnsBadRequest()
    {
        var invalidDir = Path.Combine(_tempRoomDirectory!, "non_existent_folder_abc_123");
        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/rooms/open", new OpenRoomRequest(invalidDir), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenRoom_WhileFlowLockHeld_ReturnsBadRequestWithNonEmptyMessage()
    {
        // #324: a task whose flow.lock is held by another writer is still readable -- ConcurrencyGuard
        // locks only flow.lock, never snapshot.json/flow.jsonl -- so /api/rooms/open would happily load
        // it and hand the caller a projection for a task another client is actively mutating, and on the
        // paths where LoadAsync produced no message it returned a bare 400 with an empty body the client
        // could not explain. Holding the lock must now yield a 400 whose body carries a sentence a UI can
        // show. CreatePausedRoomDirectoryAsync writes the directory's files directly (no ConcurrencyGuard),
        // so only this explicit Acquire puts flow.lock in the held state under test.
        const string executionId = "exec-locked-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);

        using var guard = ConcurrencyGuard.Acquire(roomDirectory);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/open", new OpenRoomRequest(roomDirectory), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(body));
        Assert.Contains("another client", body);
    }

    [Fact]
    public async Task Pairing_Flow_Succeeds_And_Enables_Auth()
    {
        // 1. Get pairing code (authenticated via loopback token)
        var codeResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        Assert.True(codeResponse.IsSuccessStatusCode);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();
        Assert.NotNull(code);

        // 2. Pair remote client (public POST, no auth headers on request client)
        using var remoteClient = new HttpClient();
        var pairRequest = new { Code = code, ClientName = "Test Mobile App" };
        var pairResponse = await remoteClient.PostAsJsonAsync($"{_baseUrl}/api/pairing/pair", pairRequest, TestContext.Current.CancellationToken);
        Assert.True(pairResponse.IsSuccessStatusCode);
        var pairData = await pairResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var pairedToken = pairData.GetProperty("token").GetString();
        Assert.NotNull(pairedToken);

        // 3. Make a request using the newly paired token (should be authorized)
        remoteClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pairedToken);
        var recentTasksResponse = await remoteClient.GetAsync($"{_baseUrl}/api/rooms/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, recentTasksResponse.StatusCode);
    }

    [Fact]
    public async Task Pairing_With_Invalid_Code_Returns_BadRequest()
    {
        using var remoteClient = new HttpClient();
        var pairRequest = new { Code = "999999", ClientName = "Test Mobile App" };
        var pairResponse = await remoteClient.PostAsJsonAsync($"{_baseUrl}/api/pairing/pair", pairRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, pairResponse.StatusCode);
    }

    [Fact]
    public async Task Pairing_Locks_Out_After_Max_Failed_Attempts()
    {
        // A real code is active, but every guess below is deliberately wrong — proving the
        // pairing endpoint can't be brute-forced across its 60s validity window: after enough
        // wrong guesses, even the correct code is rejected until a fresh one is generated.
        var codeResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();
        var wrongCode = code == "000000" ? "111111" : "000000";

        using var remoteClient = new HttpClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongResponse = await remoteClient.PostAsJsonAsync(
                $"{_baseUrl}/api/pairing/pair", new { Code = wrongCode, ClientName = "Attacker" }, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrongResponse.StatusCode);
        }

        // Attempts are now exhausted — even the real code must be rejected.
        var finalResponse = await remoteClient.PostAsJsonAsync(
            $"{_baseUrl}/api/pairing/pair", new { Code = code, ClientName = "Test Mobile App" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, finalResponse.StatusCode);
    }

    [Fact]
    public async Task Request_Without_Token_Is_Rejected_With_401()
    {
        using var remoteClient = new HttpClient();
        var response = await remoteClient.GetAsync($"{_baseUrl}/api/rooms/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // M21 Phase 6 (#243): PairedClientsStore could add a client but never remove one — the missing
    // revocation path M20 deferred until "whichever milestone builds the actual remote client".
    private async Task<(string ClientId, string Token)> PairANewClientAsync(string name)
    {
        var codeResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/code", TestContext.Current.CancellationToken);
        var codeData = await codeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var code = codeData.GetProperty("code").GetString();

        using var remoteClient = new HttpClient();
        var pairResponse = await remoteClient.PostAsJsonAsync(
            $"{_baseUrl}/api/pairing/pair", new { Code = code, ClientName = name }, TestContext.Current.CancellationToken);
        var pairData = await pairResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var token = pairData.GetProperty("token").GetString()!;

        var clientsResponse = await _client.GetAsync($"{_baseUrl}/api/pairing/clients", TestContext.Current.CancellationToken);
        var clients = await clientsResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var clientId = clients.EnumerateArray().Last(c => c.GetProperty("name").GetString() == name).GetProperty("clientId").GetString()!;

        return (clientId, token);
    }

    [Fact]
    public async Task RevokePairedClient_CausesNextRequest_ToBeUnauthorized()
    {
        var (clientId, token) = await PairANewClientAsync("Revocation Test Device");

        using var pairedClient = new HttpClient();
        pairedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var beforeRevoke = await pairedClient.GetAsync($"{_baseUrl}/api/rooms/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, beforeRevoke.StatusCode);

        var deleteResponse = await _client.DeleteAsync($"{_baseUrl}/api/pairing/clients/{clientId}", TestContext.Current.CancellationToken);
        Assert.True(deleteResponse.IsSuccessStatusCode);

        var afterRevoke = await pairedClient.GetAsync($"{_baseUrl}/api/rooms/recent", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task RevokeUnknownClientId_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"{_baseUrl}/api/pairing/clients/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PairedClient_CannotListOrRevokeOtherDevices()
    {
        var (_, token) = await PairANewClientAsync("Non-Owner Device");
        using var pairedClient = new HttpClient();
        pairedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var listResponse = await pairedClient.GetAsync($"{_baseUrl}/api/pairing/clients", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, listResponse.StatusCode);

        var deleteResponse = await pairedClient.DeleteAsync($"{_baseUrl}/api/pairing/clients/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    // M21 Phase 2 (#232): /api/rooms/artifact — the only way a client with no access to the
    // daemon host's filesystem (Aer.Mobile) can see what it's approving.
    private static readonly StepId WorkerStep = new("worker");

    private static async Task<string> CreateRoomDirectoryWithArtifactAsync(
        string executionId, string fileName, string content, CancellationToken cancellationToken)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("single-step"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", ["goal"], [fileName], DependsOn: [], RetryPolicy: new RetryPolicy(1))]));

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"aer_daemon_artifact_test_{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), cancellationToken);

        var request = new ExecutionRequest(
            new ExecutionId(executionId),
            new WorkflowId("wf-1"),
            WorkerStep,
            "worker",
            Inputs: [],
            Outputs: [fileName],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId(executionId)), cancellationToken);
        }

        var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, fileName), content, cancellationToken);

        return roomDirectory;
    }

    [Fact]
    public async Task GetArtifact_WithKnownExecutionAndFile_ReturnsItsContent()
    {
        var roomDirectory = await CreateRoomDirectoryWithArtifactAsync(
            "exec-1", "result.txt", "The output.", TestContext.Current.CancellationToken);

        var response = await _client.GetAsync(
            $"{_baseUrl}/api/rooms/artifact?directoryPath={Uri.EscapeDataString(roomDirectory)}&executionId=exec-1&fileName=result.txt",
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("The output.", body.GetProperty("content").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task GetArtifact_WithFileNameNotInExecutionsOutputs_ReturnsNotFound()
    {
        var roomDirectory = await CreateRoomDirectoryWithArtifactAsync(
            "exec-1", "result.txt", "The output.", TestContext.Current.CancellationToken);

        // Neither a real output of exec-1 nor a real path — this is the path-traversal guard:
        // fileName must appear in the execution's own recorded OutputFiles, nothing else.
        var response = await _client.GetAsync(
            $"{_baseUrl}/api/rooms/artifact?directoryPath={Uri.EscapeDataString(roomDirectory)}&executionId=exec-1&fileName=..%2f..%2fsecrets.txt",
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetArtifact_WithMissingQueryParameters_ReturnsBadRequest()
    {
        var response = await _client.GetAsync(
            $"{_baseUrl}/api/rooms/artifact?directoryPath=&executionId=&fileName=",
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> CreatePausedRoomDirectoryAsync(string executionId, CancellationToken cancellationToken)
    {
        var snapshot = SnapshotBinder.Bind(new WorkflowDefinition(
            new WorkflowTemplateId("single-step-gate"),
            WorkflowTemplateVersion: 1,
            Steps: [new WorkflowStepDefinition(WorkerStep, "worker", ["goal"], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1), PausePoint: new PausePoint([]))]));

        var roomDirectory = Path.Combine(Path.GetTempPath(), $"aer_daemon_paused_test_{Guid.NewGuid():N}");
        await SnapshotBinder.PersistAsync(snapshot, Path.Combine(roomDirectory, "snapshot.json"), cancellationToken);

        var request = new ExecutionRequest(
            new ExecutionId(executionId),
            new WorkflowId("wf-1"),
            WorkerStep,
            "worker",
            Inputs: [],
            Outputs: ["out"],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(Path.Combine(roomDirectory, "flow.jsonl")))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), cancellationToken);
            await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(new ExecutionId(executionId)), cancellationToken);
            await writer.AppendAsync(new FlowEvent.WorkflowPaused(new ExecutionId(executionId), WorkerStep), cancellationToken);
        }

        return roomDirectory;
    }

    private static async Task<string> WriteRejectableBindingsAsync(CancellationToken cancellationToken)
    {
        // "claude" (the real, registered adapter -- the daemon has no "shell" stub) resolves to a
        // command-line descriptor only (ClaudeWorkerAdapter.Resolve builds args, never spawns a
        // process) -- WorkerBindingResolver.Resolve calls this eagerly for every entry regardless of
        // decision type, but Reject itself never dispatches the resolved binding, so no real `claude`
        // process is ever started by this test.
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "claude", new WorkerContract("worker", ["goal"], [new ProducedOutput("out")], []),
                "irrelevant, never dispatched", TimeSpan.FromSeconds(30)),
        };

        var directory = Path.Combine(Path.GetTempPath(), $"aer_daemon_bindings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), cancellationToken);
        return path;
    }

    /// <summary>
    /// Names an adapter no registry entry can resolve -- <see cref="Aer.Adapters.WorkerBindingResolver.Resolve"/>
    /// throws <see cref="Aer.Adapters.UnknownWorkerAdapterException"/> (an <see cref="Aer.Flow.AerFlowException"/>)
    /// synchronously, before <c>RunCommand.ExecuteAsync</c> ever reaches <c>MutationInterface.StartWorkflowAsync</c>
    /// -- a fast, deterministic way to exercise <see cref="RoomClient.RunAsync"/>'s failure path with no live
    /// vendor CLI involved.
    /// </summary>
    private static async Task<string> WriteUnresolvableBindingsAsync(CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["worker"] = new WorkerBindingConfigEntry(
                "not-a-registered-adapter", new WorkerContract("worker", ["goal"], [new ProducedOutput("out")], []),
                "irrelevant, never dispatched", TimeSpan.FromSeconds(30)),
        };

        var directory = Path.Combine(Path.GetTempPath(), $"aer_daemon_bindings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config), cancellationToken);
        return path;
    }

    [Fact]
    public async Task Reject_TriggersASecondWebSocketBroadcast_SoAPhoneSeesTheDecisionLand()
    {
        // The connect-time snapshot push (proven by the DirectoryPath test above) is only half of
        // what Aer.Mobile's decision inbox depends on -- the other half, never previously exercised
        // by any test, is that POSTing a decision actually triggers a *second* broadcast to every
        // connected socket. /api/rooms/decide dispatches on a background Task.Run and returns 200
        // immediately (fire-and-forget, see Program.cs), so a missing broadcast here would look
        // identical to the phone: 200 OK, card never updates. See RoomClient.DecideAsync's
        // in-process fallback, which reaches the daemon's reopenTaskAsync -> BroadcastStateAsync path.
        const string executionId = "exec-reject-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);

        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/open", new OpenRoomRequest(roomDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        // DecideCommand always loads a bindings file, regardless of decision type (Aer.Cli's
        // DecideCommand.cs) -- set it directly on the daemon's DI-registered BindingsPathHolder,
        // *after* /api/rooms/open (which overwrites it from LoadLastBindingsFilePathAsync's own
        // remembered value), rather than through /api/rooms/run, which would persist to the real
        // per-user %APPDATA%\Aer.Ui\recent-room-directories.json convenience file
        // (LocalUiConfigurationStore.CreateDefault(), Program.cs:113) -- this test must not leave
        // that behind on whatever machine runs it.
        var bindingsFilePath = await WriteRejectableBindingsAsync(TestContext.Current.CancellationToken);
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath = bindingsFilePath;
        // #1230 / decision 0056: the room carries its own bindings, and a decision resolves those
        // rather than the slot above. Production puts this copy here at run time; this test builds its
        // room directly, so it does the same. The slot assignment stays: without it this test would
        // pass whether or not the endpoint still reads the slot, and the point is that it does not.
        File.Copy(bindingsFilePath, AerPaths.RoomBindingsFile(roomDirectory));

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        var first = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var firstPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count)).RootElement;
        Assert.Equal("Paused", firstPayload.GetProperty("State").GetProperty("Status").GetString());

        var decideResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(roomDirectory, WorkerStep.Value, executionId, DecisionType.Reject),
            TestContext.Current.CancellationToken);
        Assert.True(decideResponse.IsSuccessStatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var second = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var secondPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, second.Count)).RootElement;

        Assert.Equal("Terminal", secondPayload.GetProperty("State").GetProperty("Status").GetString());
        Assert.Equal(roomDirectory, secondPayload.GetProperty("DirectoryPath").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunRoom_TriggersTwoWebSocketBroadcasts_ImmediateAndOnCompletion()
    {
        // #330: /api/rooms/run is the endpoint the desktop's own RoomClient.RunAsync HTTP branch
        // posts to (HomeView.OnStartTemplateClick -> MainWindow.RunAsync) once a template has already
        // been materialized in-process. Unlike its siblings /api/rooms/open and /api/templates/run,
        // this endpoint used to have zero broadcast-related code at all before dispatching the
        // background pump -- a paired phone, already watching some other directory, learned nothing
        // about this one until the whole run eventually finished. No prior /api/rooms/open here
        // deliberately: the paused-with-nothing-ready task below resumes as an instant no-op, so
        // RunAsync's own *pre-existing* completion broadcast (reopenTaskAsync, unchanged by this fix)
        // alone would already deliver exactly one message here -- asserting on two, not just "at least
        // one", is what actually isolates and proves /api/rooms/run's new pre-broadcast specifically
        // adds a second, earlier one rather than just riding the completion broadcast that already
        // existed.
        const string executionId = "exec-run-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var bindingsFilePath = await WriteRejectableBindingsAsync(TestContext.Current.CancellationToken);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var runResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);
        Assert.True(runResponse.IsSuccessStatusCode);

        var buffer = new byte[1024 * 64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using (var linked1 = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken))
        {
            var first = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked1.Token);
            var firstPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count)).RootElement;
            Assert.Equal(roomDirectory, firstPayload.GetProperty("DirectoryPath").GetString());
            Assert.Equal("Paused", firstPayload.GetProperty("State").GetProperty("Status").GetString());
        }

        using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var second = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked2.Token);
        var secondPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, second.Count)).RootElement;
        Assert.Equal(roomDirectory, secondPayload.GetProperty("DirectoryPath").GetString());
        Assert.Equal("Paused", secondPayload.GetProperty("State").GetProperty("Status").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunRoom_ThatFailsInTheBackground_StillTriggersASecondWebSocketBroadcast()
    {
        // #330: RoomClient.RunAsync's in-process fallback (what Aer.Daemon's own singleton session
        // always uses) used to return straight out of its `catch (AerFlowException ex)` block without
        // ever calling reopenTaskAsync -- so a run that threw partway through never broadcast at all,
        // for either a mobile- or desktop-initiated run. A connected phone would see the pre-run
        // broadcast above and then nothing again, forever, even though the run had already stopped.
        // The unresolvable-adapter bindings below make RunCommand.ExecuteAsync throw synchronously,
        // before touching the snapshot -- so this run's own failure broadcast (unlike the pre-broadcast
        // above) is the ONLY thing distinguishing "the run stopped" from "still running" here.
        const string executionId = "exec-run-fail-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var bindingsFilePath = await WriteUnresolvableBindingsAsync(TestContext.Current.CancellationToken);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var runResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/run",
            new RunRoomRequest(roomDirectory, null, bindingsFilePath),
            TestContext.Current.CancellationToken);
        Assert.True(runResponse.IsSuccessStatusCode);

        var buffer = new byte[1024 * 64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // First message: /api/rooms/run's own pre-broadcast (proven above), reflecting the still-Paused
        // state from before the background run even started.
        using (var linked1 = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken))
        {
            var first = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked1.Token);
            var firstPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, first.Count)).RootElement;
            Assert.Equal("Paused", firstPayload.GetProperty("State").GetProperty("Status").GetString());
        }

        // Second message: only possible if RunAsync's catch block now also calls reopenTaskAsync on
        // failure -- the background Task.Run's own binding-resolution failure never mutates the
        // snapshot, so this is the same "Paused" state again, but a *second* broadcast that would not
        // exist at all without this fix.
        using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var second = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked2.Token);
        var secondPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, second.Count)).RootElement;
        Assert.Equal("Paused", secondPayload.GetProperty("State").GetProperty("Status").GetString());
        Assert.Equal(roomDirectory, secondPayload.GetProperty("DirectoryPath").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocketSnapshot_IncludesDirectoryPath_SoAClientThatNeverCalledOpenCanStillDecide()
    {
        // A client that only ever observes the WS stream (Aer.Mobile — the task was opened by the
        // desktop, not by this client) has no other way to learn the directoryPath that
        // /api/rooms/decide and /api/rooms/cancel require.
        var roomDirectory = await CreateRoomDirectoryWithArtifactAsync(
            "exec-1", "result.txt", "The output.", TestContext.Current.CancellationToken);
        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/open", new OpenRoomRequest(roomDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        var payload = JsonDocument.Parse(json).RootElement;

        Assert.Equal(roomDirectory, payload.GetProperty("DirectoryPath").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// #1240: the WS payload carries the DERIVED room-card status, both halves — see
    /// <c>DaemonBroadcast.DeriveRoomCardStatus</c> for why a remote client cannot compute it.
    /// </summary>
    /// <remarks>
    /// The discriminator is that the two disagree here: this room's raw <c>State.Status</c> is
    /// Paused while the derived pair says NeedsYou / "Waiting for your review". A payload echoing
    /// the raw status under the new name would pass an assertion on either field alone.
    /// </remarks>
    [Fact]
    public async Task WebSocketSnapshot_IncludesTheDerivedRoomCardStatus_WhichNoRemoteClientCanCompute()
    {
        const string executionId = "exec-card-status-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/open", new OpenRoomRequest(roomDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), TestContext.Current.CancellationToken);
        var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        var payload = JsonDocument.Parse(json).RootElement;

        Assert.Equal("Paused", payload.GetProperty("State").GetProperty("Status").GetString());
        Assert.Equal("NeedsYou", payload.GetProperty("RoomCardStatus").GetString());
        Assert.Equal("Waiting for your review", payload.GetProperty("RoomCardStatusText").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetTemplates_ReturnsCatalogAndVendorPresence()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/templates", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var hasTemplates = body.TryGetProperty("templates", out var templates) || body.TryGetProperty("Templates", out templates);
        Assert.True(hasTemplates);
        Assert.Equal(5, templates.GetArrayLength());

        var hasVendors = body.TryGetProperty("availableVendors", out var vendors) || body.TryGetProperty("AvailableVendors", out vendors);
        Assert.True(hasVendors);
        Assert.True(vendors.GetArrayLength() > 0);
    }

    [Fact]
    public async Task RunTemplate_MaterializesAndStartsTaskWithoutCallerSuppliedPaths()
    {
        var request = new RunTemplateRequest(
            TemplateId: "solo-run",
            PrimaryAdapter: "claude",
            RoomName: "test-template-task-" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/templates/run", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var hasProp = body.TryGetProperty("roomDirectoryPath", out var dirProp) || body.TryGetProperty("RoomDirectoryPath", out dirProp);
        Assert.True(hasProp);
        var dirPath = dirProp.GetString();
        Assert.NotNull(dirPath);
        Assert.True(Directory.Exists(dirPath));
        Assert.True(File.Exists(Path.Combine(dirPath, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(dirPath, "bindings.json")));
    }

    [Theory]
    [InlineData("../../escaped-task")]
    [InlineData("..\\..\\escaped-task")]
    public async Task RunTemplate_WithPathTraversalTaskName_ReturnsBadRequest(string maliciousTaskName)
    {
        // Review follow-up (issue #250): RoomName used to be Path.Combine'd into the daemon-owned
        // tasks root with no containment check -- a crafted name could escape ~/.aer/tasks entirely
        // and make the daemon create/write files anywhere it can reach. This is exactly the
        // filesystem access the milestone's own design says a caller with only TemplateId/RoomName
        // (no real paths) should never get.
        if (maliciousTaskName.Contains('\\') && !OperatingSystem.IsWindows())
        {
            // '\' is not a path separator outside Windows, so this input never traverses out of the
            // tasks root there -- it's just a literal (contained) folder name, and OK is correct.
            return;
        }

        var request = new RunTemplateRequest(TemplateId: "solo-run", PrimaryAdapter: "claude", RoomName: maliciousTaskName);

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/templates/run", request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DecideWithArtifactReference_FileNameNotInExecutionsOutputs_ReturnsBadRequest()
    {
        // Same path-traversal guard as GetArtifact_WithFileNameNotInExecutionsOutputs_ReturnsNotFound
        // above, but for /api/rooms/decide's ArtifactReference resolution (M22 Phase 5) -- it used to
        // Path.Combine the caller-supplied FileName straight into the resolved output directory with
        // no check that it names a real output of that execution, letting a remote client (the exact
        // audience Phase 5 exists to serve without host filesystem access) pull an arbitrary host file
        // in as "reviewer feedback".
        const string executionId = "exec-artifact-ref-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "out"), "the real output", TestContext.Current.CancellationToken);

        var decideResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(
                roomDirectory, WorkerStep.Value, executionId, DecisionType.Reject,
                ArtifactReference: new ArtifactReference(executionId, "../../../secrets.txt")),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, decideResponse.StatusCode);
    }

    /// <summary>
    /// #1227: a decision the daemon cannot carry out is refused with a sentence, not accepted and
    /// dropped.
    /// <para>
    /// Found by driving the phone: the room screen said "Approved review", the daemon answered 200,
    /// and the room never moved. Why the daemon can end up without the bindings a decision needs, and
    /// what swallowed the failure, is on the guard itself in <c>Aer.Daemon</c>'s decide endpoint.
    /// </para>
    /// <para>
    /// Asserted on the status and the sentence, and deliberately NOT on "the journal did not grow".
    /// A second reader checked that one and found it vacuous: <c>DecideCommand</c> loads bindings
    /// before it ever constructs a <c>FlowEventLogWriter</c>, so the journal was untouched in the
    /// buggy version too. The thing this fix changes is what the person is told, and that is the only
    /// thing worth pinning — a fixture dressed up to look like it proves more would be worse than
    /// this comment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DecideOnARoomWhoseWorkersAreUnknown_IsRefusedRatherThanAcceptedAndDropped()
    {
        const string executionId = "exec-no-bindings-1";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(roomDirectory, WorkerStep.Value, executionId, DecisionType.Resume),
            TestContext.Current.CancellationToken);

        // The polarity that matters: this was 200 before #1227, over a room that never moved.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("doesn't know which workers this room runs", body);
    }

    /// <summary>
    /// #1246: decide in a room with no bindings.json through the desktop's own decide path
    /// (RoomClient.DecideAsync), and assert the room's bindings.json exists afterwards and contains
    /// the bindings from the desktop's provider.
    /// </summary>
    [Fact]
    public async Task DecideAsync_InRoomWithNoBindings_MaterializesRoomBindingsFromClientProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string executionId = "exec-unstick-1";

        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, cancellationToken);
        var clientBindingsFilePath = await WriteRejectableBindingsAsync(cancellationToken);
        var roomBindingsFile = AerPaths.RoomBindingsFile(roomDirectory);

        // Ensure room starts without its own bindings.json
        Assert.False(File.Exists(roomBindingsFile));

        var configStore = new LocalUiConfigurationStore(Path.Combine(_tempRoomDirectory!, $"config-{Guid.NewGuid():N}.json"));
        var viewModel = new MainWindowViewModel();
        var roomClient = new RoomClient(
            configStore,
            new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() },
            viewModel,
            bindingsFilePathProvider: () => clientBindingsFilePath,
            mutationStarted: () => { },
            mutationFailed: () => { },
            reopenRoomAsync: (_, _) => Task.CompletedTask,
            daemonUrl: _baseUrl,
            spawnDaemonOnDemand: false);

        var outcome = await roomClient.DecideAsync(
            roomDirectory,
            WorkerStep,
            new ExecutionId(executionId),
            DecisionType.Reject,
            targetStepId: null,
            revisionFilePath: null,
            supplementaryWorker: null,
            supplementaryOutputName: null,
            cancellationToken: cancellationToken);

        // The request was accepted, and that is ALL this asserts: /api/rooms/decide dispatches the
        // decision itself fire-and-forget, so ErrorMessage can only ever reflect the synchronous half
        // of the handler — which is the materialise-or-skip step this test is about. Read as "the
        // decision succeeded" it would be wrong.
        Assert.Null(outcome.ErrorMessage);
        Assert.True(File.Exists(roomBindingsFile));
        var clientContent = await File.ReadAllTextAsync(clientBindingsFilePath, cancellationToken);
        var roomContent = await File.ReadAllTextAsync(roomBindingsFile, cancellationToken);
        Assert.Equal(clientContent, roomContent);
    }

    /// <summary>
    /// #1246 discriminating control: the same decide in a room that ALREADY has bindings.json must
    /// leave that file's contents unchanged even if the client's provider supplies a different path.
    /// </summary>
    [Fact]
    public async Task DecideAsync_InRoomWithExistingBindings_LeavesRoomBindingsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string executionId = "exec-unstick-control-1";

        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, cancellationToken);
        var existingBindingsFilePath = await WriteUnresolvableBindingsAsync(cancellationToken);
        var roomBindingsFile = AerPaths.RoomBindingsFile(roomDirectory);
        File.Copy(existingBindingsFilePath, roomBindingsFile);
        var existingContent = await File.ReadAllTextAsync(roomBindingsFile, cancellationToken);

        var clientBindingsFilePath = await WriteRejectableBindingsAsync(cancellationToken);

        var configStore = new LocalUiConfigurationStore(Path.Combine(_tempRoomDirectory!, $"config-{Guid.NewGuid():N}.json"));
        var viewModel = new MainWindowViewModel();
        var roomClient = new RoomClient(
            configStore,
            new Dictionary<string, IWorkerAdapter> { ["claude"] = new ClaudeWorkerAdapter() },
            viewModel,
            bindingsFilePathProvider: () => clientBindingsFilePath,
            mutationStarted: () => { },
            mutationFailed: () => { },
            reopenRoomAsync: (_, _) => Task.CompletedTask,
            daemonUrl: _baseUrl,
            spawnDaemonOnDemand: false);

        var outcome = await roomClient.DecideAsync(
            roomDirectory,
            WorkerStep,
            new ExecutionId(executionId),
            DecisionType.Reject,
            targetStepId: null,
            revisionFilePath: null,
            supplementaryWorker: null,
            supplementaryOutputName: null,
            cancellationToken: cancellationToken);

        // Accepted, not "succeeded" — same caveat as the arm above, and it matters more here: this
        // room's bindings deliberately name an adapter nothing resolves, so the fire-and-forget
        // decide behind this 200 would fail if it ever ran. That is fine for what is being pinned.
        // The claim is only that the room's own file is untouched, and the gate that decides it is
        // pure file existence, checked synchronously before the response.
        Assert.Null(outcome.ErrorMessage);
        var finalContent = await File.ReadAllTextAsync(roomBindingsFile, cancellationToken);
        Assert.Equal(existingContent, finalContent);
        Assert.NotEqual(await File.ReadAllTextAsync(clientBindingsFilePath, cancellationToken), finalContent);
    }


    /// <summary>
    /// #1230 / decision 0056: a decision resolves the room's OWN bindings, never the user-global slot
    /// holding whichever file was run or opened last.
    /// </summary>
    /// <remarks>
    /// The discriminator is that the two rooms' bindings differ in a way the dispatch cannot hide:
    /// room A's adapter resolves, room B's does not. The slot is pointed at A and the decision is made
    /// in B. If B's own file is used, the unresolvable adapter fails loudly and lands in B's
    /// turn-errors log; if the slot wins — the bug — A's bindings resolve fine and no such error is
    /// ever written. Before this fix that silent success was the whole defect: the wrong workers, no
    /// refusal, no signal.
    /// </remarks>
    [Fact]
    public async Task DecideResolvesTheRoomsOwnWorkers_NotWhicheverRoomRanLast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string executionId = "exec-own-bindings-1";

        var otherRoomBindings = await WriteRejectableBindingsAsync(cancellationToken);
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, cancellationToken);
        File.Copy(
            await WriteUnresolvableBindingsAsync(cancellationToken),
            AerPaths.RoomBindingsFile(roomDirectory));

        // "Another room ran last, and its bindings are what the daemon remembers."
        DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>().BindingsFilePath = otherRoomBindings;

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(roomDirectory, WorkerStep.Value, executionId, DecisionType.Resume),
            cancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

        // The dispatch is fire-and-forget behind that 200, so the evidence lands in the room's own
        // error log (#341) rather than in the response.
        var errorLog = Path.Combine(roomDirectory, ".aer", "turn-errors.log");
        string log = "";
        for (var i = 0; i < 600 && !log.Contains("not-a-registered-adapter", StringComparison.Ordinal); i++)
        {
            if (File.Exists(errorLog))
            {
                using var stream = new FileStream(
                    errorLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                log = await reader.ReadToEndAsync(cancellationToken);
            }
            if (!log.Contains("not-a-registered-adapter", StringComparison.Ordinal))
            {
                // wait-ok: the ceiling is the loop bound, 600 iterations x 100ms = 60s, and the poll exits the moment the log carries the adapter name
                await Task.Delay(100, cancellationToken);
            }
        }

        Assert.Contains("not-a-registered-adapter", log);
    }

    /// <summary>
    /// #1230's second reader: cancel carried the same defect decide did. The mechanism is written
    /// out at the <c>/api/rooms/cancel</c> handler in <c>Program.cs</c>; what this pins is the
    /// outcome — the named room's own bindings win, per decision 0056.
    /// </summary>
    /// <remarks>
    /// The discriminator is the slot's value after the call. It is seeded with a DIFFERENT room's
    /// file, so an endpoint that leaves it alone — the defect — leaves that foreign path in place;
    /// only the guard moves it to this room's own. Asserting on the cancel's own outcome cannot
    /// discriminate: cancelling a paused room succeeds either way, which is exactly why the bug was
    /// silent.
    /// </remarks>
    [Fact]
    public async Task CancelPointsAtTheRoomsOwnWorkers_NotWhicheverRoomRanLast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string executionId = "exec-cancel-own-bindings-1";

        var otherRoomBindings = await WriteRejectableBindingsAsync(cancellationToken);
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, cancellationToken);
        var roomBindings = AerPaths.RoomBindingsFile(roomDirectory);
        File.Copy(await WriteUnresolvableBindingsAsync(cancellationToken), roomBindings);

        var pathHolder = DaemonHost.App!.Services.GetRequiredService<BindingsPathHolder>();
        pathHolder.BindingsFilePath = otherRoomBindings;

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/cancel",
            new CancelRoomRequest(roomDirectory, executionId),
            cancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(roomBindings, pathHolder.BindingsFilePath);
    }

    /// <summary>
    /// The other polarity of the traversal refusal above: a reference naming a REAL output of that
    /// execution gets past the reference check.
    /// <para>
    /// It is asserted on the message rather than on the status code since #1227. This daemon has no
    /// worker bindings — nothing here runs or opens a room — so the request is now refused a step
    /// later, on the grounds that a decision it cannot carry out must not be accepted and dropped.
    /// Asserting "not rejected for the reference reason" keeps this test discriminating on the thing
    /// it exists for: swap "out" for the sibling's traversal path and the message changes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DecideWithArtifactReference_WithKnownExecutionAndFile_PassesTheReferenceCheck()
    {
        const string executionId = "exec-artifact-ref-2";
        var roomDirectory = await CreatePausedRoomDirectoryAsync(executionId, TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(roomDirectory, "artifacts", $"execution_{executionId}");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "out"), "the real output", TestContext.Current.CancellationToken);

        var decideResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/decide",
            new DecideRoomRequest(
                roomDirectory, WorkerStep.Value, executionId, DecisionType.Reject,
                ArtifactReference: new ArtifactReference(executionId, "out")),
            TestContext.Current.CancellationToken);

        var body = await decideResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("does not name a known output file", body);
    }

    // M24 Phase 1 (#262): Interactive Sessions endpoint coverage. These deliberately never send an
    // InitialMessage/Message that would reach ExecuteSessionTurnAsync's real vendor dispatch --
    // that path shells out to whatever CLI the resolved adapter names, which isn't something a
    // default (non-smoke) test run can assume is installed or authenticated on the host (see
    // CLAUDE.md's live-vendor-smoke-tests section). Everything below only exercises
    // materialization, persistence, and request validation, which never touch a vendor process.
    private async Task<(string SessionId, string RoomDirectoryPath)> StartASessionAsync(string? roomName = null, string? workingDirectory = null)
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            RoomName: roomName ?? "test-session-" + Guid.NewGuid().ToString("N"),
            WorkingDirectory: workingDirectory);

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return (metadata.SessionId, metadata.RoomDirectoryPath);
    }

    [Fact]
    public async Task StartSession_WithNoInitialMessage_MaterializesAndReturnsDirectoryPath()
    {
        var (sessionId, roomDirectoryPath) = await StartASessionAsync();

        Assert.NotEmpty(sessionId);
        Assert.True(Directory.Exists(roomDirectoryPath));
        Assert.True(File.Exists(Path.Combine(roomDirectoryPath, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(roomDirectoryPath, "bindings.json")));
        Assert.True(File.Exists(Path.Combine(roomDirectoryPath, ".aer", "room.json")));
    }

    [Fact]
    public async Task StartSession_ThenGetById_ReturnsTheSamePersistedSession()
    {
        var (sessionId, roomDirectoryPath) = await StartASessionAsync();

        var getResponse = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
        Assert.True(getResponse.IsSuccessStatusCode);

        var metadata = await getResponse.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(sessionId, metadata.SessionId);
        Assert.Equal(roomDirectoryPath, metadata.RoomDirectoryPath);
    }

    [Fact]
    public async Task GetById_ForAnUnknownSessionId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/does-not-exist-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSessions_IncludesAJustStartedSession()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var sessions = await response.Content.ReadFromJsonAsync<List<SessionMetadata>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sessions);
        Assert.Contains(sessions, s => s.SessionId == sessionId);
    }

    [Fact]
    public async Task WebSocketSnapshot_IncludesSessionId_ForASessionDirectory()
    {
        // Aer.Mobile's chat UI (issue #262 follow-up): a phone whose _openDirectoryPath was seeded
        // from another client's push (never having called /api/sessions/start itself) has no other
        // way to learn this directory is an interactive session, or which SessionId to fetch turns
        // for, without this sibling -- see SendStateAsync's remarks in Program.cs.
        //
        // Deliberately not using StartASessionAsync/POST /api/sessions/start here: a session
        // materialized with no initial message never actually runs (Aer.Daemon's
        // ExecuteSessionTurnAsync only fires when InitialMessage is set), so it has no snapshot.json
        // yet -- RoomProjectionLoader.LoadAsync throws InvalidRoomDirectoryException for it, and
        // /api/ws's on-connect push silently never fires (LastLoadSucceeded stays false). That's a
        // pre-existing daemon quirk, not something to route around by invoking a real vendor CLI in
        // a test (this suite never does that -- see the other CreateXxxDirectoryAsync helpers,
        // which all hand-write snapshot.json/flow.jsonl directly). This helper does the same, plus a
        // hand-written .aer/session.json, standing in for what a session's first real completed turn
        // would leave behind.
        const string sessionId = "test-session-ws-1";
        var roomDirectory = await CreateSessionRoomDirectoryAsync(sessionId, TestContext.Current.CancellationToken);

        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/open", new OpenRoomRequest(roomDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var payload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;

        Assert.Equal(roomDirectory, payload.GetProperty("DirectoryPath").GetString());
        Assert.Equal(sessionId, payload.GetProperty("SessionId").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    private static async Task<string> CreateSessionRoomDirectoryAsync(string sessionId, CancellationToken cancellationToken)
    {
        var roomDirectory = await CreateRoomDirectoryWithArtifactAsync("exec-session-1", "response.md", "Hi there.", cancellationToken);

        var metadata = new SessionMetadata(
            sessionId,
            roomDirectory,
            CurrentAdapter: "claude",
            CurrentVendorSessionId: null,
            Model: null,
            WorkingDirectory: null,
            TurnCount: 1,
            SafetyCeiling: InteractiveSessionMaterializer.DefaultSafetyCeiling,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Turns: [new SessionTurn(0, "claude", "hello", "hi there", DateTimeOffset.UtcNow, false, false)]);
        await InteractiveSessionMaterializer.SaveMetadataAsync(metadata, Path.Combine(roomDirectory, ".aer", "room.json"), cancellationToken);

        return roomDirectory;
    }

    [Fact]
    public async Task WebSocketSnapshot_OmitsSessionId_ForAnOrdinaryRoomDirectory()
    {
        // A plain (non-session) room directory has no .aer/session.json -- confirms the new sibling
        // is additive and doesn't leak a stale/wrong SessionId onto unrelated task pushes.
        var roomDirectory = await CreateRoomDirectoryWithArtifactAsync(
            "exec-plain-1", "result.txt", "The output.", TestContext.Current.CancellationToken);
        var openResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/open", new OpenRoomRequest(roomDirectory), TestContext.Current.CancellationToken);
        Assert.True(openResponse.IsSuccessStatusCode);

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var buffer = new byte[1024 * 64];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var payload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement;

        Assert.Equal(roomDirectory, payload.GetProperty("DirectoryPath").GetString());
        Assert.False(payload.TryGetProperty("SessionId", out _));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendSessionMessage_WithEmptyMessage_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: _tempRoomDirectory, Message: ""),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendSessionMessage_WithNeitherDirectoryPathNorSessionId_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(Message: "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendSessionMessage_ForADirectoryThatIsNotASessionDirectory_ReturnsBadRequest()
    {
        // A real, existing directory (satisfies Directory.Exists) that was never materialized by
        // InteractiveSessionMaterializer -- no .aer/session.json, so metadata load must fail closed.
        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/send",
            new SendSessionMessageRequest(DirectoryPath: _tempRoomDirectory, Message: "hello"),
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("agy")]
    public async Task GetAdapterCapabilities_ReturnsOkWithTheRequestedVendor(string adapter)
    {
        // Neither adapter's DiscoverCapabilitiesAsync shells out in a way that throws when its CLI
        // is missing or unauthenticated -- Claude's is filesystem-only, Gemini's degrades each
        // subcommand to null on Win32Exception/InvalidOperationException (AgyWorkerAdapter.cs) --
        // so this is safe to assert on regardless of what's installed on the host.
        var response = await _client.GetAsync($"{_baseUrl}/api/adapters/capabilities?adapter={adapter}", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var capabilities = await response.Content.ReadFromJsonAsync<WorkerCapabilities>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(capabilities);
        Assert.Equal(adapter, capabilities.Vendor);
        Assert.Contains(capabilities.Items, i => i.Name == "/compact");
    }

    [Fact]
    public async Task GetAdapterCapabilities_WithUnknownAdapterName_FallsBackToClaude()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/adapters/capabilities?adapter=not-a-real-vendor", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var capabilities = await response.Content.ReadFromJsonAsync<WorkerCapabilities>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(capabilities);
        Assert.Equal("claude", capabilities.Vendor);
    }

    [Fact]
    public async Task GetSessionCommands_ForAStartedSession_ReturnsCapabilities()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/commands", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var capabilities = await response.Content.ReadFromJsonAsync<WorkerCapabilities>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(capabilities);
        Assert.Equal("claude", capabilities.Vendor);
    }

    private sealed record SessionCommandsResponse(string Vendor, List<WorkerCapabilityItem> Items, List<string> Models, List<string> RecentlyUsed);

    [Fact]
    public async Task RecordCommandUsed_ThenGetSessionCommands_SurfacesItAsRecentlyUsed()
    {
        var (sessionId, _) = await StartASessionAsync();

        var recordResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/{sessionId}/commands/record", new RecordCommandUsedRequest("/compact"), TestContext.Current.CancellationToken);
        Assert.True(recordResponse.IsSuccessStatusCode);

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/commands", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var commands = await response.Content.ReadFromJsonAsync<SessionCommandsResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(commands);
        Assert.Contains("/compact", commands.RecentlyUsed);
    }

    [Fact]
    public async Task SetSessionMode_ToAuto_UpdatesTheBoundPermissionGrant()
    {
        var (sessionId, roomDirectoryPath) = await StartASessionAsync();

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest("auto"), TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(Path.Combine(roomDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
        var entry = bindings[InteractiveSessionMaterializer.DefaultWorkerName];
        Assert.NotNull(entry.PermissionGrant);
        Assert.True(entry.PermissionGrant!.ReadFiles);
        Assert.True(entry.PermissionGrant.WriteFiles);
        Assert.True(entry.PermissionGrant.RunShellCommands);
        Assert.True(entry.PermissionGrant.NetworkAccess);
    }

    [Fact]
    public async Task SetSessionMode_ToPlan_MakesTheGrantReadOnly()
    {
        var (sessionId, roomDirectoryPath) = await StartASessionAsync();

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest("plan"), TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(Path.Combine(roomDirectoryPath, "bindings.json"), TestContext.Current.CancellationToken);
        var entry = bindings[InteractiveSessionMaterializer.DefaultWorkerName];
        Assert.NotNull(entry.PermissionGrant);
        Assert.True(entry.PermissionGrant!.ReadFiles);
        Assert.False(entry.PermissionGrant.WriteFiles);
        Assert.False(entry.PermissionGrant.RunShellCommands);
    }

    [Fact]
    public async Task SetSessionMode_WithAnUnknownMode_ReturnsBadRequest()
    {
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest("not-a-real-mode"), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record SessionModeResponse(string Mode);

    [Fact]
    public async Task GetSessionMode_ForANewCodebaseSession_ReturnsDefault()
    {
        // A session bound to a working directory gets the conservative codebase default
        // (read + write, no shell/network), which maps to "default" mode.
        var (sessionId, _) = await StartASessionAsync(workingDirectory: Path.GetTempPath());

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var mode = await response.Content.ReadFromJsonAsync<SessionModeResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(mode);
        Assert.Equal("default", mode.Mode);
    }

    [Fact]
    public async Task GetSessionMode_ForADirectoryLessSession_ReportsCustomNotDefault()
    {
        // #321 / decision 0004: a directory-less session fails closed (no filesystem, shell, or
        // network). That grant is deliberately none of auto/plan/default, so it reports "custom" --
        // never "default", which was the old fail-open behaviour granting write access rooted where
        // nobody chose. (A dedicated "plain chat" mode label is a separate follow-up.)
        var (sessionId, _) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var mode = await response.Content.ReadFromJsonAsync<SessionModeResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(mode);
        Assert.Equal("custom", mode.Mode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("plan")]
    [InlineData("default")]
    public async Task SetSessionMode_ThenGetSessionMode_ReflectsTheChange(string mode)
    {
        var (sessionId, _) = await StartASessionAsync();

        var setResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", new SetSessionModeRequest(mode), TestContext.Current.CancellationToken);
        Assert.True(setResponse.IsSuccessStatusCode);

        var getResponse = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}/mode", TestContext.Current.CancellationToken);
        Assert.True(getResponse.IsSuccessStatusCode);
        var result = await getResponse.Content.ReadFromJsonAsync<SessionModeResponse>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(mode, result.Mode);
    }

    [Fact]
    public async Task GetSessionMode_ForANonexistentSession_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{_baseUrl}/api/sessions/does-not-exist/mode", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClearSession_ResetsTurnsAndForcesAFreshUnestablishedVendorSession()
    {
        var (sessionId, roomDirectoryPath) = await StartASessionAsync();
        var metadataPath = Path.Combine(roomDirectoryPath, ".aer", "room.json");

        // Simulate a session that already had real turns and an established native vendor session
        // -- clear must reset both without ever needing a real vendor call itself.
        var beforeClear = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(beforeClear);
        var withTurns = beforeClear with
        {
            Turns = [new SessionTurn(1, "claude", "hello", "hi", DateTimeOffset.UtcNow, false, false)],
            TurnCount = 1,
            VendorSessionEstablished = true,
        };
        await InteractiveSessionMaterializer.SaveMetadataAsync(withTurns, metadataPath, TestContext.Current.CancellationToken);
        var originalVendorSessionId = withTurns.CurrentVendorSessionId;

        var response = await _client.PostAsync($"{_baseUrl}/api/sessions/{sessionId}/clear", null, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var cleared = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(cleared);
        Assert.Empty(cleared.Turns);
        Assert.Equal(0, cleared.TurnCount);
        Assert.False(cleared.VendorSessionEstablished);
        // A fresh, distinct id -- not merely un-established -- so a leftover client-side reference
        // to the old id can never be mistaken for still-valid after a clear.
        Assert.NotEqual(originalVendorSessionId, cleared.CurrentVendorSessionId);
        Assert.NotNull(cleared.CurrentVendorSessionId);

        var onDisk = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath, TestContext.Current.CancellationToken);
        Assert.NotNull(onDisk);
        Assert.Empty(onDisk.Turns);
    }

    [Fact]
    public async Task ClearSession_ForANonexistentSession_ReturnsNotFound()
    {
        var response = await _client.PostAsync($"{_baseUrl}/api/sessions/does-not-exist/clear", null, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StartSession_WithATaskNameAlreadyInUse_ReturnsBadRequestAndDoesNotClobberTheFirstSession()
    {
        var roomName = "collision-test-" + Guid.NewGuid().ToString("N");
        var (firstSessionId, _) = await StartASessionAsync(roomName);

        var secondRequest = new StartSessionRequest(Adapter: "claude", RoomName: roomName);
        var secondResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", secondRequest, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, secondResponse.StatusCode);

        // The rejected second attempt must not have clobbered the first session -- it must still be
        // reachable by its original id with its original SessionId intact.
        var getResponse = await _client.GetAsync($"{_baseUrl}/api/sessions/{firstSessionId}", TestContext.Current.CancellationToken);
        Assert.True(getResponse.IsSuccessStatusCode);
        var metadata = await getResponse.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(firstSessionId, metadata.SessionId);
    }

    [Fact]
    public async Task GetFleet_ReturnsOkAndIncludesAStartedSessionByDefault()
    {
        var (_, roomDirectoryPath) = await StartASessionAsync();

        var response = await _client.GetAsync($"{_baseUrl}/api/rooms", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var items = await response.Content.ReadFromJsonAsync<List<RoomFleetItem>>(FleetReadOptions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Contains(items, i => i.RoomDirectoryPath == roomDirectoryPath);
    }

    [Fact]
    public async Task GetFleet_CarriesCreatedAndUpdatedTimestampsAndIsOrderedByRecency()
    {
        // Two sessions so the returned list has at least two entries to check ordering across.
        await StartASessionAsync();
        await StartASessionAsync();

        var items = await (await _client.GetAsync($"{_baseUrl}/api/rooms", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<RoomFleetItem>>(FleetReadOptions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.True(items!.Count >= 2, $"expected at least two fleet entries, got {items.Count}");

        // #322: every entry carries real UTC timestamps, and nothing was updated before it existed.
        foreach (var item in items)
        {
            Assert.NotEqual(default, item.Created);
            Assert.NotEqual(default, item.Updated);
            Assert.True(item.Updated >= item.Created,
                $"{item.FriendlyName}: Updated {item.Updated:o} precedes Created {item.Created:o}");
        }

        // #322: the list is ordered most-recently-updated first. Asserting the monotonic invariant
        // (each entry's Updated >= the next's) rather than "the last-started session is index 0"
        // avoids a wall-clock race when two sessions are created within one timestamp tick.
        for (var i = 0; i + 1 < items.Count; i++)
        {
            Assert.True(items[i].Updated >= items[i + 1].Updated,
                $"fleet not ordered by recency at index {i}: {items[i].Updated:o} < {items[i + 1].Updated:o}");
        }
    }

    [Fact]
    public async Task ArchiveUnarchiveAndDelete_RoundTripThroughTheFleetAndLifecycleEndpoints()
    {
        var roomName = "fleet-lifecycle-" + Guid.NewGuid().ToString("N");
        var (_, roomDirectoryPath) = await StartASessionAsync(roomName);

        // Archiving hides it from the default fleet list but keeps it reachable with includeArchived.
        var archiveResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/archive", new RoomDirectoryRequest(roomDirectoryPath), TestContext.Current.CancellationToken);
        Assert.True(archiveResponse.IsSuccessStatusCode);

        var defaultList = await (await _client.GetAsync($"{_baseUrl}/api/rooms", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<RoomFleetItem>>(FleetReadOptions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(defaultList!, i => i.RoomDirectoryPath == roomDirectoryPath);

        var withArchived = await (await _client.GetAsync($"{_baseUrl}/api/rooms?includeArchived=true", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<RoomFleetItem>>(FleetReadOptions, cancellationToken: TestContext.Current.CancellationToken);
        var archivedItem = Assert.Single(withArchived!, i => i.RoomDirectoryPath == roomDirectoryPath);
        Assert.True(archivedItem.IsArchived);

        // Archiving alone must not free the name for reuse -- workflow.json/session.json is still on disk.
        var collisionResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", new StartSessionRequest(Adapter: "claude", RoomName: roomName), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, collisionResponse.StatusCode);

        // Unarchiving reinstates it in the default list.
        var unarchiveResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/unarchive", new RoomDirectoryRequest(roomDirectoryPath), TestContext.Current.CancellationToken);
        Assert.True(unarchiveResponse.IsSuccessStatusCode);

        var reinstatedList = await (await _client.GetAsync($"{_baseUrl}/api/rooms", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<RoomFleetItem>>(FleetReadOptions, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(reinstatedList!, i => i.RoomDirectoryPath == roomDirectoryPath && !i.IsArchived);

        // Only a real delete frees the directory and the name (M24 Phase 5 regression, #278).
        var deleteResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/delete", new RoomDirectoryRequest(roomDirectoryPath), TestContext.Current.CancellationToken);
        Assert.True(deleteResponse.IsSuccessStatusCode);
        Assert.False(Directory.Exists(roomDirectoryPath));

        var recentsAfterDelete = await (await _client.GetAsync($"{_baseUrl}/api/rooms/recent", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(roomDirectoryPath, recentsAfterDelete!);

        var freshCollisionResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", new StartSessionRequest(Adapter: "claude", RoomName: roomName), TestContext.Current.CancellationToken);
        Assert.True(freshCollisionResponse.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("unarchive")]
    [InlineData("delete")]
    public async Task TaskLifecycleEndpoints_WithADirectoryPathOutsideTasksOrSessionsRoots_ReturnBadRequest(string action)
    {
        // Same containment guard #250 added for RunTemplate's RoomName, applied here (review
        // follow-up): these endpoints are remote-reachable (mobile's DaemonClient included) and
        // delete does a real recursive Directory.Delete -- an uncontained DirectoryPath is strictly
        // worse than #250's traversal, since it needs no traversal trick, just any absolute path
        // outside ~/.aer/tasks and ~/.aer/sessions.
        var outsidePath = Path.Combine(_tempRoomDirectory!, "outside-managed-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/{action}", new RoomDirectoryRequest(outsidePath), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(Directory.Exists(outsidePath));
    }

    [Fact]
    public async Task DeleteRoom_ForANonexistentDirectory_ReturnsNotFound()
    {
        // Must be under the managed ~/.aer/sessions root -- otherwise the containment guard now
        // rejects it as BadRequest before this handler's own NotFound check ever runs.
        var missingDirectory = Path.Combine(AerPaths.Rooms, "never-created-" + Guid.NewGuid().ToString("N"));

        var response = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/delete", new RoomDirectoryRequest(missingDirectory), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegisterProject_ThenListProjects_IncludesItAndCanBeCleanedUp()
    {
        var marker = "aer_daemon_test_project_" + Guid.NewGuid().ToString("N");
        var projectDirectory = Path.Combine(Path.GetTempPath(), marker);

        try
        {
            var postResponse = await _client.PostAsJsonAsync(
                $"{_baseUrl}/api/projects", new RegisterProjectRequest(projectDirectory, marker), TestContext.Current.CancellationToken);
            Assert.True(postResponse.IsSuccessStatusCode);

            var getResponse = await _client.GetAsync($"{_baseUrl}/api/projects", TestContext.Current.CancellationToken);
            Assert.True(getResponse.IsSuccessStatusCode);

            var projects = await getResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
            var matched = projects.EnumerateArray().Any(p =>
                (p.TryGetProperty("friendlyName", out var f) || p.TryGetProperty("FriendlyName", out f)) && f.GetString() == marker);
            Assert.True(matched);
        }
        finally
        {
            // KnownProjectsStore persists to projects.json under AerPaths.Root -- the AER_HOME
            // redirect keeps that in this run's temp root, but still scrub the synthetic entry so the
            // assertion above can't be corrupted by a sibling test and cleanup stays correct if the
            // redirect is ever absent.
            var projectsFile = Path.Combine(AerPaths.Root, "projects.json");
            if (File.Exists(projectsFile))
            {
                var json = await File.ReadAllTextAsync(projectsFile, TestContext.Current.CancellationToken);
                var remaining = JsonSerializer.Deserialize<List<JsonElement>>(json)!
                    .Where(p => !((p.TryGetProperty("friendlyName", out var f) || p.TryGetProperty("FriendlyName", out f)) && f.GetString() == marker))
                    .ToList();
                await File.WriteAllTextAsync(
                    projectsFile,
                    JsonSerializer.Serialize(remaining, new JsonSerializerOptions { WriteIndented = true }),
                    TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task ProgressWebSocket_AcceptsAConnectionWithoutRequiringAnOpenRoom()
    {
        // Deliberately kept separate from /api/ws (M24 Phase 1) -- a client subscribing to live
        // in-turn progress has no RoomClient dependency, unlike the projection socket.
        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws/progress?token={token}"), TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Open, socket.State);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TurnHostStatus_UnhostedRoom_Returns409()
    {
        // #994's absence contract, daemon side: a room the turn host is not hosting answers 409,
        // which RoomClient.TryGetTurnHostStatusAsync maps to null (absence, not error). Raw
        // client against THIS class's daemon deliberately — the first draft constructed a real
        // RoomClient, whose connection path reads the REAL ~/.aer registration and can spawn a
        // real daemon on probe failure (#998); its green never touched this daemon at all.
        // Red arm: with the hosted-room scope guard removed from the endpoint, this returns 200.
        var encodedPath = Uri.EscapeDataString(_tempRoomDirectory!);
        var response = await _client.GetAsync(
            $"{_baseUrl}/api/rooms/turn-host/status?roomDirectoryPath={encodedPath}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TurnHostStatus_HostedDormantRoom_RoundTripsThroughTheDtoContract()
    {
        // #994's presence contract, wire level (second-reader finding): the endpoint emits an
        // anonymous object under ASP.NET web defaults (camelCase); RoomTurnHostStatus pins each
        // field with a camelCase JsonPropertyName. Nothing else deserializes the real payload
        // into the real DTO, so a casing drift on either side would silently null every field.
        // Red arm: rename one side's property and the matching assertion below fails.
        var room = _tempRoomDirectory!;
        await File.WriteAllTextAsync(
            Path.Combine(room, "turn-throttles.json"),
            """{ "machineTurnMinimumGapSeconds": 45, "machineTurnsPerHour": 8, "consecutiveFailureLimit": 3 }""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(room, "room.jsonl"),
            """{"owner":"room","Event":{"eventType":"turnHostDormancyEntered","ConsecutiveFailures":3,"Timestamp":"2026-08-04T17:00:00+00:00"}}""" + "\n" +
            """{"owner":"room","Event":{"eventType":"escalationRaised","FromWorkerId":"turn-host","Trigger":"Confidence","Subject":{"kind":"hostCondition","Condition":"turn-host-dormancy","Detail":"3 consecutive uncommitted turns tripped the breaker"},"Timestamp":"2026-08-04T17:00:01+00:00"}}""" + "\n",
            TestContext.Current.CancellationToken);

        var watchResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/rooms/watch", new { RoomDirectoryPath = room }, TestContext.Current.CancellationToken);
        Assert.True(watchResponse.IsSuccessStatusCode);

        // The turn host adopts the watched room on its next tick (500ms loop); poll until the
        // status endpoint stops refusing the room as non-hosted.
        HttpResponseMessage response = null!;
        for (int i = 0; i < 100; i++)
        {
            response = await _client.GetAsync(
                $"{_baseUrl}/api/rooms/turn-host/status?roomDirectoryPath={Uri.EscapeDataString(room)}",
                TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<RoomTurnHostStatus>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.Equal(45, status.Throttles.MachineTurnMinimumGapSeconds);
        Assert.Equal(8, status.Throttles.MachineTurnsPerHour);
        Assert.Equal(3, status.Throttles.ConsecutiveFailureLimit);
        Assert.Equal("file", status.ThrottlesSource);
        Assert.Null(status.LoadError);
        Assert.Equal(0, status.TurnsInTrailingHourCount);
        Assert.Equal(8, status.MachineTurnsPerHourCap);
        Assert.True(status.IsDormant);
        Assert.Equal("3 consecutive uncommitted turns tripped the breaker", status.DormancyEscalationDetail);
    }

    /// <summary>
    /// #1229. Looking a session up by id scans EVERY room under the rooms root and parses each one's
    /// room.json, so before this fix one unreadable room anywhere made that lookup answer 500 for a
    /// session that was itself perfectly healthy — the caller was told its own session had failed.
    /// </summary>
    /// <remarks>
    /// The flake this came from is the transient form of the same bug: on Windows CI a room being
    /// written (or deleted) by a concurrently-running test outlasted LoadMetadataAsync's retry, and
    /// the poll for an unrelated session got a bodyless 500 on its third iteration. A permanently
    /// corrupt room.json is that same failure made deterministic — the retry cannot rescue it either
    /// way, and the scan must not care.
    /// </remarks>
    [Fact]
    public async Task LookingUpASessionSurvivesAnUnreadableRoomBesideIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionId = "healthy-" + Guid.NewGuid().ToString("N");

        var healthyRoom = Path.Combine(AerPaths.Rooms, "healthy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(healthyRoom, ".aer"));
        await InteractiveSessionMaterializer.SaveMetadataAsync(
            new SessionMetadata(
                SessionId: sessionId,
                RoomDirectoryPath: healthyRoom,
                CurrentAdapter: "claude",
                CurrentVendorSessionId: null,
                Model: null,
                WorkingDirectory: null,
                TurnCount: 0,
                SafetyCeiling: 200,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                Turns: []),
            Path.Combine(healthyRoom, ".aer", AerPaths.RoomMetadataFileName),
            cancellationToken);

        // Permanently unparseable, so LoadMetadataAsync's retry exhausts and throws rather than
        // eventually succeeding — the same exception the transient case delivers, minus the timing.
        var corruptRoom = Path.Combine(AerPaths.Rooms, "corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(corruptRoom, ".aer"));
        await File.WriteAllTextAsync(
            Path.Combine(corruptRoom, ".aer", AerPaths.RoomMetadataFileName), "{ this is not json",
            cancellationToken);

        try
        {
            var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}", cancellationToken);

            Assert.True(response.IsSuccessStatusCode,
                $"the healthy session answered {(int)response.StatusCode} — "
                + await response.Content.ReadAsStringAsync(cancellationToken));
            var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: cancellationToken);
            Assert.NotNull(metadata);
            Assert.Equal(sessionId, metadata.SessionId);

            // And the list endpoint, which scans the same way: the corrupt room is skipped, not
            // allowed to empty the whole list.
            var listResponse = await _client.GetAsync($"{_baseUrl}/api/sessions", cancellationToken);
            Assert.True(listResponse.IsSuccessStatusCode,
                $"the session list answered {(int)listResponse.StatusCode}");
            var list = await listResponse.Content.ReadFromJsonAsync<List<SessionMetadata>>(cancellationToken: cancellationToken);
            Assert.NotNull(list);
            Assert.Contains(list, s => s.SessionId == sessionId);
        }
        finally
        {
            // Both rooms sit under the shared rooms root, so they are cleaned up rather than left to
            // widen the very scan this test is about for every later test in the assembly.
            DirectoryCleanup.DeleteRecursively(healthyRoom);
            DirectoryCleanup.DeleteRecursively(corruptRoom);
        }
    }
}
