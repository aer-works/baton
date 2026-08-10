namespace Aer.VendorProbe.Tests;

/// <summary>
/// The discriminating control the vendor probe never had (#1088). The suite recorded agy's structured
/// output and per-turn usage as absent across three versions because <c>Probes</c> hard-coded claude's
/// stream-json grammar and keyed detection on claude's <c>"type"</c> envelope — and nothing ever built
/// or tested this tool. These are <b>pure-function</b> tests: no <c>Cli.Invoke</c>, no vendor process,
/// safe in CI. The agy/claude fixtures are the first lines of real <c>--output-format stream-json</c>
/// runs captured on an authenticated host (2026-08-10, agy 1.1.11 / claude 2.1.222) and frozen here.
/// </summary>
public sealed class ProbeGrammarTests
{
    // First line of a real `agy -p "<prompt>" --output-format stream-json` run. Note: `"event"`, not `"type"`.
    private const string AgyFirstLine =
        """{"event":"init","conversation_id":"5ec0d582-de3a-4fce-b626-578a3fcd9815","init":{"cwd":"C:/tmp"}}""";

    // First line of a real `claude -p --output-format stream-json --verbose "<prompt>"` run.
    private const string ClaudeFirstLine =
        """{"type":"system","subtype":"hook_started","hook_event":"SessionStart","session_id":"361b75"}""";

    // What agy actually returns when handed claude's grammar: the prompt string became "--output-format",
    // so agy answered in prose and stream-json never engaged — the exact false-negative this fix ends.
    private const string AgyProseWhenMisinvoked =
        "Could you specify how you would like to use `--output-format`? For assistant responses...";

    [Fact]
    public void Agy_stream_json_args_use_flag_value_grammar_and_omit_claude_verbose()
    {
        var args = Probes.StreamJsonArgs("agy", "Reply with exactly: ok");

        // agy's -p is flag-VALUE (#491): the prompt is the value of -p, immediately after it.
        var p = Array.IndexOf(args, "-p");
        Assert.True(p >= 0 && p + 1 < args.Length, "expected a -p flag with a following value");
        Assert.Equal("Reply with exactly: ok", args[p + 1]);

        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        // agy rejects claude's --verbose with exit 2 ("flags provided but not defined: -verbose").
        Assert.DoesNotContain("--verbose", args);
    }

    [Fact]
    public void Claude_stream_json_args_keep_boolean_p_positional_prompt_and_verbose()
    {
        var args = Probes.StreamJsonArgs("claude", "Reply with exactly: ok");

        // claude's -p is a boolean; the prompt is positional (last), and --verbose is required for stream-json.
        var p = Array.IndexOf(args, "-p");
        Assert.True(p >= 0);
        Assert.Equal("--output-format", args[p + 1]); // NOT the prompt — claude grammar differs from agy
        Assert.Contains("--verbose", args);
        Assert.Equal("Reply with exactly: ok", args[^1]);
    }

    [Fact]
    public void LooksLikeStreamJson_recognises_agy_event_envelope()
    {
        // The regression that mattered: agy streams `{"event":...}`, and the old `Contains("\"type\"")`
        // check read that as "not structured". This must be true.
        Assert.True(Probes.LooksLikeStreamJson(AgyFirstLine));
    }

    [Fact]
    public void LooksLikeStreamJson_recognises_claude_type_envelope()
    {
        Assert.True(Probes.LooksLikeStreamJson(ClaudeFirstLine));
    }

    [Fact]
    public void LooksLikeStreamJson_rejects_the_misinvoked_prose_and_empty()
    {
        Assert.False(Probes.LooksLikeStreamJson(AgyProseWhenMisinvoked));
        Assert.False(Probes.LooksLikeStreamJson(""));
        // A line merely containing the word "type" in prose must not masquerade as a stream (structural, not substring).
        Assert.False(Probes.LooksLikeStreamJson("The response type is JSON, apparently."));
    }
}
