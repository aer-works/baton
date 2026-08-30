using System.Diagnostics;
using Baton.Artifacts;
using Baton.Concurrency;
using Baton.CrashTestHost;
using Baton.Dispatch;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Tests.TestSupport;
using static Baton.Tests.TestSupport.CrashTestHostLauncher;

namespace Baton.Tests.EndToEnd;

/// <summary>
/// M10 Phase 4 (issue #72): the real-process half of the crash-durability guarantee. Every fixture
/// here launches <c>Baton.CrashTestHost</c> (see its own <c>Program.cs</c> header for why it
/// exists) as a genuinely separate OS process, waits for the exact durable
/// fact that defines one of <see cref="Outcomes.ProcessCrashRecoveryDetector"/>'s four crash states
/// to appear in its log, then kills that process outright and reconciles from the resulting,
/// genuinely crashed log via a second, in-process <see cref="MutationInterface.StartWorkflowAsync"/>
/// call against a real <see cref="CoreDispatcher"/> — the real-process counterpart to
/// <c>MutationInterfaceCrashRecoveryTests</c>' mutation-level fixtures, which manufacture the same
/// four states by hand-writing exactly the log lines a real crash would leave behind rather than by
/// killing a real process.
/// </summary>
public class CrashRecoveryEndToEndTests
{
    [Fact]
    public async Task A_host_killed_before_a_real_dispatch_ever_starts_has_its_recorded_intent_resubmitted_on_recovery()
    {
        var (roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal) = MakeTaskPaths();
        try
        {
            var host = Launch("before-dispatch", roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal);
            try
            {
                // Durable proof the safe pre-spawn crash state has been reached: the intent is
                // fsync'd, and — because this run's dispatcher is paused before ever calling the
                // real one — no CoreEvent has been or ever will be written for it by this process.
                await WaitForLogConditionAsync(logPath, s => s.FlowEvents.OfType<FlowEvent.ExecutionRequestAccepted>().Any());
                await KillAndWaitAsync(host);
            }
            finally
            {
                if (!host.HasExited)
                {
                    host.Kill();
                }
            }

            var originalExecutionId = await GetAcceptedExecutionIdAsync(logPath);

            var finalState = await RunRecoveryAsync(roomDirectory, artifactsRoot, logPath, ScenarioWorker.QuickSuccess);

            var stepState = finalState.Steps.Single();
            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.Equal(originalExecutionId, stepState.LatestExecutionId);

            // The same attempt, not a retry: still exactly one ExecutionRequestAccepted for it.
            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());

            await AssertResultFileExistsAsync(artifactsRoot, originalExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_host_killed_after_an_unfulfilled_cancellation_for_a_never_started_execution_finalizes_it_cancelled_on_recovery()
    {
        var (roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal) = MakeTaskPaths();
        try
        {
            var host = Launch("before-dispatch", roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal);
            try
            {
                await WaitForLogConditionAsync(logPath, s => s.FlowEvents.OfType<FlowEvent.ExecutionRequestAccepted>().Any());

                // Tells the still-running host's own background watcher to request cancellation
                // in-process (the only real delivery point for a live execution, M10 Phase 2) —
                // never forwarded to Core, since the paused dispatcher never lets a real one start.
                await File.WriteAllTextAsync(cancelSignal, "go", TestContext.Current.CancellationToken);
                await WaitForLogConditionAsync(logPath, s => s.FlowEvents.OfType<FlowEvent.CancellationRequested>().Any());

                await KillAndWaitAsync(host);
            }
            finally
            {
                if (!host.HasExited)
                {
                    host.Kill();
                }
            }

            var originalExecutionId = await GetAcceptedExecutionIdAsync(logPath);

            var finalState = await RunRecoveryAsync(roomDirectory, artifactsRoot, logPath, ScenarioWorker.QuickSuccess);

            var stepState = finalState.Steps.Single();
            Assert.Equal(StepStatus.Cancelled, stepState.Status);
            Assert.Equal(originalExecutionId, stepState.LatestExecutionId);

            // The cancel won outright: never dispatched, so still exactly one ExecutionRequestAccepted.
            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
            Assert.Empty(await reader.ReadAllCoreEventsAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_host_killed_after_a_real_process_exits_but_before_classification_classifies_it_on_recovery_from_the_recorded_exit()
    {
        var (roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal) = MakeTaskPaths();
        try
        {
            var host = Launch("after-dispatch", roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal);
            try
            {
                // Durable proof the real process really ran and really exited (the "ran while Flow
                // was down" window): CoreDispatcher.DispatchAsync does not return — and so this
                // run's decorator cannot yet be paused after it — until both ExecutionStarted and
                // ExecutionExited are themselves durably appended.
                await WaitForLogConditionAsync(logPath, s => s.CoreEvents.OfType<CoreEvent.ExecutionExited>().Any());
                await KillAndWaitAsync(host);
            }
            finally
            {
                if (!host.HasExited)
                {
                    host.Kill();
                }
            }

            var originalExecutionId = await GetAcceptedExecutionIdAsync(logPath);

            var finalState = await RunRecoveryAsync(roomDirectory, artifactsRoot, logPath, ScenarioWorker.QuickSuccess);

            var stepState = finalState.Steps.Single();
            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.Equal(originalExecutionId, stepState.LatestExecutionId);

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Single(events.OfType<FlowEvent.ExecutionRequestAccepted>());
            Assert.Single(events, e => e is FlowEvent.ExecutionSucceeded es && es.ExecutionId == originalExecutionId);

            // The classification really read the real process's real output, not a stand-in fact.
            await AssertResultFileExistsAsync(artifactsRoot, originalExecutionId);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_host_killed_while_its_real_child_is_still_running_leaves_the_attempt_abandoned_and_retried_on_recovery()
    {
        var (roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal) = MakeTaskPaths();
        var orphanedChildPid = -1;
        try
        {
            var host = Launch("none", roomDirectory, artifactsRoot, logPath, pauseSignal, cancelSignal);
            try
            {
                // Durable proof of a real, still-executing child (the third crash state, the
                // orphan): the worker sleeps for two minutes, so ExecutionStarted appearing proves
                // it is genuinely running right now, not that it has already exited.
                await WaitForLogConditionAsync(logPath, s => s.CoreEvents.OfType<CoreEvent.ExecutionStarted>().Any());

                var started = (await new FlowEventLogReader(logPath).ReadAllCoreEventsAsync(TestContext.Current.CancellationToken))
                    .OfType<CoreEvent.ExecutionStarted>().Single();
                orphanedChildPid = checked((int)started.Pid);

                await KillAndWaitAsync(host);
            }
            finally
            {
                if (!host.HasExited)
                {
                    host.Kill();
                }
            }

            var orphanExecutionId = await GetAcceptedExecutionIdAsync(logPath);

            // A fresh, fast binding for the retry (the fresh-output-directory-per-attempt rule is what
            // actually protects correctness here, not the still-possibly-alive orphan going away) —
            // the operator recovering from this crash has no reason to repeat the same 2-minute
            // sleep, and this test would otherwise block for it.
            var finalState = await RunRecoveryAsync(roomDirectory, artifactsRoot, logPath, ScenarioWorker.QuickSuccess);

            var stepState = finalState.Steps.Single();
            Assert.Equal(StepStatus.Succeeded, stepState.Status);
            Assert.NotEqual(orphanExecutionId, stepState.LatestExecutionId);

            var reader = new FlowEventLogReader(logPath);
            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, events.OfType<FlowEvent.ExecutionRequestAccepted>().Count());
            var abandoned = Assert.Single(events.OfType<FlowEvent.ExecutionFailed>());
            Assert.Equal(orphanExecutionId, abandoned.ExecutionId);
            Assert.Equal(FailureClassification.Retryable, abandoned.FailureClassification);

            // #597: the fourth failure path, and the only one that does not go through
            // OutcomeClassifier — MutationInterface builds this event directly, so the classifier's
            // own tests cannot reach it. Without an arm here, "every failure carries a reason" would
            // be a claim measured on three of the four sites that produce one.
            Assert.NotNull(abandoned.Reason);
            Assert.Contains("crash recovery", abandoned.Reason, StringComparison.OrdinalIgnoreCase);

            // The orphaned attempt's own directory is untouched — the retry got its own.
            Assert.True(Directory.Exists(ArtifactManager.ResolveOutputDirectory(artifactsRoot, orphanExecutionId)));
        }
        finally
        {
            // Best-effort: on Linux the orphan genuinely outlives the killed host and would
            // otherwise leak a sleeping process past this test. On Windows the OS Job Object backing
            // aer-core's process-tree containment already took it down alongside the host process
            // (KILL_ON_JOB_CLOSE fires when the host's last handle to the job closes, which happens
            // automatically as part of the OS tearing down the killed host's own handle table) — so
            // there this is reliably already a no-op, not a real cleanup.
            if (orphanedChildPid > 0)
            {
                TryKillOrphanedChild(orphanedChildPid);
            }

            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static async Task<FlowState> RunRecoveryAsync(
        string roomDirectory, string artifactsRoot, string logPath, ScenarioWorker recoveryWorker)
    {
        var (snapshot, bindings) = Scenarios.Build(recoveryWorker);

        await using var writer = await OpenWriterWithRetryAsync(logPath);
        var reader = new FlowEventLogReader(logPath);
        var dispatcher = new CoreDispatcher(writer);

        // #891: retry the recovery pump's lock acquisition on WorkflowLockedException for a short
        // window — the same killed-process teardown gap OpenWriterWithRetryAsync (above) handles for the
        // log. On Windows a killed host's flow.lock handle is not always released by the instant
        // Process.WaitForExit returns, so the recovery pump can transiently lose the lock to a holder
        // that is already dead. The lock is fail-fast by design (a live double-pump must error, not
        // wait — see ConcurrencyGuard), so this tolerance lives in the test harness, which manufactures
        // the race by killing and recovering within milliseconds, not in the product, where a real
        // recovery never races a still-terminating host. StartWorkflowAsync acquires the lock before it
        // writes anything, so a failed attempt mutated nothing and retrying the whole pump is safe.
        // 60s per the #1004 standard for condition-wait ceilings the green path never spends: a
        // flapping-holder occurrence outlasted the old 5s window under full-suite load (#1008).
        var lockRetryDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var completed = await Task.WhenAny(
                MutationInterface.StartWorkflowAsync(
                    Scenarios.WorkflowId, roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, dispatcher),
                Task.Delay(TimeSpan.FromSeconds(60)));

            if (completed is not Task<FlowState> stateTask)
            {
                throw new TimeoutException("Recovery run did not reach a fixed point in time.");
            }

            try
            {
                return await stateTask;
            }
            catch (WorkflowLockedException) when (DateTime.UtcNow < lockRetryDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="logPath"/> for append, retrying on <see cref="FlowJournalHeldException"/>
    /// (#816) for a short window: on Windows, a killed process's file handles are not always
    /// released by the instant <see cref="Process.WaitForExit"/>/<see cref="Process.WaitForExitAsync"/>
    /// returns — a real, observed gap between "the process object reports exited" and "the OS has
    /// finished tearing down every handle it held" — so opening this same file for append
    /// immediately after killing its previous writer can transiently collide with that teardown.
    /// </summary>
    private static async Task<FlowEventLogWriter> OpenWriterWithRetryAsync(string logPath)
    {
        // 60s, matching the lock-retry deadline above and #1004's ceiling standard: the recorded
        // #1008 occurrence was a handle-teardown flap that outlasted the old short window while the
        // holder probe confirmed nothing was stuck (the handle released before the probe ran).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                return new FlowEventLogWriter(logPath);
            }
            catch (FlowJournalHeldException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
    }

    private static async Task<ExecutionId> GetAcceptedExecutionIdAsync(string logPath)
    {
        var events = await new FlowEventLogReader(logPath).ReadAllAsync();
        return events.OfType<FlowEvent.ExecutionRequestAccepted>().Single(e => e.Request.StepId == Scenarios.StepA).Request.ExecutionId;
    }

    private static async Task AssertResultFileExistsAsync(string artifactsRoot, ExecutionId executionId)
    {
        var path = Path.Combine(ArtifactManager.ResolveOutputDirectory(artifactsRoot, executionId), "result");
        Assert.True(File.Exists(path));
        Assert.Equal("done", (await File.ReadAllTextAsync(path)).Trim());
    }

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath, string PauseSignal, string CancelSignal) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"crash-task-{Guid.NewGuid():N}");
        return (
            roomDirectory,
            Path.Combine(roomDirectory, "artifacts"),
            Path.Combine(roomDirectory, "flow.jsonl"),
            Path.Combine(roomDirectory, "pause.signal"),
            Path.Combine(roomDirectory, "cancel.signal"));
    }
}
