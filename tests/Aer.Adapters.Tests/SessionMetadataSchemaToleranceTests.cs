using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

public class SessionMetadataSchemaToleranceTests
{
    /// <summary>
    /// The tolerance an operator-authored <c>bindings.json</c> gets from its parser: an unknown key
    /// (<c>MinimalOverhead</c>, removed by #521, plus a key that never existed) is skipped rather
    /// than rejected, so config AER itself wrote before #521 keeps parsing.
    /// </summary>
    /// <remarks>
    /// This used to pin the same tolerance on the <c>session.json</c>/<c>room.json</c> reader too
    /// (<c>InteractiveSessionMaterializer.LoadMetadataAsync</c>), since the two readers configure
    /// their strictness independently. That reader was deleted as orphaned by the daemon narrowing
    /// (#1421) — nothing in the tree persists or reads back an interactive room's <c>room.json</c>
    /// any more — leaving <see cref="WorkerBindingConfigParser"/> as the one still-live reader this
    /// tolerance matters for.
    /// </remarks>
    [Fact]
    public void A_bindings_file_carrying_the_removed_field_still_parses()
    {
        // Shaped like a bindings.json authored before #521 -- PascalCase keys, matching
        // tests/Aer.Cli.SmokeTests/Fixtures/*.json, because this parser passes no
        // PropertyNameCaseInsensitive and a camelCase fixture fails for the wrong reason.
        var json = """
            {
              "chat-worker": {
                "Adapter": "claude",
                "PromptTemplate": "Hello",
                "Timeout": "00:10:00",
                "MinimalOverhead": true,
                "AerNotARealField": 42,
                "Contract": {
                  "WorkerName": "chat-worker",
                  "RequiredInputs": [],
                  "ProducedOutputs": [{ "Name": "response.md" }],
                  "OptionalMetadata": []
                }
              }
            }
            """;

        var entries = WorkerBindingConfigParser.Parse(json);

        Assert.True(entries.ContainsKey("chat-worker"));
        var entry = entries["chat-worker"];
        Assert.Equal("claude", entry.Adapter);
        Assert.Equal("Hello", entry.PromptTemplate);
        Assert.Equal("chat-worker", entry.Contract.WorkerName);
    }
}
