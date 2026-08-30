using Baton.Flow.Domain;

namespace Baton.Flow.Outcomes;

/// <summary>
/// Aggregates and prunes <see cref="CoreEvent.ExecutionStarted"/> and <see cref="CoreEvent.ExecutionExited"/>
/// core event facts carried across projection checkpoints (#971 continuation).
/// </summary>
public static class CoreEventAggregation
{
    /// <summary>
    /// Merges carried core aggregates from a <see cref="Projection.ProjectionCheckpointState"/> with
    /// core events read from the event log tail.
    /// </summary>
    public static (HashSet<ExecutionId> StartedExecutionIds, Dictionary<ExecutionId, CoreEvent.ExecutionExited> ExitedByExecutionId) Merge(
        IReadOnlySet<ExecutionId>? carriedStarted,
        IReadOnlyDictionary<ExecutionId, CoreEvent.ExecutionExited>? carriedExited,
        IReadOnlyList<CoreEvent> tailCoreEvents)
    {
        ArgumentNullException.ThrowIfNull(tailCoreEvents);

        var started = carriedStarted is not null
            ? new HashSet<ExecutionId>(carriedStarted)
            : new HashSet<ExecutionId>();

        var exited = carriedExited is not null
            ? new Dictionary<ExecutionId, CoreEvent.ExecutionExited>(carriedExited)
            : new Dictionary<ExecutionId, CoreEvent.ExecutionExited>();

        foreach (var coreEvent in tailCoreEvents)
        {
            switch (coreEvent)
            {
                case CoreEvent.ExecutionStarted startedEvent:
                    started.Add(startedEvent.ExecutionId);
                    break;
                case CoreEvent.ExecutionExited exitedEvent:
                    exited[exitedEvent.ExecutionId] = exitedEvent;
                    break;
            }
        }

        return (started, exited);
    }

    /// <summary>
    /// Prunes core aggregates to keep only entries whose <see cref="ExecutionId"/> is some step's
    /// <see cref="StepState.LatestExecutionId"/> with <see cref="StepState.Status"/> equal to
    /// <see cref="StepStatus.Running"/> — resolved executions are never consulted again.
    /// </summary>
    public static (HashSet<ExecutionId> StartedExecutionIds, Dictionary<ExecutionId, CoreEvent.ExecutionExited> ExitedByExecutionId) Prune(
        IReadOnlySet<ExecutionId> started,
        IReadOnlyDictionary<ExecutionId, CoreEvent.ExecutionExited> exited,
        FlowState state)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(exited);
        ArgumentNullException.ThrowIfNull(state);

        var runningExecutionIds = state.Steps
            .Where(s => s.Status == StepStatus.Running && s.LatestExecutionId.HasValue)
            .Select(s => s.LatestExecutionId!.Value)
            .ToHashSet();

        var prunedStarted = started.Where(id => runningExecutionIds.Contains(id)).ToHashSet();
        var prunedExited = exited.Where(kvp => runningExecutionIds.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return (prunedStarted, prunedExited);
    }
}
