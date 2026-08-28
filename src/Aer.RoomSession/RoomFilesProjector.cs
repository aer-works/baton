using Aer.Flow.Artifacts;
using Aer.Flow.Domain;

namespace Aer.RoomSession;

/// <summary>
/// Reconstructs <see cref="RoomFiles"/> from <see cref="ArtifactLineage"/> — never a second
/// directory read: <see cref="ArtifactLineageProjector"/> already listed every execution's output
/// files in event-log order (<c>ArtifactLineageProjectorTests.Executions_are_projected_in_recorded_order</c>),
/// so this only re-groups that same fact by name instead of by execution (0021 §2), adding the one
/// thing <see cref="ArtifactLineage"/> doesn't carry: when. That comes from the same journal entries
/// <see cref="RoomProjectionLoader"/> already reads once for <see cref="Aer.Flow.Projection.StepPauseMoment"/>
/// — read the same honest-gap way <see cref="Aer.Flow.Projection.StepPauseMoment.PausedAt"/> already
/// is, off each producing execution's own terminal event's <see cref="LogEntry.WriterUtcTimestamp"/>,
/// never fabricated when that stamp (or the terminal event itself) is absent.
/// </summary>
public static class RoomFilesProjector
{
    public static RoomFiles Project(ArtifactLineage lineage, IReadOnlyList<LogEntry> entries, string artifactsRootPath)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var producedAtByExecutionId = new Dictionary<ExecutionId, DateTimeOffset?>();
        foreach (var entry in entries)
        {
            if (entry is not LogEntry.FlowLogEntry flowLogEntry)
            {
                continue;
            }

            ExecutionId? terminalExecutionId = flowLogEntry.Event switch
            {
                FlowEvent.ExecutionSucceeded succeeded => succeeded.ExecutionId,
                FlowEvent.ExecutionFailed failed => failed.ExecutionId,
                FlowEvent.ExecutionCancelled cancelled => cancelled.ExecutionId,
                _ => null,
            };

            if (terminalExecutionId is not { } executionId)
            {
                continue;
            }

            producedAtByExecutionId[executionId] = flowLogEntry.WriterUtcTimestamp is { } stamp
                ? new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc))
                : null;
        }

        var versionsByName = new Dictionary<string, List<FileVersion>>(StringComparer.Ordinal);
        var orderedNames = new List<string>();

        foreach (var execution in lineage.Executions)
        {
            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, execution.ExecutionId);
            var producedAt = producedAtByExecutionId.GetValueOrDefault(execution.ExecutionId);

            foreach (var fileName in execution.OutputFiles)
            {
                // #292: prompt.txt is durable capture of what the worker was asked, not something it
                // produced — it stays under the step's Prompt expander, never a room file.
                if (string.Equals(fileName, ArtifactManager.PromptFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!versionsByName.TryGetValue(fileName, out var versions))
                {
                    versions = [];
                    versionsByName[fileName] = versions;
                    orderedNames.Add(fileName);
                }

                versions.Add(new FileVersion(
                    execution.Worker, producedAt, Path.Combine(outputDirectory, fileName), execution.ExecutionId));
            }
        }

        var files = orderedNames
            .Select(name => new RoomFile(name, versionsByName[name]))
            .ToList();

        return new RoomFiles(files);
    }
}
