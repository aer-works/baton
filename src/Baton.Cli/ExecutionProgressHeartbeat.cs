using System.Globalization;
using Baton.Artifacts;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Projection;
using Baton.Status;
using Baton.Store;

namespace Baton.Cli;

/// <summary>
/// #1549: the pump-side source of <see cref="FlowEvent.ExecutionProgress"/> — a coarse,
/// content-free heartbeat that closes the gap a healthy, long-running lane otherwise leaves in
/// <c>flow.jsonl</c>: zero journal events between <see cref="FlowEvent.ExecutionRequestAccepted"/>
/// and its terminal outcome, which made every "last journal event age" reader (the Fleet Glass ⚠,
/// an anomaly rule) misread a busy worker as stalled (spec/baton.md §7's "false Running ⚠" entry).
/// Started and stopped by <see cref="RunCommand"/> alongside its own
/// <see cref="Mutation.MutationInterface.StartWorkflowAsync"/> call, the identical fire-and-forget
/// shape <see cref="CancelRequestPoller"/> already uses — a sibling poller, not a merge into that
/// one, since the two have unrelated cadences and unrelated failure handling.
/// </summary>
/// <remarks>
/// Ticks on <see cref="GetInterval"/>'s own cadence (default 5 minutes) rather than polling
/// frequently and gating internally: the check interval already IS the coarse cadence the heartbeat
/// wants, so there is no separate internal gate to keep in sync with it. On each tick, resolves the
/// room's one candidate execution (<see cref="RunningExecutionResolver"/> — the same "fail closed
/// on zero or more than one candidate" resolver <see cref="CancelRequestPoller"/> already uses, so a
/// room running more than one execution concurrently gets no heartbeat for either, a stated coverage
/// limit rather than a silent gap) and stats its <c>.stdout.log</c>. An event is appended only when
/// that file's mtime has advanced since the last tick THIS instance observed for THAT execution —
/// never on the first tick a fresh execution id is seen (its current mtime becomes the baseline,
/// with nothing yet to compare it against), and never for a non-process dispatch or one whose
/// output directory has no <c>.stdout.log</c> at all (nothing to stat). A wedged worker's stdout
/// never advances, so its heartbeat correctly goes quiet too — that is the entire point of gating on
/// mtime rather than emitting unconditionally on a timer; this is not a keepalive.
/// </remarks>
public static class ExecutionProgressHeartbeat
{
    public const string IntervalSecondsEnvironmentVariable = "BATON_EXECUTION_PROGRESS_INTERVAL_SECONDS";

    public static readonly TimeSpan PlaceholderDefaultInterval = TimeSpan.FromMinutes(5);

    // Same bounds rationale as RoomRetentionSweep.MinInterval/MaxInterval: the upper bound keeps a
    // pathological value (e.g. "1e300") from overflowing TimeSpan.FromSeconds, and the lower bound
    // keeps a sub-second typo from hot-looping this poller's own Task.Delay.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromDays(1);

    public static TimeSpan GetInterval()
    {
        var val = BatonEnvironmentSnapshot.Current.ExecutionProgressIntervalSecondsOverride;
        if (!string.IsNullOrWhiteSpace(val) &&
            double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, MinInterval.TotalSeconds, MaxInterval.TotalSeconds));
        }

        return PlaceholderDefaultInterval;
    }

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> fires. Never throws for a single tick's own
    /// fault (a torn log read racing the pump's own writer, a transient filesystem error) — every
    /// exception except the loop's own cancellation is caught, logged, and the loop continues, the
    /// same fire-and-forget contract <see cref="CancelRequestPoller.RunAsync"/> already documents.
    /// </summary>
    public static async Task RunAsync(
        string roomDirectoryPath,
        string logPath,
        string artifactsRootPath,
        WorkflowDefinitionSnapshot snapshot,
        IEventLogWriter eventLogWriter,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var tracker = new Tracker(null, null);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                tracker = await TickAsync(
                        logPath, artifactsRootPath, snapshot, eventLogWriter, tracker, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine($"execution progress heartbeat against '{roomDirectoryPath}' failed this tick: {ex.Message}");
                }
                catch
                {
                    // Swallow a broken stderr pipe, matching CancelRequestPoller's own F6 guard.
                }
            }
        }
    }

    /// <summary>Which execution this instance last observed, and the <c>.stdout.log</c> mtime it last saw for it.</summary>
    internal readonly record struct Tracker(ExecutionId? ExecutionId, DateTime? LastSeenStdoutMtimeUtc);

    internal static async Task<Tracker> TickAsync(
        string logPath,
        string artifactsRootPath,
        WorkflowDefinitionSnapshot snapshot,
        IEventLogWriter eventLogWriter,
        Tracker tracker,
        CancellationToken cancellationToken)
    {
        var reader = new FlowEventLogReader(logPath);
        var events = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var state = StateProjector.Project(events, snapshot);
        var resolved = RunningExecutionResolver.Resolve(state);

        if (resolved.Single is not { } executionId)
        {
            // Zero or more than one candidate: nothing to heartbeat this tick, and nothing carried
            // forward -- a candidate that reappears later starts fresh, exactly like a newly-seen one.
            return new Tracker(null, null);
        }

        var stdoutPath = Path.Combine(
            ArtifactManager.ResolveOutputDirectory(artifactsRootPath, executionId), ExecutionStreamLogger.StdoutLogFileName);
        if (!File.Exists(stdoutPath))
        {
            // A non-process dispatch, or a process one that hasn't written its first stdout chunk
            // yet -- nothing to stat, so nothing to compare on the next tick either.
            return new Tracker(executionId, null);
        }

        var mtimeUtc = File.GetLastWriteTimeUtc(stdoutPath);

        if (tracker.ExecutionId != executionId)
        {
            // First observation of this execution: this tick's mtime becomes the baseline. Never an
            // immediate emission -- the heartbeat only ever reports an ADVANCE it has itself observed.
            return new Tracker(executionId, mtimeUtc);
        }

        if (tracker.LastSeenStdoutMtimeUtc is { } lastSeen && mtimeUtc > lastSeen)
        {
            await eventLogWriter.AppendAsync(new FlowEvent.ExecutionProgress(executionId), cancellationToken).ConfigureAwait(false);
            return new Tracker(executionId, mtimeUtc);
        }

        return tracker;
    }
}
