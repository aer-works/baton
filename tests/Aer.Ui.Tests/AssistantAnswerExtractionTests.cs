using Aer.Daemon;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit coverage for <see cref="DaemonHost.TryExtractAssistantAnswer"/> (#534): recovering the
/// answer a SUCCESSFUL turn put in its structured result, for when no output file was written.
/// </summary>
/// <remarks>
/// The payload in <see cref="RecoversTheAnswerFromARealSuccessfulResultLine"/> is a real
/// <c>claude --output-format stream-json</c> result line captured from the live failure that opened
/// #534, trimmed only of fields irrelevant here. It is the case the product actually hits: a
/// directory-less session gets an all-deny grant (fail-closed, #321), so the worker cannot write
/// <c>response.md</c>, says so, and exits <c>is_error: false</c> with the answer in <c>result</c>.
/// <para>
/// This helper and <see cref="DaemonHost.TryExtractVendorErrorMessage"/> read the same line and are
/// separated by one condition. Getting that condition backwards would render a vendor error as
/// though the assistant had said it, so the polarity is asserted from both directions here and
/// again end-to-end in <see cref="SessionAnswerWithoutOutputFileTests"/>.
/// </para>
/// </remarks>
public class AssistantAnswerExtractionTests
{
    [Fact]
    public void RecoversTheAnswerFromARealSuccessfulResultLine()
    {
        var rawStdout = """
            {"type":"system","subtype":"init","session_id":"b2b94128-9843-4f0f-9022-513885784090"}
            {"type":"result","subtype":"success","is_error":false,"stop_reason":"end_turn","result":"Acknowledged — this is turn 1 of a smoke test."}
            """;

        Assert.Equal("Acknowledged — this is turn 1 of a smoke test.",
            DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    /// <summary>
    /// The polarity guard. A failed turn's text is an ERROR and belongs in <c>ErrorMessage</c>; if
    /// it leaked out of here it would be rendered as the assistant's reply.
    /// </summary>
    [Fact]
    public void RefusesToTreatAFailedTurnsTextAsAnAnswer()
    {
        var rawStdout = """
            {"type":"result","subtype":"error_during_execution","is_error":true,"result":"No conversation found with session ID: 4b195030"}
            """;

        Assert.Null(DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    /// <summary>
    /// The complement of the guard above, asserted so the pair cannot silently drift into agreeing:
    /// the same input the answer-extractor refuses must still yield an error message.
    /// </summary>
    [Fact]
    public void TheSameFailedLineStillYieldsAnErrorMessage()
    {
        var rawStdout = """
            {"type":"result","subtype":"error_during_execution","is_error":true,"result":"No conversation found with session ID: 4b195030"}
            """;

        Assert.Contains("No conversation found", DaemonHost.TryExtractVendorErrorMessage(rawStdout));
    }

    [Fact]
    public void ScansBackwardAndTakesTheFinalResultLine()
    {
        var rawStdout = """
            {"type":"result","subtype":"success","is_error":false,"result":"stale earlier result"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"noise"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"the real answer"}
            """;

        Assert.Equal("the real answer", DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoResultLineAtAll()
    {
        var rawStdout = """
            {"type":"system","subtype":"init","session_id":"abc"}
            {"type":"assistant","message":{"content":[]}}
            """;

        Assert.Null(DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    /// <summary>
    /// An empty or whitespace <c>result</c> is not an answer. Returning it would trade an empty turn
    /// for an empty turn while reporting success, which is worse than not recovering at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsAnEmptyResultAsNoAnswer(string result)
    {
        var rawStdout = $"{{\"type\":\"result\",\"is_error\":false,\"result\":\"{result}\"}}";

        Assert.Null(DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    [Fact]
    public void SurvivesNonJsonNoiseOnStdout()
    {
        var rawStdout = """
            Warning: something the CLI printed that is not JSON at all
            {"type":"result","subtype":"success","is_error":false,"result":"answered anyway"}
            """;

        Assert.Equal("answered anyway", DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    [Fact]
    public void ReturnsNullForEmptyStdout()
    {
        Assert.Null(DaemonHost.TryExtractAssistantAnswer(""));
    }

    // ---- agy (#1088): the same recovery over agy's own stream-json envelope ----

    [Fact]
    public void RecoversTheAnswerFromARealAgyStreamJsonResultEvent()
    {
        // Real agy 1.1.11 `--output-format stream-json` shape (why the terminal result carries the answer:
        // DaemonHost.TryExtractAssistantAnswer's agy branch).
        var rawStdout = """
            {"event":"init","conversation_id":"5ec0d582"}
            {"event":"step_update","step_update":{"state":"DONE","step_type":"agent_response"}}
            {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","num_turns":1,"usage":{"total_tokens":15580}}}
            """;

        Assert.Equal("Created note.txt containing HELLO-WORLD.",
            DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    /// <summary>
    /// Polarity guard for agy: a non-SUCCESS status is a failure, not an answer. agy's exact
    /// failure-result shape is unmeasured (its quota error surfaces on stderr, not a stdout result), so
    /// the guard keys on <c>status != SUCCESS</c> rather than a guessed error field.
    /// </summary>
    [Fact]
    public void RefusesToTreatANonSuccessAgyResultAsAnAnswer()
    {
        var rawStdout = """
            {"event":"result","result":{"status":"ERROR","response":"whatever text"}}
            """;

        Assert.Null(DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsAnEmptyAgyResponseAsNoAnswer(string response)
    {
        var rawStdout = $"{{\"event\":\"result\",\"result\":{{\"status\":\"SUCCESS\",\"response\":\"{response}\"}}}}";

        Assert.Null(DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }

    [Fact]
    public void DoesNotMistakeABareStringAgyResultForAnAnswer()
    {
        // An `event:result` carrying a bare string matches neither envelope (see the `is JsonObject`
        // guard in DaemonHost.TryExtractAssistantAnswer), so nothing is recovered.
        var rawStdout = """{"event":"result","result":"a bare string, not agy's object"}""";

        Assert.Null(DaemonHost.TryExtractAssistantAnswer(rawStdout));
    }
}
