using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;
using Aer.Flow.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// M8 Phase 3 (reactive concurrent dispatch): mutation-level tests against a
/// <see cref="StubCoreDispatcher"/> with <see cref="TaskCompletionSource{TResult}"/>-controlled
/// completion order, proving <see cref="MutationInterface.StartWorkflowAsync"/> dispatches every
/// ready step of a round concurrently and reacts to each completion independently — no real
/// processes, no timing-based assertions beyond a bounded "nothing else happened yet" check.
/// </summary>
public class MutationInterfaceConcurrencyTests
{
    private static readonly StepId A = new("a");
    private static readonly StepId B = new("b");
    private static readonly StepId C = new("c");
    private static readonly StepId D = new("d");
    private static readonly StepId F = new("f");

    private static readonly WorkerContract Contract = new("stub-worker", [], [], []);
    private static readonly CoreDispatchTarget Target = new("stub", []);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoFurtherDispatchWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// A ceiling, not a pace. No dispatch these facts wait for is behind a real clock (#1202): the
    /// steps that can fail carry zero backoff, the rest never fail so never reach one, and the fact
    /// that does test a deferral drives a <see cref="FakeTimeProvider"/>. So a wait that reaches
    /// this has stopped rather than slowed, and the ceiling only keeps a regression from hanging
    /// the suite.
    /// </summary>
    private static readonly TimeSpan DispatchCeiling = TimeSpan.FromSeconds(60);

    /// <summary>How many times the deferral fact advances the fake clock before calling it stuck.</summary>
    private const int AdvancePasses = 50;

    /// <summary>
    /// A backoff long enough that no real-clock accident could satisfy the deferral fact — it only
    /// ever completes because that fact advances a <see cref="FakeTimeProvider"/> past it.
    /// </summary>
    private static readonly BackoffPolicy DeferredBackoff =
        new(TimeSpan.FromMinutes(10), 1, TimeSpan.FromMinutes(10), JitterMode.None);

    private static readonly CoreDispatchResult Succeeded = new(0, CoreExitReason.Natural);
    private static readonly CoreDispatchResult Failed = new(1, CoreExitReason.Natural);

    [Fact]
    public async Task StartWorkflowAsync_dispatches_ready_steps_concurrently_and_reacts_per_completion()
    {
        // A -> B, C (fan-out); D depends only on B; F is a true join on both B and C.
        var snapshot = MakeSnapshot(
            Step(A, dependsOn: []),
            Step(B, dependsOn: [A]),
            Step(C, dependsOn: [A]),
            Step(D, dependsOn: [B]),
            Step(F, dependsOn: [B, C]));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var aResult = stub.EnqueueResult(A);
            var bResult = stub.EnqueueResult(B);
            var cResult = stub.EnqueueResult(C);
            var dResult = stub.EnqueueResult(D);
            var fResult = stub.EnqueueResult(F);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(A, await ReadNextDispatchAsync(stub));
            aResult.SetResult(Succeeded);

            // A's completion makes both B and C ready in the same round — both must dispatch before
            // either is allowed to finish, i.e. neither B's nor C's downstream may appear yet.
            var firstOfBC = await ReadNextDispatchAsync(stub);
            var secondOfBC = await ReadNextDispatchAsync(stub);
            Assert.Equal(new HashSet<StepId> { B, C }, new HashSet<StepId> { firstOfBC, secondOfBC });
            await AssertNoFurtherDispatchAsync(stub);

            // Completing B alone must dispatch D (depends only on B) without waiting for C — a slow
            // step must never delay unrelated ready work.
            bResult.SetResult(Succeeded);
            Assert.Equal(D, await ReadNextDispatchAsync(stub));
            await AssertNoFurtherDispatchAsync(stub);

            // F needs both B and C; only completing C now unblocks it.
            cResult.SetResult(Succeeded);
            Assert.Equal(F, await ReadNextDispatchAsync(stub));

            dResult.SetResult(Succeeded);
            fResult.SetResult(Succeeded);

            var finalState = await workflowTask;
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_retries_a_failed_step_while_an_unrelated_step_stays_in_flight()
    {
        // Two independent roots: Slow never completes until the test says so; Flaky fails once then
        // succeeds. Slow being in flight must never block Flaky's retry from dispatching.
        var slow = new StepId("slow");
        var flaky = new StepId("flaky");
        var snapshot = MakeSnapshot(
            Step(slow, dependsOn: [], maxAttempts: 1),
            // #1202: BackoffPolicy.None, not the default Steady. What this fact is about is the
            // SCHEDULER — that an unrelated in-flight step does not gate a retry — and backoff
            // pacing has its own tests. Under the default the retry sits behind a real 0.5-1.0s
            // wall-clock timer (Steady: Initial 1s, Jitter Half), which is the only clock this test
            // ever waited on, and a timer is exactly what a fully-parallel suite on a loaded runner
            // cannot promise to fire on time. It went red on ubuntu CI having waited the full 60s
            // ceiling for a dispatch that should arrive in under one, and passed on a plain re-run.
            // Zero backoff removes the wait entirely: the retry now dispatches on re-projection.
            Step(flaky, dependsOn: [], maxAttempts: 2, backoff: BackoffPolicy.None));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var slowResult = stub.EnqueueResult(slow); // left pending deliberately
            var flakyAttempt1 = stub.EnqueueResult(flaky); // fails
            var flakyAttempt2 = stub.EnqueueResult(flaky); // succeeds

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            var firstRound = new[] { await ReadNextDispatchAsync(stub), await ReadNextDispatchAsync(stub) };
            Assert.Equal(new HashSet<StepId> { slow, flaky }, new HashSet<StepId>(firstRound));

            flakyAttempt1.SetResult(Failed);
            Assert.Equal(flaky, await ReadNextDispatchAsync(stub));
            await AssertNoFurtherDispatchAsync(stub);

            flakyAttempt2.SetResult(Succeeded);
            await AssertNoFurtherDispatchAsync(stub);

            slowResult.SetResult(Succeeded);

            var finalState = await workflowTask;
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
            Assert.Equal(0, finalState.Steps.Single(s => s.StepId == flaky).ConsecutiveFailureCount);

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var flakyAttempts = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Where(e => e.Request.StepId == flaky)
                .Select(e => e.Request.ExecutionId)
                .ToList();
            Assert.Equal(2, flakyAttempts.Count);
            Assert.Equal(flakyAttempts.Distinct().Count(), flakyAttempts.Count);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The half #1202 would otherwise have deleted: a retry that IS deferred must still wake while an
    /// unrelated step is mid-flight. The pump's deferral wakeup only exists on that path — the idle
    /// branch handles the nothing-in-flight case — so without this fact, taking the wall clock out of
    /// the fact above left the behaviour with no coverage anywhere in the suite (found by the #1202
    /// second reader, which searched for another exerciser and found none).
    /// </summary>
    /// <remarks>
    /// The deadline is reached by advancing a <see cref="FakeTimeProvider"/>, not by waiting: the pump
    /// takes its delay from the same provider, so the wake is caused rather than awaited. The advance
    /// runs in a loop because the pump has to have parked on the timer before an advance can fire it,
    /// and the point at which it parks is not observable from here — each pass is instant, so the loop
    /// cannot stall the way the real clock it replaces could.
    /// </remarks>
    [Fact]
    public async Task StartWorkflowAsync_wakes_a_deferred_retry_while_an_unrelated_step_stays_in_flight()
    {
        var slow = new StepId("slow");
        var flaky = new StepId("flaky");
        var snapshot = MakeSnapshot(
            Step(slow, dependsOn: [], maxAttempts: 1),
            Step(flaky, dependsOn: [], maxAttempts: 2, backoff: DeferredBackoff));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var slowResult = stub.EnqueueResult(slow); // left pending for the whole fact, deliberately
            var flakyAttempt1 = stub.EnqueueResult(flaky);
            var flakyAttempt2 = stub.EnqueueResult(flaky);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, MakeBindings(), artifactsRoot, reader, writer, stub,
                cancellationToken: TestContext.Current.CancellationToken,
                timeProvider: fakeTime,
                // No jitter: the deadline this fact advances past has to be the one it can name.
                jitterSource: () => 1.0);

            var firstRound = new[] { await ReadNextDispatchAsync(stub), await ReadNextDispatchAsync(stub) };
            Assert.Equal(new HashSet<StepId> { slow, flaky }, new HashSet<StepId>(firstRound));

            flakyAttempt1.SetResult(Failed);

            // Polarity, and the reason this fact discriminates: before the deadline the retry must NOT
            // dispatch. Without it, a pump that ignored the deferral entirely would pass the wake
            // assertion below purely by being early.
            await AssertNoFurtherDispatchAsync(stub);

            Assert.Equal(flaky, await AdvanceUntilDispatchAsync(stub, fakeTime));

            flakyAttempt2.SetResult(Succeeded);
            slowResult.SetResult(Succeeded);

            var finalState = await workflowTask;
            Assert.All(finalState.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task StartWorkflowAsync_emits_a_rounds_accepted_events_in_snapshot_declaration_order()
    {
        // Three independent roots declared out of alphabetical/natural order, to distinguish
        // "declaration order" from any other incidental ordering the ready set might produce.
        var third = new StepId("third");
        var first = new StepId("first");
        var second = new StepId("second");
        var snapshot = MakeSnapshot(
            Step(third, dependsOn: []),
            Step(first, dependsOn: []),
            Step(second, dependsOn: []));

        var (roomDirectory, artifactsRoot, logPath) = MakeTaskPaths();
        try
        {
            var stub = new StubCoreDispatcher();
            var thirdResult = stub.EnqueueResult(third);
            var firstResult = stub.EnqueueResult(first);
            var secondResult = stub.EnqueueResult(second);

            await using var writer = new FlowEventLogWriter(logPath);
            var reader = new FlowEventLogReader(logPath);
            var bindings = MakeBindings();

            var workflowTask = MutationInterface.StartWorkflowAsync(
                new WorkflowId("wf"), roomDirectory, snapshot, bindings, artifactsRoot, reader, writer, stub, cancellationToken: TestContext.Current.CancellationToken);

            await ReadNextDispatchAsync(stub);
            await ReadNextDispatchAsync(stub);
            await ReadNextDispatchAsync(stub);
            thirdResult.SetResult(Succeeded);
            firstResult.SetResult(Succeeded);
            secondResult.SetResult(Succeeded);

            await workflowTask;

            var events = await reader.ReadAllAsync(TestContext.Current.CancellationToken);
            var acceptedOrder = events
                .OfType<FlowEvent.ExecutionRequestAccepted>()
                .Select(e => e.Request.StepId)
                .ToList();

            Assert.Equal([third, first, second], acceptedOrder);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    private static WorkflowStepDefinition Step(StepId stepId, IReadOnlyList<StepId> dependsOn, int maxAttempts = 1, BackoffPolicy? backoff = null) =>
        new(stepId, "stub-worker", [], [], dependsOn, backoff is null ? new RetryPolicy(maxAttempts) : new RetryPolicy(maxAttempts, backoff));

    private static WorkflowDefinitionSnapshot MakeSnapshot(params WorkflowStepDefinition[] steps) => new(
        new WorkflowDefinitionSnapshotId($"snapshot-{Guid.NewGuid():N}"),
        new WorkflowTemplateId("concurrency-test"),
        WorkflowTemplateVersion: 1,
        Steps: steps);

    private static Dictionary<string, WorkerBinding> MakeBindings() =>
        new() { ["stub-worker"] = new WorkerBinding.Process(Contract, Target, Timeout) };

    private static (string RoomDirectory, string ArtifactsRoot, string LogPath) MakeTaskPaths()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        return (roomDirectory, Path.Combine(roomDirectory, "artifacts"), Path.Combine(roomDirectory, "flow.jsonl"));
    }

    private static async Task<StepId> ReadNextDispatchAsync(StubCoreDispatcher stub)
    {
        var readTask = stub.DispatchStarted.ReadAsync().AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(DispatchCeiling));

        // Not Assert.Same: its message ("Values are not the same instance", two Task ToStrings) says
        // nothing about what went wrong, which is what made #1202 cost a re-run to learn nothing
        // from. Nothing in these facts waits on a clock except a retry backoff, so reaching this
        // ceiling means the pump stopped scheduling, not that it was slow — say that.
        if (completed != readTask)
        {
            Assert.Fail(
                $"No dispatch began within {DispatchCeiling.TotalSeconds:0}s. No wait in these facts is behind "
                + "a real clock (see DispatchCeiling), so this is a pump that stopped scheduling rather than "
                + "one running late.");
        }

        return await readTask;
    }

    /// <summary>
    /// Advances <paramref name="fakeTime"/> until a dispatch begins, or the ceiling is reached.
    /// The loop exists because the pump must already be parked on its deferral timer for an advance
    /// to fire it, and nothing here can observe the moment it parks; every pass is instant, so this
    /// bounds on iterations rather than on elapsed real time.
    /// </summary>
    private static async Task<StepId> AdvanceUntilDispatchAsync(StubCoreDispatcher stub, FakeTimeProvider fakeTime)
    {
        var readTask = stub.DispatchStarted.ReadAsync().AsTask();

        for (var pass = 0; pass < AdvancePasses; pass++)
        {
            fakeTime.Advance(TimeSpan.FromMinutes(1));

            // Yields to the pump between advances rather than pacing it: a completed task, so this
            // is a scheduler turn and not a wait. wait-ok: no duration is involved.
            await Task.Yield();

            if (readTask.IsCompleted)
            {
                return await readTask;
            }
        }

        var settled = await Task.WhenAny(readTask, Task.Delay(DispatchCeiling));
        if (settled != readTask)
        {
            Assert.Fail(
                $"The deferred retry never dispatched after {AdvancePasses} advances of the fake clock. "
                + "Its deadline is minutes out and the clock moved well past it, so the pump is not waking "
                + "on the deferral at all.");
        }

        return await readTask;
    }

    private static async Task AssertNoFurtherDispatchAsync(StubCoreDispatcher stub)
    {
        var waitTask = stub.DispatchStarted.WaitToReadAsync().AsTask();
        var completed = await Task.WhenAny(waitTask, Task.Delay(NoFurtherDispatchWindow));
        Assert.NotSame(waitTask, completed);
    }
}




