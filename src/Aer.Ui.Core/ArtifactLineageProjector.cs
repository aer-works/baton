using Aer.Flow.Artifacts;
using Aer.Flow.Domain;

namespace Aer.Ui.Core;

/// <summary>
/// Reconstructs <see cref="ArtifactLineage"/> from event history plus the artifacts directory
/// (UI spec §10, §11; M14 Phase 4, issue #121) — the same one-more-pass-over-the-same-event-list
/// shape <see cref="ExecutionHistoryProjector"/> established (M14 Phase 2), extended to also read
/// artifact directories, which spec §12's transparency rule names as a legitimate projection input
/// alongside the snapshot and Event Store. Never calls into <see cref="Aer.Flow.Domain.FlowState"/>
/// or <c>StateProjector</c>'s retry/staleness/readiness logic — every fact here is either read
/// straight off an <see cref="ExecutionRequest"/> as recorded, or off disk.
/// </summary>
public static class ArtifactLineageProjector
{
    public static ArtifactLineage Project(
        IReadOnlyList<FlowEvent> events, WorkflowDefinitionSnapshot snapshot, string artifactsRootPath)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var stepDefinitionById = snapshot.Steps.ToDictionary(step => step.StepId);
        var producerStepIdByOutputNameCache = new Dictionary<StepId, Dictionary<string, StepId>>();

        Dictionary<string, StepId> ProducerStepIdByOutputName(WorkflowStepDefinition step)
        {
            if (producerStepIdByOutputNameCache.TryGetValue(step.StepId, out var cached))
            {
                return cached;
            }

            var map = new Dictionary<string, StepId>();
            foreach (var dependencyStepId in step.DependsOn)
            {
                var dependency = stepDefinitionById[dependencyStepId];
                foreach (var outputName in dependency.Outputs)
                {
                    map[outputName] = dependencyStepId;
                }
            }

            producerStepIdByOutputNameCache[step.StepId] = map;
            return map;
        }

        var executions = new List<ExecutionArtifacts>();

        foreach (var flowEvent in events)
        {
            if (flowEvent is not FlowEvent.ExecutionRequestAccepted accepted)
            {
                continue;
            }

            var request = accepted.Request;

            var inputLinks = new List<ArtifactInputLink>();
            if (request.StepId is { } stepId)
            {
                var step = stepDefinitionById[stepId];
                var producers = ProducerStepIdByOutputName(step);

                // Deliberately walks the snapshot's declared step.Inputs (durable, structural), not
                // request.Inputs — the latter holds ArtifactManager.ResolveInputPaths' already-resolved
                // file paths (§16), not the bare input names this lookup needs to key against.
                foreach (var inputName in step.Inputs)
                {
                    if (producers.TryGetValue(inputName, out var producerStepId) &&
                        request.UpstreamExecutionIds.TryGetValue(producerStepId, out var producerExecutionId))
                    {
                        inputLinks.Add(new ArtifactInputLink(inputName, producerStepId, producerExecutionId));
                    }
                }
            }

            var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, request.ExecutionId);
            var outputFiles = Directory.Exists(outputDirectory)
                ? Directory.GetFiles(outputDirectory)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList()
                : (IReadOnlyList<string>)[];

            executions.Add(new ExecutionArtifacts(
                request.ExecutionId, request.StepId, request.Worker, outputFiles, inputLinks, request.Outputs));
        }

        return new ArtifactLineage(executions);
    }
}
