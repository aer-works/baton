using System.Net.Http.Json;
using Aer.Adapters;
using Aer.Daemon;
using Aer.Ui.Tests.TestSupport;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// #534: a chat turn the vendor completed successfully must not be recorded as an empty turn just
/// because the worker wrote no output file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the real case, not an edge case.</b>
/// <see cref="InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory"/> returns an all-deny
/// grant for a session with no working directory — deliberately fail-closed (#321). When measured,
/// that became <c>--disallowedTools Edit,Write,NotebookEdit,Bash</c>, so the model genuinely could
/// not write <c>response.md</c>. It said so, exited <c>is_error: false</c>, and put the answer in
/// <c>result</c>. Measured identically on <c>claude-opus-5</c> and <c>claude-haiku-4-5</c>, so it was
/// not a model declining to use a tool it had.
/// </para>
/// <para>
/// <b>Since #649 the write tools are no longer on that flag</b> — they ride the <c>PreToolUse</c>
/// hook, which allows a write into <c>AER_OUTPUT_DIR</c>, and that is where <c>response.md</c> is
/// addressed. So the answer-without-a-file case is no longer the *only* outcome of a directory-less
/// session; it remains a reachable one (the model may simply answer, the hook may deny, the vendor
/// may refuse), and the reading path must keep handling it. That is what this covers.
/// </para>
/// <para>
/// The worker's contract nonetheless declares <c>ProducedOutputs: [response.md]</c>. Both halves are
/// defensible; together they cannot hold, and the answer was being discarded.
/// </para>
/// <para>
/// <b>Deterministic and CI-safe.</b> No vendor CLI: <see cref="SessionTurnStubAdapter"/>'s
/// <see cref="SessionTurnStubAdapter.NoOutputFileSentinel"/> reproduces the exact shape. Every
/// pre-existing session stub wrote the output file, which is precisely why no test caught this.
/// </para>
/// </remarks>
[Collection("DaemonIntegrationTests")]
public class SessionAnswerWithoutOutputFileTests : IAsyncLifetime
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

        // Read the daemon token from the redirected AER_HOME root (tests/Shared/AerHomeRedirect.cs),
        // never the real per-user ~/.aer. Without this every request is Unauthorized, which is what
        // made all three tests here fail identically on the first run -- including the control, and
        // a failing control means no result in this class means anything.
        var tokenFile = Path.Combine(AerPaths.Root, "daemon.token");
        if (File.Exists(tokenFile))
        {
            var token = (await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken)).Trim();
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

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
            catch (HttpRequestException)
            {
                // Kestrel is still binding its port; retry.
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_daemon is not null)
        {
            await _daemon.DisposeAsync();
        }
    }

    /// <summary>
    /// The control. A turn whose worker DOES write the output file must keep working — this is what
    /// makes the failing case below a finding about the missing file rather than about the harness.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_worker_writes_the_output_file_records_its_answer()
    {
        var turn = await SendOneTurnAsync("ordinary turn, stub writes the file");

        Assert.False(string.IsNullOrWhiteSpace(turn.AssistantResponse),
            "the control turn lost its answer, so nothing else in this class means anything");
        Assert.Null(turn.ErrorMessage);
    }

    /// <summary>
    /// The defect. Vendor succeeded, answer is on stdout, no output file — the answer must survive.
    /// </summary>
    [Fact]
    public async Task A_successful_turn_that_writes_no_output_file_still_records_its_answer()
    {
        var turn = await SendOneTurnAsync(
            $"please answer {SessionTurnStubAdapter.NoOutputFileSentinel}");

        Assert.False(string.IsNullOrWhiteSpace(turn.AssistantResponse),
            "the vendor succeeded and its answer was on stdout, but AER recorded an empty turn (#534)");
        Assert.Contains(SessionTurnStubAdapter.StdoutOnlyAnswer, turn.AssistantResponse!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The polarity guard, and the one that keeps the fix honest: a turn that genuinely FAILED must
    /// not have the vendor's error text laundered into <c>AssistantResponse</c> as though the model
    /// had said it. Recovering an answer from stdout and mistaking an error for an answer are one
    /// character apart in the implementation.
    /// </summary>
    [Fact]
    public async Task A_failed_turn_does_not_present_its_error_as_the_assistant_answer()
    {
        var turn = await SendOneTurnAsync($"this one fails {SessionTurnStubAdapter.FailureSentinel}");

        Assert.True(string.IsNullOrWhiteSpace(turn.AssistantResponse),
            "a failed turn must not render an error as if the assistant had answered it");
        Assert.False(string.IsNullOrWhiteSpace(turn.ErrorMessage));
    }

    /// <summary>
    /// #650: the chat contract no longer requires response.md, so the prompt is the only thing that
    /// asks for it — and on a non-streaming vendor the file is the only channel an answer can arrive
    /// on. The ask must therefore survive the daemon's per-turn prompt rewrite, which overwrites the
    /// materialized template on every turn.
    /// </summary>
    /// <remarks>
    /// Asserted against the bindings.json the daemon writes immediately before dispatching, which is
    /// the prompt the vendor actually receives. The materialized template is not evidence — it is
    /// discarded. The stub adapter writes response.md whether or not it was asked, so no assertion
    /// about the recorded answer can discriminate this; the dispatched prompt is the only witness.
    /// </remarks>
    [Fact]
    public async Task The_prompt_the_vendor_is_dispatched_with_still_asks_for_the_response_file()
    {
        var turn = await SendOneTurnAsync("does the ask survive the turn rewrite");
        Assert.NotNull(turn);

        var roomsRoot = Path.Combine(AerPaths.Root, "rooms");
        var bindingsFiles = Directory.Exists(roomsRoot)
            ? Directory.GetFiles(roomsRoot, "bindings.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList()
            : [];
        Assert.NotEmpty(bindingsFiles);

        var bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFiles[0], TestContext.Current.CancellationToken);
        var chat = bindings[InteractiveSessionMaterializer.DefaultWorkerName];

        Assert.Contains(
            InteractiveSessionMaterializer.DefaultOutputFileName, chat.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains("does the ask survive the turn rewrite", chat.PromptTemplate, StringComparison.Ordinal);

        // The other half of #650, on the same entry: the contract must no longer require the file.
        // Both together are the fix — requiring it fails a turn that cannot write, and not asking for
        // it removes the only channel a non-streaming vendor has.
        Assert.Empty(chat.Contract.ProducedOutputs);
    }

    private async Task<SessionTurn> SendOneTurnAsync(string message)
    {
        var start = new StartSessionRequest(
            Adapter: "claude",
            RoomName: "no-output-file-" + Guid.NewGuid().ToString("N"),
            InitialMessage: message,
            SafetyCeiling: 200);

        var startResponse = await _client.PostAsJsonAsync(
            $"{_baseUrl}/api/sessions/start", start, TestContext.Current.CancellationToken);
        Assert.True(startResponse.IsSuccessStatusCode,
            $"session start failed: {startResponse.StatusCode}");
        var started = await startResponse.Content.ReadFromJsonAsync<SessionMetadata>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(started);

        for (var i = 0; i < 600; i++)
        {
            var response = await _client.GetAsync(
                $"{_baseUrl}/api/sessions/{started.SessionId}", TestContext.Current.CancellationToken);
            // A bare IsSuccessStatusCode assert here failed on Windows CI with no way to tell what
            // the daemon actually returned (poll iteration, status, body) — make the flake name
            // itself if it recurs.
            Assert.True(response.IsSuccessStatusCode,
                $"poll {i} for session {started.SessionId}: {(int)response.StatusCode} {response.StatusCode} — "
                + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var metadata = await response.Content.ReadFromJsonAsync<SessionMetadata>(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(metadata);
            if (metadata.Turns.Count >= 1)
            {
                return metadata.Turns[0];
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the session never recorded a turn at all");
        return null!;
    }
}
