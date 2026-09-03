using System.Text.Json.Serialization;

namespace Baton.Status;

/// <summary>
/// #734 (spec/baton.md §7): the two declared-output names <c>Baton.Cli.Daemon.DeliveryPoller</c>
/// recognizes by name — the same "search a room's already-resolved outputs for a literal file name"
/// pattern <c>Baton.Cli.WatchFireService.BuildPayload</c>'s own <c>verdict.json</c> lookup already
/// uses. A workflow step includes either or both of these names in its
/// <see cref="Domain.WorkflowStepDefinition.Outputs"/>, and the worker writes the branch name / PR
/// number as that file's own content, to have the poller pick the room up. Neither name is validated
/// or reserved anywhere else — <see cref="Domain.ProducedOutput"/> accepts them like any other
/// declared output name.
/// </summary>
public static class DeliveryReferenceOutputNames
{
    public const string Branch = "delivery-branch.txt";
    public const string PullRequest = "delivery-pr.txt";
}

/// <summary>A room's declared delivery reference, resolved from its own produced outputs.</summary>
public sealed record DeliveryReference(int? PullRequestNumber, string? Branch);

/// <summary>
/// #734: reads <see cref="DeliveryReferenceOutputNames"/> off a room's already-resolved output paths
/// — the same list <see cref="StepOutputResolver"/> produces and
/// <c>WorkflowStatusProjector</c>/<c>TerminalSentinelWriter</c> already carry as <c>Outputs</c> —
/// never a second output-resolution pass of its own.
/// </summary>
public static class DeliveryReferenceResolver
{
    public static DeliveryReference? Resolve(IReadOnlyList<string>? outputs)
    {
        if (outputs is null || outputs.Count == 0)
        {
            return null;
        }

        var branch = ReadNamed(outputs, DeliveryReferenceOutputNames.Branch);
        var prText = ReadNamed(outputs, DeliveryReferenceOutputNames.PullRequest);
        int? pullRequestNumber = prText is not null && int.TryParse(prText, out var parsed) ? parsed : null;

        return branch is null && pullRequestNumber is null ? null : new DeliveryReference(pullRequestNumber, branch);
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

/// <summary>
/// #734: <c>fleet_status</c>'s own per-room delivery summary (spec/baton.md §6/§7) — the latest
/// <c>FlowEvent.Delivery*</c> fact already journaled for the room, never a live <c>gh</c> read of its
/// own. Absent until the poller has actually recorded a first fact.
/// </summary>
public sealed record DeliveryStatusView(
    [property: JsonPropertyName("pr")] int Pr,
    [property: JsonPropertyName("state")] string State);
