using Baton.Domain;
using Baton.Mutation;
using Baton.Store;

namespace Baton.Tests.Mutation;

/// <summary>
/// M10 Phase 2: unit-level tests against <see cref="InFlightExecutionRegistry"/> directly (no
/// <see cref="Baton.Mutation.MutationInterface"/> pump involved), covering the race
/// <see cref="MutationInterfaceLiveCancellationTests"/> cannot deterministically force: an execution
/// this registry already snapshotted for cancellation can settle naturally — and be
/// <see cref="InFlightExecutionRegistry.Unregister"/>'d, disposing its
/// <see cref="CancellationTokenSource"/> — while the registry is still durably recording the intent
/// for it (or a sibling), before it ever gets to signal.
/// </summary>
public class InFlightExecutionRegistryTests
{
    private static readonly ExecutionId A = new("a");
    private static readonly ExecutionId B = new("b");

    [Fact]
    public async Task RequestStopAsync_tolerates_every_entry_settling_naturally_while_intent_is_still_being_recorded()
    {
        var registry = new InFlightExecutionRegistry();
        var writer = new GatedEventLogWriter();
        registry.Bind(writer);

        registry.Register(A);
        registry.Register(B);

        var stopTask = registry.RequestStopAsync(CancellationToken.None);
        await writer.FirstAppendStarted;

        // Simulates DispatchAndRecordOutcomeAsync's own Unregister firing for both executions —
        // e.g. both happened to complete naturally — exactly while RequestStopAsync is still
        // durably recording CancellationRequested and has not yet reached either Cancel() call.
        registry.Unregister(A);
        registry.Unregister(B);

        writer.ReleaseAll();

        // Must complete without throwing ObjectDisposedException from cancelling an already-disposed
        // CancellationTokenSource.
        await AwaitWithTimeoutAsync(stopTask);
    }

    [Fact]
    public async Task RequestCancellationAsync_tolerates_its_target_settling_naturally_while_intent_is_still_being_recorded()
    {
        var registry = new InFlightExecutionRegistry();
        var writer = new GatedEventLogWriter();
        registry.Bind(writer);

        registry.Register(A);

        var cancelTask = registry.RequestCancellationAsync(A, TestContext.Current.CancellationToken);
        await writer.FirstAppendStarted;

        // A settled naturally (e.g. succeeded) exactly while its own targeted cancellation request
        // is still being recorded.
        registry.Unregister(A);

        writer.ReleaseAll();

        await AwaitWithTimeoutAsync(cancelTask);
    }

    /// <summary>
    /// #513: <see cref="InFlightExecutionRegistry.RequestCancellationAsync"/>'s return value became
    /// load-bearing when <c>Baton.CrashTestHost.Program</c>'s signal watcher started retrying on
    /// <c>false</c> until it observes <c>true</c>, to tolerate racing <see cref="InFlightExecutionRegistry.Register"/>.
    /// Nothing asserted the boolean itself before this — every existing caller discards it. Both
    /// arms matter: a caller that retries on <c>false</c> needs <c>false</c> to actually mean "not
    /// yet, try again" for an unregistered target, and <c>true</c> to actually mean "delivered" once
    /// registered — not just "didn't throw".
    /// </summary>
    [Fact]
    public async Task RequestCancellationAsync_returns_false_before_registration_and_true_after()
    {
        var registry = new InFlightExecutionRegistry();
        var writer = new GatedEventLogWriter();
        registry.Bind(writer);
        writer.ReleaseAll();

        var beforeRegister = await registry.RequestCancellationAsync(A, TestContext.Current.CancellationToken);
        Assert.False(beforeRegister, "an unregistered target has nothing to cancel and nothing to signal");

        registry.Register(A);

        var afterRegister = await registry.RequestCancellationAsync(A, TestContext.Current.CancellationToken);
        Assert.True(afterRegister, "a registered target's cancellation was recorded and signalled");
    }

    /// <summary>
    /// #1549: <see cref="FlowEvent.CancellationDelivered"/> is distinct from
    /// <see cref="FlowEvent.CancellationRequested"/> — recorded only once the signal actually reaches
    /// a live token, not merely once an entry was found under the lock.
    /// </summary>
    [Fact]
    public async Task RequestCancellationAsync_appends_CancellationDelivered_after_a_live_signal()
    {
        var registry = new InFlightExecutionRegistry();
        var writer = new RecordingEventLogWriter();
        registry.Bind(writer);
        registry.Register(A);

        var delivered = await registry.RequestCancellationAsync(A, TestContext.Current.CancellationToken);

        Assert.True(delivered);
        Assert.Collection(
            writer.Appended,
            e => Assert.IsType<FlowEvent.CancellationRequested>(e),
            e => Assert.IsType<FlowEvent.CancellationDelivered>(e));
    }

    /// <summary>Plain, non-gating event recorder — for tests with no race to pin down.</summary>
    private sealed class RecordingEventLogWriter : IEventLogWriter
    {
        private readonly List<FlowEvent> _appended = [];

        public IReadOnlyList<FlowEvent> Appended => _appended;

        public Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
        {
            _appended.Add(flowEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// #1549: the same disposal race <see cref="RequestCancellationAsync_tolerates_its_target_settling_naturally_while_intent_is_still_being_recorded"/>
    /// already pins for "does not throw" also proves the NEW consequence — a target that settles
    /// naturally between the lock snapshot and <c>TryCancel</c> gets its intent durably recorded
    /// (<see cref="FlowEvent.CancellationRequested"/>) but was never actually delivered, so
    /// <see cref="FlowEvent.CancellationDelivered"/> must not follow it.
    /// </summary>
    [Fact]
    public async Task RequestCancellationAsync_records_intent_but_not_delivery_when_the_token_disposes_first()
    {
        var registry = new InFlightExecutionRegistry();
        var writer = new GatedRecordingEventLogWriter();
        registry.Bind(writer);
        registry.Register(A);

        var cancelTask = registry.RequestCancellationAsync(A, TestContext.Current.CancellationToken);
        await writer.FirstAppendStarted;

        registry.Unregister(A);
        writer.ReleaseAll();

        var delivered = await cancelTask;
        Assert.True(delivered, "intent was still recorded even though nothing was actually delivered");
        Assert.Collection(
            writer.Appended,
            e => Assert.IsType<FlowEvent.CancellationRequested>(e));
    }

    private static async Task AwaitWithTimeoutAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.Same(task, completed);
        await task;
    }

    /// <summary>An <see cref="IEventLogWriter"/> whose <see cref="AppendAsync"/> blocks until the test releases it, to pin down the race window deterministically.</summary>
    private sealed class GatedEventLogWriter : IEventLogWriter
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstAppendStarted => _started.Task;

        public void ReleaseAll() => _release.TrySetResult();

        public async Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The same first-append gate as <see cref="GatedEventLogWriter"/>, plus an ordered record of
    /// every event actually appended — what
    /// <see cref="RequestCancellationAsync_records_intent_but_not_delivery_when_the_token_disposes_first"/>
    /// needs to assert on the CancellationRequested/CancellationDelivered sequence, not just "did not
    /// throw." <see cref="_release"/> is a single latch shared by every call, so only the very first
    /// append (the one the test blocks on) actually waits — later ones (after <see cref="ReleaseAll"/>)
    /// see it already completed and return immediately, same as <see cref="GatedEventLogWriter"/>.
    /// </summary>
    private sealed class GatedRecordingEventLogWriter : IEventLogWriter
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<FlowEvent> _appended = [];

        public Task FirstAppendStarted => _started.Task;

        public IReadOnlyList<FlowEvent> Appended => _appended;

        public void ReleaseAll() => _release.TrySetResult();

        public async Task AppendAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            _appended.Add(flowEvent);
        }
    }
}
