using Aer.Adapters.Tests.TestSupport;
using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Outcomes;
using Xunit;

namespace Aer.Adapters.Tests;

[Collection(LaunchConfigCollection.Name)]
public sealed class InteractiveSessionTests
{
    private readonly WorkerContract _contract = new(
        WorkerName: "chat-worker",
        RequiredInputs: [],
        ProducedOutputs: [new ProducedOutput("response.md")],
        OptionalMetadata: []);

    [Fact]
    public void ClaudeWorkerAdapter_ResolvesSessionFlags_FirstTurnAndResumed()
    {
        var adapter = new ClaudeWorkerAdapter();

        // Turn 1: new session ID -> --session-id <uuid>
        var invTurn1 = new WorkerInvocation(
            PromptTemplate: "Hello",
            SessionId: "session-123",
            ResumeSession: false,
            StreamJson: true);

        var target1 = adapter.Resolve(invTurn1, _contract);
        Assert.Contains("--session-id", target1.Args);
        Assert.Contains("session-123", target1.Args);
        // #521: this used to assert --bare IS emitted. Inverted deliberately, and kept rather than
        // deleted, because a silent regression here is invisible: --bare suppresses the mandatory
        // PreToolUse hook (0029/#543) even when the hook is passed explicitly via --settings, and a
        // worker with no gate looks exactly like a worker with a gate that allowed the call. This
        // only catches --bare re-added unconditionally -- it says nothing about --safe-mode, an
        // inherited CLAUDE_CODE_SIMPLE=1, or --bare reintroduced behind a new flag (see the fuller
        // comment in ClaudeWorkerAdapter.cs).
        Assert.DoesNotContain("--bare", target1.Args);
        Assert.Contains("stream-json", target1.Args);
        Assert.Contains("--include-partial-messages", target1.Args);
        // --print + --output-format=stream-json refuses to run at all without --verbose (confirmed
        // against the installed claude CLI) -- regression coverage for that failure mode.
        Assert.Contains("--verbose", target1.Args);
        Assert.DoesNotContain("--resume", target1.Args);

        // Turn 2: resume session -> --resume <uuid>
        var invTurn2 = new WorkerInvocation(
            PromptTemplate: "Next message",
            SessionId: "session-123",
            ResumeSession: true,
            StreamJson: true);

        var target2 = adapter.Resolve(invTurn2, _contract);
        Assert.Contains("--resume", target2.Args);
        Assert.Contains("session-123", target2.Args);
        Assert.DoesNotContain("--session-id", target2.Args);
    }

    [Fact]
    public void AgyWorkerAdapter_ResolvesSessionFlags_ConversationAndLogFile()
    {
        var adapter = new AgyWorkerAdapter();

        // Turn 1: initial -> --log-file
        var invTurn1 = new WorkerInvocation(
            PromptTemplate: "Hello",
            SessionId: null,
            ResumeSession: false,
            LogFilePath: "/tmp/agy-log.txt");

        var target1 = adapter.Resolve(invTurn1, _contract);
        Assert.Contains("--log-file", target1.Args);
        Assert.Contains("/tmp/agy-log.txt", target1.Args);
        Assert.DoesNotContain("--conversation", target1.Args);

        // Turn 2: resume -> --conversation <id>
        var invTurn2 = new WorkerInvocation(
            PromptTemplate: "Next message",
            SessionId: "conv-999",
            ResumeSession: true);

        var target2 = adapter.Resolve(invTurn2, _contract);
        Assert.Contains("--conversation", target2.Args);
        Assert.Contains("conv-999", target2.Args);
    }

    [Fact]
    public void Materialize_CreatesValidTwoStepDefinitionAndMetadata()
    {
        var (def, bindings, meta) = InteractiveSessionMaterializer.Materialize(
            sessionId: "sess-abc",
            roomDirectoryPath: "/tmp/aer/sessions/session-sess-abc",
            adapter: "claude",
            initialMessage: "Opening prompt");

        // #285: "chat" itself declares no PausePoint (a successful turn must flow straight through
        // to the anchor, uninterrupted); the downstream "turn-anchor" step declares the PausePoint,
        // targeting "chat" -- a legal, distinct-ancestor Supersede target, unlike the
        // old single self-referencing step.
        Assert.Equal("interactive-session-template", def.WorkflowTemplateId.Value);
        Assert.Equal(2, def.Steps.Count);
        var chatStep = Assert.Single(def.Steps, s => s.StepId.Value == "chat");
        Assert.Null(chatStep.PausePoint);
        var anchorStep = Assert.Single(def.Steps, s => s.StepId.Value == InteractiveSessionMaterializer.AnchorStepId);
        Assert.Contains(new StepId("chat"), anchorStep.DependsOn);
        Assert.NotNull(anchorStep.PausePoint);
        Assert.Contains(new StepId("chat"), anchorStep.PausePoint!.SupersedeTargets);
        // #334: a settled chat turn is "awaiting your next message", not an approval gate — the one
        // declaration site that opts out of the ReadyForReview default.
        Assert.Equal(PausePointKind.NeedsInput, anchorStep.PausePoint!.Kind);

        Assert.Equal(2, bindings.Count);
        Assert.True(bindings.ContainsKey("chat-worker"));
        Assert.Equal("claude", bindings["chat-worker"].Adapter);
        Assert.StartsWith("Opening prompt", bindings["chat-worker"].PromptTemplate, StringComparison.Ordinal);
        Assert.True(bindings.ContainsKey(InteractiveSessionMaterializer.AnchorWorkerName));
        Assert.Equal(NoOpWorkerAdapter.AdapterName, bindings[InteractiveSessionMaterializer.AnchorWorkerName].Adapter);

        // #650: the chat step declares no output. A chat turn's answer arrives either as response.md
        // or in the vendor's structured result, and the daemon reads whichever it gets — so requiring
        // the file classified a completed turn as Failed on every directory-less and plan-mode
        // session, whose grants cannot write one. The ask moved to the prompt, where it belongs:
        // a declared-and-absent output is correctly a failure.
        Assert.Empty(chatStep.Outputs);
        Assert.Empty(bindings["chat-worker"].Contract.ProducedOutputs);
        // Deliberately NOT asserted on the materialized PromptTemplate: the daemon rebuilds a turn's
        // prompt from the user's message and overwrites that field before every dispatch, so an
        // assertion there is green and vacuous. BuildTurnPrompt is the shared path both the
        // materializer and the daemon's per-turn rewrite go through, and it is what carries the ask.
        Assert.Contains(
            InteractiveSessionMaterializer.DefaultOutputFileName,
            InteractiveSessionMaterializer.BuildTurnPrompt("any message"),
            StringComparison.Ordinal);
        Assert.StartsWith("any message", InteractiveSessionMaterializer.BuildTurnPrompt("any message"), StringComparison.Ordinal);

        // Nothing upstream declares response.md any more, so the anchor cannot require it. DependsOn
        // (asserted above) is what orders the two steps; this only ever wired an artifact the no-op
        // anchor never reads.
        Assert.Empty(anchorStep.Inputs);
        Assert.Empty(bindings[InteractiveSessionMaterializer.AnchorWorkerName].Contract.RequiredInputs);

        Assert.Equal("sess-abc", meta.SessionId);
        Assert.Equal("claude", meta.CurrentAdapter);
        Assert.Equal(0, meta.TurnCount);
    }

    [Fact]
    public void A_chat_turn_that_writes_nothing_now_satisfies_its_contract()
    {
        // #650, stated at the layer that decides it. ContractValidator requires File.Exists for every
        // declared output, and OutcomeClassifier turns an unsatisfied contract into Failed even on a
        // natural exit-0 — so while the chat contract declared response.md, every turn of a
        // directory-less or plan-mode session (whose grants cannot write) classified Failed despite
        // the vendor succeeding. That verdict is what three separate daemon workarounds route around.
        var (_, bindings, _) = InteractiveSessionMaterializer.Materialize(
            sessionId: "sess-empty", roomDirectoryPath: "/tmp/aer/sessions/sess-empty", adapter: "claude");

        var emptyOutputDirectory = Path.Combine(Path.GetTempPath(), $"aer-650-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyOutputDirectory);
        try
        {
            Assert.True(
                ContractValidator.IsSatisfied(bindings["chat-worker"].Contract, emptyOutputDirectory),
                "a chat turn that produced no artifact must not be classified as a failure");

            // The polarity control on the same validator: it still fails a contract that DOES declare
            // an output, so the assertion above is about the chat contract rather than about a
            // validator that stopped checking anything.
            Assert.False(
                ContractValidator.IsSatisfied(
                    new WorkerContract("w", [], [new ProducedOutput("required.md")], []),
                    emptyOutputDirectory),
                "the validator stopped enforcing declared outputs, so the assertion above proves nothing");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(emptyOutputDirectory);
        }
    }

    [Fact]
    public void DefaultGrantForWorkingDirectory_NoDirectory_FailsClosed()
    {
        // #321 / decision 0004: no working directory means no project ceiling, so the grant floors to
        // the intersection -- no filesystem, shell, or network. Blank/whitespace count as none.
        foreach (var dir in new string?[] { null, "", "   " })
        {
            var grant = InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory(dir);
            Assert.False(grant.ReadFiles, $"ReadFiles should be false for [{dir ?? "null"}]");
            Assert.False(grant.WriteFiles, $"WriteFiles should be false for [{dir ?? "null"}]");
            Assert.False(grant.RunShellCommands);
            Assert.False(grant.NetworkAccess);
        }
    }

    [Fact]
    public void DefaultGrantForWorkingDirectory_WithDirectory_IsConservative()
    {
        var grant = InteractiveSessionMaterializer.DefaultGrantForWorkingDirectory("/home/user/project");
        Assert.True(grant.ReadFiles);
        Assert.True(grant.WriteFiles);
        Assert.False(grant.RunShellCommands);   // conservative: shell still off by default
        Assert.False(grant.NetworkAccess);      // conservative: network still off by default
    }

    [Fact]
    public void Materialize_WithoutWorkingDirectoryOrGrant_ChatWorkerFailsClosed()
    {
        // The wiring, not just the helper: a directory-less session (mobile "Start new chat", desktop
        // "plain chat if empty") must not materialize a chat worker with write access rooted at the
        // daemon/app cwd nobody chose (#321).
        var (_, bindings, _) = InteractiveSessionMaterializer.Materialize(
            sessionId: "sess-nodir",
            roomDirectoryPath: "/tmp/aer/sessions/session-sess-nodir",
            adapter: "claude");

        var grant = bindings["chat-worker"].PermissionGrant;
        Assert.NotNull(grant);
        Assert.False(grant!.ReadFiles);
        Assert.False(grant.WriteFiles);
        Assert.False(grant.RunShellCommands);
        Assert.False(grant.NetworkAccess);
    }

    [Fact]
    public void Materialize_WithWorkingDirectory_ChatWorkerGetsConservativeGrant()
    {
        var (_, bindings, _) = InteractiveSessionMaterializer.Materialize(
            sessionId: "sess-dir",
            roomDirectoryPath: "/tmp/aer/sessions/session-sess-dir",
            adapter: "claude",
            workingDirectory: "/home/user/project");

        var grant = bindings["chat-worker"].PermissionGrant;
        Assert.NotNull(grant);
        Assert.True(grant!.ReadFiles);
        Assert.True(grant.WriteFiles);
        Assert.False(grant.RunShellCommands);
        Assert.False(grant.NetworkAccess);
    }

    [Fact]
    public void SynthesizeContextSummary_FormatsHistoryCorrectly()
    {
        List<SessionTurn> turns =
        [
            new SessionTurn(1, "claude", "What is 2+2?", "4", DateTimeOffset.UtcNow, false, false),
            new SessionTurn(2, "claude", "What is 3+3?", "6", DateTimeOffset.UtcNow, true, false)
        ];

        var summary = InteractiveSessionMaterializer.SynthesizeContextSummary(turns, "What is 4+4?");
        Assert.Contains("User: What is 2+2?", summary);
        Assert.Contains("Assistant: 4", summary);
        Assert.Contains("User: What is 3+3?", summary);
        Assert.Contains("Now continue with the following user request:", summary);
        Assert.Contains("What is 4+4?", summary);
    }

    [Fact]
    public async Task ClaudeWorkerAdapter_DiscoverCapabilities_ReturnsModelAliasesAndCompactCommand()
    {
        var claudeAdapter = new ClaudeWorkerAdapter();
        var claudeCaps = await claudeAdapter.DiscoverCapabilitiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("claude", claudeCaps.Vendor);
        // Claude Code has no "list models" subcommand — `--model` only documents alias examples in
        // --help, so this is a deliberately hardcoded, CLI-independent list (unlike Gemini below).
        Assert.Contains("sonnet", claudeCaps.Models);
        Assert.Contains(claudeCaps.Items, item => item.Name == "/compact");
    }

    [Fact]
    public async Task AgyWorkerAdapter_DiscoverCapabilities_DoesNotFabricateDataWhenAgyUnavailable()
    {
        // agy is a real vendor CLI coincidentally present on some hosts (never assumed present in
        // CI — see CLAUDE.md's live-vendor-smoke-test rule). This only asserts the parts that don't
        // depend on the CLI being installed: it must never throw, and it must never report a
        // model/agent/plugin list it didn't actually observe from `agy models`/`agy agent`/
        // `agy plugin list`.
        var geminiAdapter = new AgyWorkerAdapter();
        var geminiCaps = await geminiAdapter.DiscoverCapabilitiesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agy", geminiCaps.Vendor);
        Assert.Contains(geminiCaps.Items, item => item.Name == "/compact");
        Assert.Contains(geminiCaps.Items, item => item.Name == "accept-edits" && item.Kind == "mode");
        Assert.DoesNotContain(geminiCaps.Models, m => string.IsNullOrWhiteSpace(m));
    }
}
