using Aer.Flow.Domain;

namespace Aer.RoomSession;

/// <summary>
/// The room's files as one list (0021 §2; #1340, slice 1 of the plan the issue names) — a
/// projection over facts <see cref="ArtifactLineage"/> already carries, re-grouped by name instead
/// of by execution. <see cref="ArtifactLineageProjector"/>'s <c>OutputFiles</c> is per-execution;
/// this is the same facts read the way 0021 wants them presented, one entry per distinct file name.
/// </summary>
public sealed record RoomFiles(IReadOnlyList<RoomFile> Files);

/// <summary>
/// One file, identified by name (the same identity <see cref="Aer.Flow.Artifacts.ArtifactManager.ResolveInputPaths"/>
/// already keys handover on), and its version chain in <see cref="Versions"/> — oldest first, in the
/// order the Event Store recorded the executions that produced them, never by timestamp (0021 §2;
/// see <see cref="FileVersion.ProducedAt"/> for why timestamp order would be the wrong axis). A
/// retried step's successive executions are successive versions of the same file, exactly like a
/// handover between two different steps producing the same name.
/// </summary>
public sealed record RoomFile(string Name, IReadOnlyList<FileVersion> Versions);

/// <summary>
/// One version of a <see cref="RoomFile"/>: who produced it, when, and where it lives on disk.
/// </summary>
/// <param name="ProducedAt">
/// The producing execution's terminal event, read off the journal envelope's writer stamp — an
/// honest gap (<see langword="null"/>) rather than a fabricated instant when that stamp predates the
/// field, or the execution never reached a terminal event. Never derived from wall-clock time read
/// at projection time, which is not the record.
/// </param>
/// <param name="Origin">
/// An internal handle for addressing this version — not a fact for display. 0021 §2's rule that
/// execution directories and numbers are never surfaced anywhere applies to this field precisely:
/// it exists so a version can be addressed, and must never reach a rendered string.
/// </param>
public sealed record FileVersion(string Worker, DateTimeOffset? ProducedAt, string FilePath, ExecutionId Origin);
