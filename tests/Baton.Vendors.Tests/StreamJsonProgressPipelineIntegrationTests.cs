using Baton.Vendors.Tests.TestSupport;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Store;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// M24 Phase 1's live in-turn streaming (issue #262) was wired end-to-end in
/// <c>Baton.Daemon.Program.ExecuteSessionTurnAsync</c> — <c>CoreDispatchTarget.OnStdoutLine</c> →
/// a bounded <c>Channel&lt;string&gt;</c> → <see cref="ClaudeWorkerAdapter.TryParseProgressEvent"/> →
/// a WebSocket broadcast — but every prior test exercised only one link at a time: the parser
/// against a bare string, and the resolver forwarding a callback delegate. Nothing had ever proven
/// that a real OS process's real stdout, captured through <see cref="CoreDispatcher"/>'s actual
/// native line-splitting, survives the channel hand-off and comes out the other side as the exact
/// ordered <see cref="WorkerProgressEvent"/>s a live <c>claude --output-format stream-json</c> turn
/// would produce. This spawns a real (non-claude, non-network) process that emits the exact four
/// lines captured from a live (unauthenticated) <c>claude</c> invocation — see
/// <c>ClaudeStreamJsonProgressParsingTests</c> for the same fixture used at the string level — and
/// dispatches it through the real <see cref="CoreDispatcher"/>, replicating
/// <c>ExecuteSessionTurnAsync</c>'s own channel/pump pattern so a regression in either the native
/// capture or the channel hand-off would fail here, not silently in production.
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class StreamJsonProgressPipelineIntegrationTests
{
    // Captured verbatim from a live `claude -p ... --output-format stream-json
    // --include-partial-messages --verbose` invocation against an unauthenticated CLI — real
    // envelope shapes, not hand-written approximations.
    private static readonly string[] CapturedLines =
    [
        """{"type":"system","subtype":"init","cwd":"C:\\Users\\pbree\\.claude\\jobs\\f4cd3a08\\tmp","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","tools":["Bash","Edit","PowerShell","Read"],"mcp_servers":[],"model":"claude-sonnet-5","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.216","output_style":"default","analytics_disabled":false,"product_feedback_disabled":false,"uuid":"f93ec384-884d-4bcd-b658-4d33f1393dea","fast_mode_state":"off"}""",
        """{"type":"system","subtype":"status","status":"requesting","uuid":"27fa2324-06c3-4d3a-b301-5a967274a7b9","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7"}""",
        """{"type":"assistant","message":{"id":"0b0f33a2-825a-4ca9-ac37-c0e49fca6f02","container":null,"model":"<synthetic>","role":"assistant","stop_details":null,"stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"Not logged in · Please run /login"}],"context_management":null},"parent_tool_use_id":null,"session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","uuid":"c8766ee4-a0c2-4104-90a6-2907ce7d9e03","timestamp":"2026-07-21T16:26:12.855Z","error":"authentication_failed"}""",
        """{"type":"result","subtype":"success","is_error":true,"api_error_status":null,"duration_ms":29,"duration_api_ms":0,"num_turns":1,"result":"Not logged in · Please run /login","stop_reason":"stop_sequence","session_id":"16ab91d3-511f-46ad-ade5-c946b7c9e2f7","total_cost_usd":0,"modelUsage":{},"permission_denials":[],"terminal_reason":"api_error","fast_mode_state":"off","uuid":"f9febda5-1240-4853-a32e-1dba6c60c012"}""",
    ];

    private static CoreDispatchTarget BuildEmitterTarget(string fixturePath) =>
        new("cmd", ["/c", "type", fixturePath]);

    [Fact]
    public async Task A_real_spawned_process_emitting_the_captured_fixture_survives_dispatch_channel_and_parser_intact()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "baton_stream_pipeline_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fixturePath = Path.Combine(tempDir, "capture.jsonl");
            await File.WriteAllLinesAsync(fixturePath, CapturedLines, TestContext.Current.CancellationToken);

            var logPath = Path.Combine(tempDir, "flow.jsonl");
            await using var writer = new FlowEventLogWriter(logPath);
            var dispatcher = new CoreDispatcher(writer);

            // Mirrors ExecuteSessionTurnAsync's own channel/pump exactly: OnStdoutLine only ever
            // enqueues (it runs on BatonTask's callback thread — EventRaised is invoked
            // synchronously on the thread running the spawn/wait loop), a separate task drains the
            // channel and does the real parse off that thread.
            var channel = System.Threading.Channels.Channel.CreateBounded<string>(
                new System.Threading.Channels.BoundedChannelOptions(500)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

            var rawLinesReceived = new List<string>();
            var parsedEvents = new List<WorkerProgressEvent>();
            var adapter = new ClaudeWorkerAdapter();

            var pumpTask = Task.Run(async () =>
            {
                await foreach (var line in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
                {
                    rawLinesReceived.Add(line);
                    if (adapter.TryParseProgressEvent(line, out var progressEvent) && progressEvent is not null)
                    {
                        parsedEvents.Add(progressEvent);
                    }
                }
            }, TestContext.Current.CancellationToken);

            var target = BuildEmitterTarget(fixturePath) with
            {
                OnStdoutLine = line => channel.Writer.TryWrite(line),
            };

            var request = new ExecutionRequest(
                new ExecutionId("exec-stream-pipeline-1"),
                new WorkflowId("wf-stream-pipeline"),
                new StepId("chat"),
                "chat-worker",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromSeconds(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            var result = await dispatcher.DispatchAsync(request, target, TestContext.Current.CancellationToken);

            channel.Writer.Complete();
            await pumpTask;

            Assert.Equal(CoreExitReason.Natural, result.Reason);
            Assert.Equal(0, result.ExitCode);

            // The real native capture + line-split must reproduce the fixture exactly -- proves
            // CoreDispatcher's StdoutChunk buffering handles multi-line output faithfully, not just
            // in the single-shot unit tests that hand a string directly to the parser.
            Assert.Equal(CapturedLines, rawLinesReceived);

            Assert.Equal(4, parsedEvents.Count);

            Assert.Equal("status", parsedEvents[0].Kind);
            Assert.Equal("Session started", parsedEvents[0].Text);

            Assert.Equal("status", parsedEvents[1].Kind);
            Assert.Equal("requesting", parsedEvents[1].Text);

            Assert.Equal("text", parsedEvents[2].Kind);
            Assert.Equal("Not logged in · Please run /login", parsedEvents[2].Text);

            // #1561: the `result` line (CapturedLines[3]) is an error turn (is_error: true), so it
            // now surfaces as a fourth event -- the status/error summary EchoStreamJsonLine renders
            // to show WHY the lane failed, rather than vanishing the way every `result` line did
            // before #1561.
            Assert.Equal("result", parsedEvents[3].Kind);
            Assert.Equal("error — Not logged in · Please run /login", parsedEvents[3].Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                DirectoryCleanup.DeleteRecursively(tempDir);
            }
        }
    }
}
