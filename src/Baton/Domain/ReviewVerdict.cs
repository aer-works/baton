using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// A review worker's structured findings (#732) — the artifact a review-shaped step declares as a
/// schema-checked <see cref="ProducedOutput"/>. Not to be confused with
/// <see cref="Outcomes.OutcomeVerdict"/>, which is Flow's own classification of how an execution
/// ended; a <see cref="ReviewVerdict"/> is content a worker wrote, and per decision 0043 the engine
/// only ever checks that it <i>parses</i> — severity and status are evidence surfaced to a person,
/// never inputs to routing (Architecture Rule 1, decision 0038).
/// </summary>
/// <param name="ReviewedRef">
/// What was reviewed — a branch, commit, or PR reference. Required: an unanchored verdict cannot
/// answer "which code was this even about", which is the first question anyone reading one asks.
/// </param>
/// <param name="Findings">Empty is valid and meaningful: the reviewer looked and found nothing.</param>
/// <param name="Summary">Optional free-text overall assessment.</param>
public sealed record ReviewVerdict(
    string ReviewedRef,
    IReadOnlyList<ReviewFinding> Findings,
    string? Summary = null);

/// <summary>One thing a review claims (#732).</summary>
/// <param name="Claim">The one-line statement of the finding. Required and non-empty.</param>
/// <param name="Anchor">Where in the reviewed code the claim points, when it points anywhere.</param>
/// <param name="Detail">Free-text elaboration — evidence, reproduction, reasoning.</param>
public sealed record ReviewFinding(
    ReviewFindingSeverity Severity,
    string Claim,
    ReviewFindingStatus Status,
    ReviewFindingAnchor? Anchor = null,
    string? Detail = null);

/// <summary>A file (and optionally line) a <see cref="ReviewFinding"/> anchors to.</summary>
public sealed record ReviewFindingAnchor(string File, int? Line = null);

/// <summary>
/// How much a <see cref="ReviewFinding"/> matters, in the reviewer's judgment. Three levels on
/// purpose: every finer-grained scale this project has met collapsed to "act / read / skim" in use.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewFindingSeverity>))]
public enum ReviewFindingSeverity
{
    High,
    Medium,
    Low,
}

/// <summary>
/// How far the reviewer verified the claim: <see cref="Confirmed"/> means reproduced or proven
/// against the code, <see cref="Refuted"/> means investigated and found untrue (kept because a
/// refuted suspicion is evidence too), <see cref="Unverified"/> means stated but not checked.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewFindingStatus>))]
public enum ReviewFindingStatus
{
    Confirmed,
    Refuted,
    Unverified,
}

/// <summary>
/// The parse half of the verdict contract: turns bytes on disk into a <see cref="ReviewVerdict"/>
/// or one sentence saying why they aren't one. <c>ContractValidator</c> consults this at
/// execution-complete the same way it evaluates an <see cref="OutputCondition"/>; readers (CLI, UI,
/// tools) use the same method so there is exactly one definition of "valid verdict".
/// </summary>
public static class ReviewVerdictSchema
{
    /// <summary>
    /// Case-insensitive on property names and enum values — the writers are vendor CLI workers,
    /// and losing a verdict to <c>"high"</c> vs <c>"High"</c> would fail runs over nothing.
    /// Unknown extra fields are tolerated (a worker may annotate; the schema names what must be
    /// there, not all that may be).
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// True with a non-null <paramref name="verdict"/> when <paramref name="bytes"/> parse and
    /// pass the semantic floor; false with a human-readable <paramref name="error"/> otherwise.
    /// Never throws on bad content — a worker wrote these bytes, and worker-controlled content
    /// must land as a classified failure, not an escaped exception.
    /// </summary>
    public static bool TryParse(byte[] bytes, out ReviewVerdict? verdict, out string? error)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        verdict = null;

        ReviewVerdict? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReviewVerdict>(bytes, Options);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        if (parsed is null)
        {
            error = "The verdict document is JSON null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.ReviewedRef))
        {
            error = "'reviewedRef' must name what was reviewed (a branch, commit, or PR).";
            return false;
        }

        // STJ binds an absent constructor parameter to its default — null here, despite the
        // non-nullable declaration — rather than throwing, so the shape floor is enforced by hand.
        if (parsed.Findings is null)
        {
            error = "'findings' must be present — an empty array when the review found nothing.";
            return false;
        }

        for (var i = 0; i < parsed.Findings.Count; i++)
        {
            var finding = parsed.Findings[i];
            if (finding is null)
            {
                error = $"findings[{i}] is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(finding.Claim))
            {
                error = $"findings[{i}].claim must be a non-empty one-line statement.";
                return false;
            }

            if (finding.Anchor is { Line: < 1 })
            {
                error = $"findings[{i}].anchor.line must be 1 or greater when present.";
                return false;
            }

            // The same deserializer leniency the findings check above guards against: File is
            // declared non-nullable, and STJ will happily bind an anchor without one. Found by the
            // schema's own first live reviewer.
            if (finding.Anchor is not null && string.IsNullOrWhiteSpace(finding.Anchor.File))
            {
                error = $"findings[{i}].anchor.file must name a file when an anchor is present.";
                return false;
            }
        }

        verdict = parsed;
        error = null;
        return true;
    }
}
