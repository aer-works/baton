namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton rooms prune</c> (#1659) — the batch form of
/// <see cref="RoomDeleteCommand"/>, plus registry hygiene (dedupe, drop missing-directory lines) that
/// runs unconditionally, independent of <see cref="Terminal"/>.
/// </summary>
/// <param name="Terminal">
/// <c>--terminal</c>: required — the only batch-delete population this verb knows how to select today
/// (a room whose <c>terminal.json</c> is present). Left as an explicit flag, not implied, so a future
/// non-terminal selection is additive rather than a silent behaviour change under the same flag-less
/// invocation.
/// </param>
/// <param name="OlderThanDays">
/// <c>--older-than &lt;days&gt;</c>: only a room whose <c>terminal.json</c> is at least this many days
/// old is a delete candidate. <c>null</c> when omitted — every terminal room matching
/// <see cref="State"/> is a candidate regardless of age.
/// </param>
/// <param name="State">
/// <c>--state &lt;Succeeded|Failed|Cancelled&gt;</c>: restricts candidates to rooms whose
/// <c>terminal.json</c> outcome (<see cref="Baton.Status.WorkflowOutcome"/>) matches exactly.
/// <c>null</c> when omitted — every terminal outcome is a candidate.
/// </param>
/// <param name="DryRun">
/// <c>--dry-run</c>: explicit request for the listing-only behaviour that is already the default
/// when <see cref="Yes"/> is not given — accepted so a caller can say so without also being forced to
/// omit <c>--yes</c> by omission alone. <see cref="RoomsPruneOptionsParser.Parse"/> is the only place
/// this property's value matters: it rejects <c>--dry-run --yes</c> together rather than letting
/// either flag silently win, so by the time a <see cref="RoomsPruneOptions"/> reaches
/// <see cref="RoomsPruneCommand"/> the two never disagree and <see cref="Yes"/> alone decides the
/// command's behaviour.
/// </param>
/// <param name="Yes">
/// <c>--yes</c>: without it, this verb only ever lists what it would remove — mutates nothing. Deletion
/// requires this flag explicitly.
/// </param>
public sealed record RoomsPruneOptions(
    bool Terminal,
    int? OlderThanDays,
    string? State,
    bool DryRun,
    bool Yes);
