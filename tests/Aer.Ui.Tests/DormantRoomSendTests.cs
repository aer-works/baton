using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #1179: "Dormancy answers, it never resumes" (03-interaction-depth.md). A send into a dormant room
/// must be answered by the product -- no worker dispatch -- and durably recorded so a reopened
/// transcript still shows the exchange; a send into a room that has since been woken must dispatch
/// exactly as before. Shares <see cref="DaemonIntegrationTests"/>'s xUnit collection for the same
/// reason <see cref="SessionTurnBranchingTests"/> does: both spin up a real Kestrel daemon per test
/// against the same real per-user config file with no cross-instance locking, so parallel runs
/// intermittently failed with "connection refused" until forced sequential.
/// </summary>
[Collection("DaemonIntegrationTests")]
public class DormantRoomSendTests : IAsyncLifetime
{
    private DaemonTestInstance? _daemon;
    private string _baseUrl = "";
    private readonly HttpClient _client = new();
    private DispatchCountingAdapter _claudeAdapter = null!;

    private string WsBaseUrl => "ws" + _baseUrl["http".Length..];

    public async ValueTask InitializeAsync()
    {
        _claudeAdapter = new DispatchCountingAdapter(new SessionTurnStubAdapter());
        IReadOnlyDictionary<string, IWorkerAdapter> stubAdapters = new Dictionary<string, IWorkerAdapter>
        {
            ["claude"] = _claudeAdapter,
            ["agy"] = new SessionTurnStubAdapter(),
            [NoOpWorkerAdapter.AdapterName] = new NoOpWorkerAdapter(),
        };

        _daemon = await DaemonTestHost.StartAsync(stubAdapters);
        _baseUrl = _daemon.BaseUrl;

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

    private static readonly TimeSpan TurnCountTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TurnCountPollInterval = TimeSpan.FromMilliseconds(100);

    private async Task<SessionMetadata> StartStubSessionAsync()
    {
        var request = new StartSessionRequest(
            Adapter: "claude",
            RoomName: "dormant-send-test-" + Guid.NewGuid().ToString("N"),
            InitialMessage: "hello");

        var response = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/start", request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        return await PollUntilTurnCountAsync(metadata.SessionId, expectedTurnCount: 1);
    }

    private async Task<SessionMetadata> PollUntilTurnCountAsync(string sessionId, int expectedTurnCount)
    {
        SessionMetadata? metadata = null;
        var polls = 0;
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TurnCountTimeout)
        {
            polls++;
            var response = await _client.GetAsync($"{_baseUrl}/api/sessions/{sessionId}", TestContext.Current.CancellationToken);
            if (response.IsSuccessStatusCode)
            {
                metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(cancellationToken: TestContext.Current.CancellationToken);
                if (metadata != null && metadata.Turns.Count >= expectedTurnCount)
                {
                    return metadata;
                }
            }

            await Task.Delay(TurnCountPollInterval, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Session {sessionId} never reached {expectedTurnCount} turn(s) within {TurnCountTimeout.TotalSeconds:0}s " +
            $"({polls} polls); last seen: {metadata?.Turns.Count ?? -1}.");
        return null!;
    }

    [Fact]
    public async Task SendMessage_ToDormantRoom_AnswersWithoutDispatch_AndPersistsTheExchange()
    {
        var started = await StartStubSessionAsync();
        var resolveCountBeforeSend = Volatile.Read(ref _claudeAdapter.ResolveCount);

        var roomLogPath = Path.Combine(started.RoomDirectoryPath, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            await writer.AppendAsync(
                new RoomEvent.TurnHostDormancyEntered(3, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }

        var token = _client.DefaultRequestHeaders.Authorization!.Parameter!;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{WsBaseUrl}/api/ws?token={token}"), TestContext.Current.CancellationToken);
        // Connect-time snapshot push -- drained so the assertion below observes the broadcast the
        // send itself triggers, not this one.
        var connectBuffer = new byte[1024 * 64];
        await socket.ReceiveAsync(new ArraySegment<byte>(connectBuffer), TestContext.Current.CancellationToken);

        var sendRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "how's it going?");
        var sendResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", sendRequest, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode);

        // The broadcast the dormant-answer branch fires -- proves both surfaces learn of the answer
        // without waiting on their own poll (same requirement a completed turn's own broadcast meets).
        var buffer = new byte[1024 * 64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // wait-ok: bounded wait for the send's WS broadcast, same shape as DaemonIntegrationTests' own broadcast tests
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var broadcastResult = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
        var broadcastPayload = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, broadcastResult.Count)).RootElement;
        Assert.Equal(started.RoomDirectoryPath, broadcastPayload.GetProperty("DirectoryPath").GetString());
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);

        var afterSend = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        var answerTurn = afterSend.Turns[1];
        Assert.True(answerTurn.IsDormancyAnswer);
        Assert.Equal("how's it going?", answerTurn.HumanMessage);
        Assert.Null(answerTurn.AssistantResponse);
        Assert.Equal("System", answerTurn.Vendor);
        Assert.False(answerTurn.NativeSessionResumed);
        Assert.False(answerTurn.VendorHandoffSynthesized);

        // Red-proof the dormant arm: the fire-and-forget dispatch this endpoint normally starts never
        // ran. Since the dormant branch returns synchronously with no Task.Run at all, there is no
        // "hasn't happened yet" race to guard against -- but wait a beat anyway so this reads as a
        // decision, not a not-yet.
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken); // wait-ok: negative assertion control window, no async dispatch exists on this branch to race against
        Assert.Equal(resolveCountBeforeSend, Volatile.Read(ref _claudeAdapter.ResolveCount));
    }

    [Fact]
    public async Task SendMessage_ToRoomWokenAfterDormancy_DispatchesNormally_NoDormancyAnswerTurn()
    {
        var started = await StartStubSessionAsync();
        var resolveCountBeforeSend = Volatile.Read(ref _claudeAdapter.ResolveCount);

        var roomLogPath = Path.Combine(started.RoomDirectoryPath, "room.jsonl");
        await using (var writer = new RoomEventLogWriter(roomLogPath))
        {
            await writer.AppendAsync(
                new RoomEvent.TurnHostDormancyEntered(3, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new RoomEvent.TurnHostDormancyCleared("operator", DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }

        var sendRequest = new SendSessionMessageRequest(SessionId: started.SessionId, Message: "still there?");
        var sendResponse = await _client.PostAsJsonAsync($"{_baseUrl}/api/sessions/send", sendRequest, TestContext.Current.CancellationToken);
        Assert.True(sendResponse.IsSuccessStatusCode);

        var afterSend = await PollUntilTurnCountAsync(started.SessionId, expectedTurnCount: 2);
        var dispatchedTurn = afterSend.Turns[1];
        Assert.False(dispatchedTurn.IsDormancyAnswer);
        Assert.Equal("claude", dispatchedTurn.Vendor);
        Assert.NotNull(dispatchedTurn.AssistantResponse);

        Assert.True(Volatile.Read(ref _claudeAdapter.ResolveCount) > resolveCountBeforeSend);
    }

    /// <summary>
    /// Wraps a real dispatch-shape stub adapter and counts <see cref="IWorkerAdapter.Resolve"/>
    /// calls -- the observation seam these tests need: <see cref="SessionTurnStubAdapter"/> alone can
    /// only prove a dispatch's outcome, not whether a dispatch was ever attempted, and the whole
    /// point of the dormant arm is that it must not attempt one.
    /// </summary>
    private sealed class DispatchCountingAdapter(IWorkerAdapter inner) : IWorkerAdapter
    {
        public int ResolveCount;

        public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
        {
            Interlocked.Increment(ref ResolveCount);
            return inner.Resolve(invocation, contract);
        }
    }
}
