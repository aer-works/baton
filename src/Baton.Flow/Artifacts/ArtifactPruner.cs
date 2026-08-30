using Baton.Flow.Concurrency;
using Baton.Flow.Domain;
using Baton.Flow.Store;

namespace Baton.Flow.Artifacts;

/// <summary>
/// Implements artifact pruning for completed runs (ADR 0009 Scope 3, #973).
/// <para>
/// <b>Pruning is NOT deletion:</b> Moves completed run artifact directories from active path
/// (<c>{artifacts}/execution_{id}</c>) to recoverable location (<c>{artifacts}/pruned/execution_{id}</c>).
/// </para>
/// <para>
/// <b>Scope:</b> Completed runs only (<see cref="WorkflowStatus.Terminal"/>). Live or paused runs are untouched.
/// <b>Keep exempts:</b> A run marked keep (<see cref="KeepMarker.IsKept"/>) is never pruned.
/// <b>Crash-safe &amp; idempotent:</b> Uses <see cref="RetryingFileMove.MoveDirectory"/>. Pruning twice is a no-op.
/// <b>Derivable:</b> Provenance is derivable from the Event Store alone — no side-table.
/// </para>
/// </summary>
public static class ArtifactPruner
{
    /// <summary>
    /// Prunes artifacts for the room at <paramref name="roomDirectoryPath"/> if it is terminal and not marked keep.
    /// Returns <c>true</c> if any artifact directory was pruned (moved), or <c>false</c> otherwise.
    /// <para>
    /// <b>Caller constraint:</b> acquires the room's <see cref="ConcurrencyGuard"/> non-reentrantly
    /// (an OS <c>FileShare.None</c> hold), so a caller that <i>already</i> holds the room lock will get a
    /// <see cref="Baton.Flow.Concurrency.WorkflowLockedException"/>, not reentrancy. Whatever policy eventually
    /// wires this (#1027) must invoke it from a context that does not hold the lock — a periodic sweep, not
    /// the in-line terminal-transition path if that path is already inside the guard.
    /// </para>
    /// </summary>
    public static async Task<bool> PruneAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        return await PruneTaskArtifactsAsync(roomDirectoryPath, artifactsRootPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prunes active execution artifact directories under <paramref name="artifactsRootPath"/> for the room at
    /// <paramref name="roomDirectoryPath"/> if the run is terminal and not marked keep.
    /// </summary>
    public static async Task<bool> PruneTaskArtifactsAsync(
        string roomDirectoryPath,
        string artifactsRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        if (!Directory.Exists(artifactsRootPath))
        {
            return false;
        }

        if (KeepMarker.IsKept(roomDirectoryPath))
        {
            return false;
        }

        // Held across probe AND move (#973 second reader, Finding 3; the pattern #972's compactor set):
        // the probe reads a run terminal, then this MOVES the directory a resumed run would write back
        // into. Without the lock, an `baton decide`/`supply` resume can repopulate execution_{id} between
        // the two steps, and the move then pulls the active directory out from under the resumed write.
        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, "artifact pruning");

        var probeResult = await WorkflowTerminalProbe.ProbeAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
        if (!probeResult.IsTerminal)
        {
            return false;
        }

        var executionDirs = Directory.GetDirectories(artifactsRootPath, "execution_*", SearchOption.TopDirectoryOnly);
        if (executionDirs.Length == 0)
        {
            return false;
        }

        var prunedAny = false;
        foreach (var execDir in executionDirs)
        {
            var dirName = Path.GetFileName(execDir);
            var targetDir = Path.Combine(artifactsRootPath, ArtifactManager.PrunedDirectoryName, dirName);

            prunedAny |= PruneDirectory(execDir, targetDir);
        }

        return prunedAny;
    }

    /// <summary>
    /// Moves an active execution directory <paramref name="sourceDir"/> to <paramref name="targetDir"/>,
    /// returning <c>true</c> only when a move actually happened.
    /// <para>
    /// A pre-existing <paramref name="targetDir"/> is a recoverable copy from an earlier prune, and this
    /// leaves it untouched rather than deleting it — pruning never deletes (0009), and a delete-then-move
    /// would destroy that copy the moment a resumed run repopulated <paramref name="sourceDir"/> at the
    /// same execution path (the race behind #973's second reader Findings 1 and 3). The same-volume
    /// <see cref="RetryingFileMove.MoveDirectory"/> is a single rename, so the move itself is atomic; the
    /// only unsafe step was the removed delete.
    /// </para>
    /// </summary>
    public static bool PruneDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            // Already pruned or missing - no-op.
            return false;
        }

        if (Directory.Exists(targetDir))
        {
            // A recoverable copy is already here. Leave BOTH it and the source in place: destroying
            // the copy to re-move over it is the one thing this feature must never do.
            return false;
        }

        RetryingFileMove.MoveDirectory(sourceDir, targetDir);
        return true;
    }
}
