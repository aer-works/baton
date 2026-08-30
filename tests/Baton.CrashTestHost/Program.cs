using Baton.CrashTestHost;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;

// M10 Phase 4 (issue #72): a small, test-only pump host standing in for Baton.Cli, which is still a
// stub. Baton.Tests spawns this as a real OS process, waits for a specific durable fact to
// appear in the log, then kills it — exercising MutationInterface.StartWorkflowAsync against real
// Core dispatch, then reconciling from a real, killed-mid-run log via a second, in-process run.
//
// args: <pausePoint> <roomDirectory> <artifactsRoot> <logPath> <pauseSignalPath> <cancelSignalPath>
//   pausePoint: "none" | "before-dispatch" | "after-dispatch" (see DispatchPausePoint).
if (args.Length != 6)
{
    await Console.Error.WriteLineAsync(
        "usage: <pausePoint> <roomDirectory> <artifactsRoot> <logPath> <pauseSignalPath> <cancelSignalPath>");
    return 1;
}

var pausePoint = args[0] switch
{
    "none" => DispatchPausePoint.None,
    "before-dispatch" => DispatchPausePoint.BeforeDispatch,
    "after-dispatch" => DispatchPausePoint.AfterDispatch,
    _ => throw new ArgumentException($"Unknown pausePoint '{args[0]}'."),
};
var roomDirectory = args[1];
var artifactsRoot = args[2];
var logPath = args[3];
var pauseSignalPath = args[4];
var cancelSignalPath = args[5];

// The worker only needs to be genuinely long-running for the no-pause (orphan) scenario, where
// this run's own real timing — not a decorator pause — is what leaves it still executing when
// killed. Both paused scenarios never let a real dispatch reach the worker at all (before-dispatch)
// or let it run to a real, fast, natural exit before pausing (after-dispatch).
var workerKind = pausePoint == DispatchPausePoint.None ? ScenarioWorker.LongSleep : ScenarioWorker.QuickSuccess;
var (snapshot, bindings) = Scenarios.Build(workerKind);

await using var writer = new FlowEventLogWriter(logPath);
var reader = new FlowEventLogReader(logPath);
var dispatcher = new PausableCoreDispatcher(new CoreDispatcher(writer), pausePoint, pauseSignalPath);
var inFlightExecutions = new InFlightExecutionRegistry();

// Fire-and-forget: harmless if this process is killed before cancelSignalPath ever appears (the
// common case for every scenario except the unfulfilled-cancellation one), since nothing here has
// any effect until that file exists.
_ = WatchForCancelSignalAsync(cancelSignalPath, reader, inFlightExecutions);

await MutationInterface.StartWorkflowAsync(
    Scenarios.WorkflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher,
    inFlightExecutions: inFlightExecutions);

return 0;

static async Task WatchForCancelSignalAsync(
    string cancelSignalPath, IEventLogReader reader, InFlightExecutionRegistry inFlightExecutions)
{
    try
    {
        while (true)
        {
            try
            {
                if (File.Exists(cancelSignalPath))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient filesystem exception while checking for cancelSignalPath existence under CI load.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }

        // Resolves from this process's own log rather than a passed-in argument: the ExecutionId is
        // minted fresh by MutationInterface on every run and unknowable to the test harness in advance.
        //
        // Retries reading the log and delivering cancellation until RequestCancellationAsync succeeds
        // (returns true): PrepareExecutionAsync durably appends ExecutionRequestAccepted to flow.jsonl
        // BEFORE MutationInterface calls InFlightExecutionRegistry.Register for that execution (issue #513).
        // Under CI load, the test harness can observe ExecutionRequestAccepted and write cancel.signal
        // in that narrow window before Register runs. If the watcher only attempts cancellation once, it
        // finds ExecutionRequestAccepted in the log, calls RequestCancellationAsync before Register has
        // been called, drops the request as a no-op, and exits — hanging the test waiting for
        // CancellationRequested. Polling until RequestCancellationAsync returns true ensures the watcher
        // waits for Register to complete if it races signal file creation.
        while (true)
        {
            try
            {
                var events = await reader.ReadAllAsync().ConfigureAwait(false);
                var accepted = events.OfType<FlowEvent.ExecutionRequestAccepted>().FirstOrDefault(e => e.Request.StepId == Scenarios.StepA);
                if (accepted is not null)
                {
                    if (await inFlightExecutions.RequestCancellationAsync(accepted.Request.ExecutionId).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or FlowEventLogReadException or UnauthorizedAccessException)
            {
                // Transient file access / log-reading collision while FlowEventLogWriter is concurrently flushing or appending.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }
    // Swallows any unhandled non-transient exceptions by writing details to stderr because WatchForCancelSignalAsync is
    // an unawaited background task (_ = WatchForCancelSignalAsync(...)) launched from Main. Rethrowing here would result in an
    // unobserved task exception, whereas writing to stderr ensures background watcher failures are visible in test logs
    // without silently hanging or crashing the process.
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"WatchForCancelSignalAsync failed: {ex}").ConfigureAwait(false);
    }
}
