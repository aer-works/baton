using System.Text;
using System.Text.Json;
using Aer.Flow.Domain;

namespace Aer.Flow.Tests.Domain;

/// <summary>
/// The parse floor of the <c>ReviewVerdict</c> schema (#732, decision 0043): what must be
/// present, what casing is forgiven, and what extra content is tolerated. One definition of "valid
/// verdict" exists (<see cref="ReviewVerdictSchema.TryParse"/>); these pin its edges.
/// </summary>
public class ReviewVerdictSchemaTests
{
    [Fact]
    public void A_full_verdict_parses_with_lowercase_property_names_and_enum_values()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            {"reviewedRef": "740-branch @ 5f8813a",
             "summary": "one defect, two cosmetics",
             "findings": [
                {"severity": "high", "claim": "paused steps print no paths", "status": "confirmed",
                 "anchor": {"file": "src/Aer.Cli/FlowStateReporter.cs", "line": 76},
                 "detail": "the gate keys on Succeeded while a pause masks the status"},
                {"severity": "low", "claim": "redundant GetFullPath", "status": "unverified"}
             ]}
            """);

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(error);
        Assert.NotNull(verdict);
        Assert.Equal("740-branch @ 5f8813a", verdict.ReviewedRef);
        Assert.Equal(2, verdict.Findings.Count);
        Assert.Equal(ReviewFindingSeverity.High, verdict.Findings[0].Severity);
        Assert.Equal(ReviewFindingStatus.Confirmed, verdict.Findings[0].Status);
        Assert.Equal(76, verdict.Findings[0].Anchor!.Line);
        Assert.Null(verdict.Findings[1].Anchor);
    }

    [Fact]
    public void An_empty_findings_array_is_a_valid_verdict_meaning_nothing_was_found()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"reviewedRef": "main", "findings": []}""");

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out var verdict, out _));
        Assert.Empty(verdict!.Findings);
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated_at_every_level()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            {"reviewedRef": "main", "findings": [
                {"severity": "medium", "claim": "x", "status": "refuted", "confidence": 0.9}
             ], "model": "sonnet", "tokens": 12345}
            """);

        Assert.True(ReviewVerdictSchema.TryParse(bytes, out _, out var error));
        Assert.Null(error);
    }

    /// <summary>
    /// Pins the deserializer-leniency fact decision 0043's Rests-on table cites (the why lives on
    /// the null check inside <see cref="ReviewVerdictSchema.TryParse"/>): presence is enforced by
    /// the hand-written floor, not by STJ. If this arm ever starts failing on a JsonException
    /// instead of the floor message, STJ tightened and the hand checks can be revisited.
    /// </summary>
    [Fact]
    public void A_document_without_findings_is_refused_by_the_shape_floor_not_by_the_deserializer()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"reviewedRef": "main"}""");

        Assert.False(ReviewVerdictSchema.TryParse(bytes, out var verdict, out var error));
        Assert.Null(verdict);
        Assert.Contains("'findings' must be present", error);
    }

    [Theory]
    [InlineData("""{"findings": []}""", "reviewedRef")]
    [InlineData("""{"reviewedRef": "  ", "findings": []}""", "reviewedRef")]
    [InlineData("""{"reviewedRef": "main", "findings": [null]}""", "findings[0]")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": " ", "status": "confirmed"}]}""", "claim")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x", "status": "confirmed", "anchor": {"file": "f", "line": 0}}]}""", "line")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "high", "claim": "x", "status": "confirmed", "anchor": {"line": 3}}]}""", "anchor.file")]
    public void Documents_below_the_semantic_floor_are_refused_with_a_reason_naming_the_field(
        string json, string expectedInError)
    {
        Assert.False(ReviewVerdictSchema.TryParse(Encoding.UTF8.GetBytes(json), out _, out var error));
        Assert.Contains(expectedInError, error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"reviewedRef": "main", "findings": [{"severity": "catastrophic", "claim": "x", "status": "confirmed"}]}""")]
    [InlineData("null")]
    public void Malformed_documents_are_refused_without_throwing(string content)
    {
        Assert.False(ReviewVerdictSchema.TryParse(Encoding.UTF8.GetBytes(content), out var verdict, out var error));
        Assert.Null(verdict);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// The declaration side's round trip: <see cref="OutputSchema"/> serializes as a string, and a
    /// default (<see cref="OutputSchema.None"/>) is omitted entirely — a contract written before
    /// the field existed and one written after it, with no schema declared, are the same bytes.
    /// </summary>
    [Fact]
    public void ProducedOutput_serializes_Schema_as_a_string_and_omits_the_default()
    {
        var schemad = JsonSerializer.Serialize(new ProducedOutput("verdict.json", Schema: OutputSchema.ReviewVerdict));
        Assert.Contains("\"ReviewVerdict\"", schemad);

        var plain = JsonSerializer.Serialize(new ProducedOutput("plan"));
        Assert.DoesNotContain("Schema", plain);

        var roundTripped = JsonSerializer.Deserialize<ProducedOutput>(schemad);
        Assert.Equal(OutputSchema.ReviewVerdict, roundTripped!.Schema);

        var caseInsensitive = JsonSerializer.Deserialize<ProducedOutput>(
            """{"Name": "verdict.json", "Schema": "reviewverdict"}""");
        Assert.Equal(OutputSchema.ReviewVerdict, caseInsensitive!.Schema);
    }
}
