using Aer.Adapters;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #521: removing <c>MinimalOverhead</c> deleted a field that had been serialized into every
/// interactive room's marker (<c>room.json</c>, formerly <c>session.json</c>) ever written. This
/// pins the property that makes such a removal safe.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InteractiveSessionMaterializer.LoadMetadataAsync"/> configures no
/// <c>UnmappedMemberHandling</c>, so System.Text.Json's default (<c>Skip</c>) applies and an unknown
/// key is ignored. That is a property of the loader's options, not of the record — flipping it to
/// <c>Disallow</c>, or adding a converter that rejects unknown keys, would make every pre-existing
/// session file throw on load. Nothing else in the suite would notice, because every other fixture
/// is written by the current serializer and therefore never carries a key the current record lacks.
/// </para>
/// <para>
/// The fixture deliberately carries two unknown keys: <c>MinimalOverhead</c>, the field this issue
/// removed, and <c>AerNotARealField</c>, which never existed. Asserting on the removed field alone
/// would still pass under a loader that special-cased it; the second key is a control against that,
/// though a weak one -- both sit at the same nesting level under identical handling, so what it
/// mainly guards against is a future back-compat shim that special-cases the removed key while
/// tightening everything else. The assertion that actually discriminates is
/// <c>VendorSessionEstablished</c>, a field declared AFTER the unknown keys in the record: it fails
/// if the reader stops early instead of skipping them, which neither unknown key alone would catch.
/// </para>
/// </remarks>
public class SessionMetadataSchemaToleranceTests
{
    [Fact]
    public async Task A_session_file_carrying_removed_and_unknown_fields_still_loads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aer-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "room.json");

        try
        {
            // PascalCase, matching what SaveMetadataAsync actually writes (it sets no
            // PropertyNamingPolicy) -- a camelCase fixture here would still pass, because
            // LoadMetadataAsync sets PropertyNameCaseInsensitive, but it would be pinning that
            // setting instead of the unmapped-member tolerance this test claims to pin.
            await File.WriteAllTextAsync(path, """
                {
                  "SessionId": "sess-legacy-001",
                  "RoomDirectoryPath": "C:\\tmp\\legacy-room",
                  "CurrentAdapter": "claude",
                  "CurrentVendorSessionId": "vendor-abc",
                  "Model": "claude-haiku-4-5-20251001",
                  "WorkingDirectory": null,
                  "TurnCount": 3,
                  "SafetyCeiling": 200,
                  "MinimalOverhead": true,
                  "AerNotARealField": {"nested": ["anything", 1, null]},
                  "CreatedAt": "2026-07-01T10:00:00+00:00",
                  "UpdatedAt": "2026-07-01T10:05:00+00:00",
                  "Turns": [],
                  "VendorSessionEstablished": true
                }
                """, TestContext.Current.CancellationToken);

            var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(
                path, TestContext.Current.CancellationToken);

            Assert.NotNull(metadata);

            // The unknown keys must be skipped rather than throwing -- and the fields either side of
            // them must survive, so a "load" that silently produced a default-everything record
            // cannot pass.
            Assert.Equal("sess-legacy-001", metadata.SessionId);
            Assert.Equal("claude", metadata.CurrentAdapter);
            Assert.Equal("vendor-abc", metadata.CurrentVendorSessionId);
            Assert.Equal(3, metadata.TurnCount);
            Assert.Equal(200, metadata.SafetyCeiling);
            Assert.True(metadata.VendorSessionEstablished,
                "a field declared AFTER the unknown keys was dropped, so the reader stopped early "
                + "rather than skipping them");

            // #1305: this fixture predates Participants -- it must load as null, never as a
            // synthesized single-entry list. Why: SessionMetadata.Participants' own remarks.
            Assert.Null(metadata.Participants);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(dir);
        }
    }

    /// <summary>
    /// The same tolerance, for the OTHER loader — an operator-authored <c>bindings.json</c>.
    /// </summary>
    /// <remarks>
    /// `session.json` and `bindings.json` are read by different code with different
    /// <c>JsonSerializerOptions</c> (`LoadMetadataAsync` sets `PropertyNameCaseInsensitive`;
    /// <see cref="WorkerBindingConfigParser"/> passes none at all). Two readers with independently
    /// configurable strictness both had to tolerate the removed key, so both are pinned — testing
    /// only one would leave the other free to start rejecting operator config that AER itself wrote
    /// before #521.
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
