using Baton.Artifacts;

namespace Baton.Cli;

/// <summary>
/// <c>baton keep</c>/<c>baton unkeep</c> (#1156): the CLI writer <see cref="KeepMarker"/> never had —
/// the marker was readable by the retention machinery (<c>ArtifactPruner</c>) but writable only by
/// hand in the filesystem. Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/> command
/// (no workflow pump, no projected state to report): handled in <c>Program.cs</c> the same way
/// <c>status</c>/<c>templates</c> are.
/// <para>
/// Deliberately does not require the room to be terminal: <see cref="ArtifactPruner.PruneTaskArtifactsAsync"/>
/// checks <see cref="KeepMarker.IsKept"/> before it probes for terminal state
/// (src/Baton/Artifacts/ArtifactPruner.cs), so an operator can mark keep ahead of a room reaching
/// terminal and the sweep honors it the moment it does — requiring terminal here first would be a
/// stricter rule than the pruner itself enforces.
/// </para>
/// </summary>
public static class KeepCommand
{
    public static async Task MarkAsync(KeepOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        RefuseUnlessRoom(options.RoomDirectoryPath, "keep");
        await KeepMarker.MarkKeepAsync(options.RoomDirectoryPath, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Marked keep for '{options.RoomDirectoryPath}'.");
    }

    public static async Task UnmarkAsync(KeepOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        RefuseUnlessRoom(options.RoomDirectoryPath, "unkeep");
        await KeepMarker.ClearKeepAsync(options.RoomDirectoryPath).ConfigureAwait(false);
        output.WriteLine($"Unmarked keep for '{options.RoomDirectoryPath}'.");
    }

    // Reuses RoomLedgerProbe's room-detection predicate (src/Baton.Cli/RoomLedgerProbe.cs) — the same
    // "does flow.jsonl exist and carry at least one recorded byte" check StatusCommand and Program's
    // own pre-ledger sentinel guard already use to decide whether a path is a real room, so a bad
    // path is refused here exactly the way it would be everywhere else in this namespace.
    private static void RefuseUnlessRoom(string roomDirectoryPath, string verb)
    {
        if (!RoomLedgerProbe.HasLedger(roomDirectoryPath))
        {
            throw new CliArgumentException(
                $"'{roomDirectoryPath}' is not a room directory — 'baton {verb}' only operates on a " +
                "room 'baton run' has already started.");
        }
    }
}
