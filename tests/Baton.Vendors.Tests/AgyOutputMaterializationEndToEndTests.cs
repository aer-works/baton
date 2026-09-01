using Baton.Domain;
using Baton.Outcomes;
using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// End-to-end coverage for issue #1594's safety net, wiring the real <see cref="AgyWorkerAdapter"/> —
/// not a fake parser — through <see cref="OutputMaterializer"/> against a verbatim captured agy
/// terminal line written to a real <c>.stdout.log</c>. <see cref="Baton.Tests.Outcomes.OutcomeClassifierTests"/>
/// pins the classification-level contract with a fake parser; this pins that the real adapter's parse
/// of the real envelope shape is what that contract actually rests on.
/// </summary>
public sealed class AgyOutputMaterializationEndToEndTests
{
    [Fact]
    public void TryCaptureFinalResponse_RealAgyResultLine_CapturesTheWorkersOwnResponse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agy-materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Verbatim capture, same fixture AgyFinalUsageParsingTests/AgyFinalResponseParsingTests
            // pin -- a real agy 1.1.11 terminal line, not a synthesized one.
            const string line = """
                {"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"Created note.txt containing HELLO-WORLD.","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":14407,"output_tokens":1173,"thinking_tokens":992,"cache_read_tokens":40765,"total_tokens":15580}}}
                """;
            File.WriteAllText(Path.Combine(directory, ".stdout.log"), line + "\n");

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);
            var validation = ContractValidator.Validate(contract, directory);
            Assert.False(validation.IsSatisfied);

            var captured = OutputMaterializer.TryCaptureFinalResponse(validation, contract, directory, new AgyWorkerAdapter());

            Assert.NotNull(captured);
            Assert.Equal(OutputMaterializer.CapturedResponseFileName, captured.FileName);
            Assert.Equal(["advice.md"], captured.UnsatisfiedOutputNames);

            var contents = File.ReadAllText(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName));
            Assert.StartsWith(OutputMaterializer.CapturedResponseHeader, contents);
            Assert.Contains("Created note.txt containing HELLO-WORLD.", contents);

            // The declared output directory is untouched -- the declared output stays unsatisfied.
            Assert.False(File.Exists(Path.Combine(directory, "advice.md")));
            var revalidated = ContractValidator.Validate(contract, directory);
            Assert.False(revalidated.IsSatisfied);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void TryCaptureFinalResponse_MultiLineStdoutLog_SelectsTheLastNonBlankLineAsTheResultLine()
    {
        // Review F3: a real agy stream log carries more than one line -- init/tool-use lines before
        // the terminal result, and (this fixture) blank/whitespace-only lines after it, which a naive
        // "first line" or "any non-blank line" scan would misread. Scanning backwards past the blanks
        // must land on the actual result line and capture ITS response, not some earlier line's.
        var directory = Path.Combine(Path.GetTempPath(), $"agy-materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var lines = new[]
            {
                """{"event":"init","session_id":"abc123"}""",
                """{"event":"tool_use","name":"Write","input":{"file_path":"note.txt"}}""",
                """{"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"the real terminal answer","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":1,"output_tokens":1,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":2}}}""",
                "",
                "  ",
            };
            File.WriteAllText(Path.Combine(directory, ".stdout.log"), string.Join("\n", lines) + "\n");

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);
            var validation = ContractValidator.Validate(contract, directory);

            var captured = OutputMaterializer.TryCaptureFinalResponse(validation, contract, directory, new AgyWorkerAdapter());

            Assert.NotNull(captured);
            var contents = File.ReadAllText(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName));
            Assert.Contains("the real terminal answer", contents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public void TryCaptureFinalResponse_TrailingNonJsonLineAfterTheResultLine_RefusesExtraction()
    {
        // Polarity twin of the discrimination test above: the result line is present, but it is not
        // the LAST non-blank line -- a trailing, non-JSON line (e.g. a stray log write after the
        // terminal event) sits after it. The scan must not skip past a line the parser declines and
        // fall back to an earlier one; it must refuse extraction entirely, same as any other
        // undecodable last line. Mutating TryReadFinalResponse's scan direction (or making it continue
        // past a declining line instead of returning) must turn this red.
        var directory = Path.Combine(Path.GetTempPath(), $"agy-materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var lines = new[]
            {
                """{"event":"result","result":{"conversation_id":"5ec0d582","status":"SUCCESS","response":"the real terminal answer","duration_seconds":3.6,"num_turns":1,"usage":{"input_tokens":1,"output_tokens":1,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":2}}}""",
                "not json at all, a stray trailing write",
            };
            File.WriteAllText(Path.Combine(directory, ".stdout.log"), string.Join("\n", lines) + "\n");

            var contract = new WorkerContract("worker", [], [new ProducedOutput("advice.md")], []);
            var validation = ContractValidator.Validate(contract, directory);

            var captured = OutputMaterializer.TryCaptureFinalResponse(validation, contract, directory, new AgyWorkerAdapter());

            Assert.Null(captured);
            Assert.False(File.Exists(Path.Combine(directory, OutputMaterializer.CapturedResponseFileName)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
