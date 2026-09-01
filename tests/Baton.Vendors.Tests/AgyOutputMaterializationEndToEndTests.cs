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
    public void TryMaterializeMissingOutputs_RealAgyResultLine_WritesTheWorkersOwnResponse()
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

            var written = OutputMaterializer.TryMaterializeMissingOutputs(validation, contract, directory, new AgyWorkerAdapter());

            Assert.Equal(["advice.md"], written);
            var contents = File.ReadAllText(Path.Combine(directory, "advice.md"));
            Assert.StartsWith(OutputMaterializer.MaterializedHeader, contents);
            Assert.Contains("Created note.txt containing HELLO-WORLD.", contents);

            var revalidated = ContractValidator.Validate(contract, directory);
            Assert.True(revalidated.IsSatisfied);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }
}
