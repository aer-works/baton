using Aer.Flow.Artifacts;
using Aer.Flow.Domain;

namespace Aer.Flow.Status;

/// <summary>
/// #740's rule for which steps' declared outputs are listable: a step whose execution itself
/// Succeeded, or a Paused step whose underlying outcome already Succeeded (the ready-for-review
/// approval gate). One implementation, called by both <c>FlowStateReporter</c> (<c>aer
/// run</c>'s printed <c>name -&gt; path</c> lines) and <see cref="WorkflowStatusProjector"/>
/// (<c>status --json</c>'s <c>outputs</c> field, and the terminal sentinel it shares a shape with) —
/// #1374 F5, so the two can no longer silently disagree about the same room.
/// </summary>
public static class StepOutputResolver
{
    public static IEnumerable<(string Name, string Path)> Resolve(
        StepState step, WorkflowStepDefinition? stepDefinition, string artifactsRootPath)
    {
        var executionSucceeded = step.Status == StepStatus.Succeeded
            || (step.Status == StepStatus.Paused && step.PausedOutcome == StepStatus.Succeeded);

        if (!executionSucceeded || step.LatestExecutionId is null || stepDefinition is null)
        {
            yield break;
        }

        var outputDirectory = ArtifactManager.ResolveOutputDirectory(artifactsRootPath, step.LatestExecutionId.Value);
        foreach (var outputName in stepDefinition.Outputs)
        {
            yield return (outputName, Path.Combine(outputDirectory, outputName));
        }
    }
}
