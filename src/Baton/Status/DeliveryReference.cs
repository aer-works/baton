using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Baton.Status;

/// <summary>The two declared-output names <c>Baton.Cli.Daemon.DeliveryPoller</c> recognizes — spec/baton.md §2's "Delivery state facts" names the convention; see there, not restated here.</summary>
public static class DeliveryReferenceOutputNames
{
    public const string Branch = "delivery-branch.txt";
    public const string PullRequest = "delivery-pr.txt";
}

/// <summary>
/// A room's declared delivery reference, resolved from its own produced outputs.
/// </summary>
/// <param name="PullRequestNumber">Parsed out of <paramref name="PullRequestReference"/> regardless of which shape it came in.</param>
/// <param name="PullRequestReference">
/// The declared PR content verbatim (minus a leading <c>#</c>) — a bare number, or a full PR URL. This
/// is what <c>DeliveryPoller</c> hands to <c>gh pr view</c> directly: a URL pins its own repo, so the
/// poller needs no working-directory/repo-root fallback for that shape.
/// </param>
public sealed record DeliveryReference(int? PullRequestNumber, string? PullRequestReference, string? Branch);

/// <summary>
/// #734: reads <see cref="DeliveryReferenceOutputNames"/> off a room's already-resolved output paths
/// — the same list <see cref="StepOutputResolver"/> produces and
/// <c>WorkflowStatusProjector</c>/<c>TerminalSentinelWriter</c> already carry as <c>Outputs</c> —
/// never a second output-resolution pass of its own.
/// </summary>
public static class DeliveryReferenceResolver
{
    // spec/baton.md §2 names the two shapes a worker plausibly writes here (a bare number, a full
    // URL) plus the hand-typed "#123" case; all three read off the same trailing digits.
    private static readonly Regex TrailingNumber = new(@"(\d+)\s*$", RegexOptions.Compiled);

    public static DeliveryReference? Resolve(IReadOnlyList<string>? outputs)
    {
        if (outputs is null || outputs.Count == 0)
        {
            return null;
        }

        var branch = ReadNamed(outputs, DeliveryReferenceOutputNames.Branch);
        var prText = ReadNamed(outputs, DeliveryReferenceOutputNames.PullRequest);

        string? reference = null;
        int? pullRequestNumber = null;
        if (prText is not null)
        {
            reference = prText.StartsWith('#') ? prText[1..] : prText;
            var match = TrailingNumber.Match(prText);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
            {
                pullRequestNumber = parsed;
            }
        }

        return branch is null && pullRequestNumber is null
            ? null
            : new DeliveryReference(pullRequestNumber, reference, branch);
    }

    private static string? ReadNamed(IReadOnlyList<string> outputs, string fileName)
    {
        var path = outputs.FirstOrDefault(
            p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : text;
    }
}

/// <summary>`fleet_status`'s own per-room delivery summary — spec/baton.md's §6 schema block states the field and its absence rule; see there, not restated here.</summary>
public sealed record DeliveryStatusView(
    [property: JsonPropertyName("pr")] int Pr,
    [property: JsonPropertyName("state")] string State);
