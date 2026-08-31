using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// Tripwires for the #1496 freeze: <see cref="BatonEnvironmentSnapshot"/> replaced BatonPaths'
/// "resolve, never capture" discipline with "capture once, never re-read". The behaviour guard and
/// the two AsyncLocal-flow tests are the new contract, documented as tests rather than prose so a
/// future reader hits them instead of rediscovering the boundary in production. The two flow tests
/// also record a finding: the task that specified this design assumed a raw <c>new Thread(...)</c>
/// would not see an active scope, which does not hold on this runtime — see the control test
/// (asserting the flow IS observed) before the actual footgun test (asserting it is not, across
/// <see cref="System.Threading.ExecutionContext.SuppressFlow"/> instead).
/// </summary>
[Collection(SerializedEnvironmentCollection.Name)]
public class BatonEnvironmentSnapshotTests
{
    /// <summary>
    /// The behaviour guard #1496 asks for: once <see cref="BatonEnvironmentSnapshot.Current"/> has
    /// captured the process snapshot, a later <c>Environment.SetEnvironmentVariable</c> for
    /// <see cref="BatonPaths.HomeEnvironmentVariable"/> is never observed by
    /// <see cref="BatonPaths.Root"/> — the opposite of the pre-#1496 contract, where <c>Root</c> was
    /// documented to re-resolve the environment on every access specifically so a mid-process mutation
    /// would be honoured immediately.
    /// </summary>
    [Fact]
    public void Root_does_not_observe_a_BATON_HOME_mutation_made_after_the_process_snapshot_is_captured()
    {
        // Forces the process snapshot to exist before the mutation below, so the mutation is
        // guaranteed to land after first access rather than incidentally racing it.
        var beforeMutation = BatonPaths.Root;

        var prior = Environment.GetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable);
        try
        {
            var neverObserved = Path.Combine(Path.GetTempPath(), $"baton-unobserved-home-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable, neverObserved);

            Assert.Equal(beforeMutation, BatonPaths.Root);
            Assert.NotEqual(neverObserved, BatonPaths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable, prior);
        }
    }

    /// <summary>
    /// A manually-created <see cref="Thread"/> DOES see an active <see cref="BatonEnvironmentSnapshot.BeginScope"/>
    /// override — <c>Thread.Start</c> captures and flows the calling thread's
    /// <see cref="System.Threading.ExecutionContext"/> the same way <c>Task.Run</c> does. This is a
    /// #1496 finding, not the shape the task asked this tripwire to prove: the ask was to document
    /// that a raw <c>new Thread(...)</c> does NOT see the override, on the belief that only
    /// async/<c>Task.Run</c> flow <c>AsyncLocal</c>. That belief does not hold on this runtime — see
    /// the assertion below, which is the control proving the flow is real before the actual footgun
    /// test.
    /// </summary>
    [Fact]
    public void AmbientOverride_DOES_flow_into_a_manually_created_Thread_contrary_to_the_1496_task_premise()
    {
        var scoped = BatonEnvironmentSnapshot.Blank with { HomeOverride = "scoped-value-flows-here" };

        BatonEnvironmentSnapshot? observedOnThread = null;
        using (BatonEnvironmentSnapshot.BeginScope(scoped))
        {
            var thread = new Thread(() => observedOnThread = BatonEnvironmentSnapshot.Current);
            thread.Start();
            thread.Join();
        }

        Assert.NotNull(observedOnThread);
        Assert.Equal(scoped.HomeOverride, observedOnThread!.HomeOverride);
    }

    /// <summary>
    /// The real non-flow boundary: <see cref="System.Threading.ExecutionContext.SuppressFlow"/> is the
    /// documented, deliberate way to stop <see cref="AsyncLocal{T}"/> — including a
    /// <see cref="BatonEnvironmentSnapshot.BeginScope"/> override — from reaching code started from
    /// the suppressed region, whether that's a raw <see cref="Thread"/>, <c>Task.Run</c>, or a
    /// thread-pool work item. Nothing in <c>src/</c> calls <c>SuppressFlow</c> today, but any future
    /// code that does (e.g. for the flow-capture allocation cost on a hot path) would silently start
    /// seeing the process snapshot instead of an active test scope or an active production override —
    /// this is where that surprise is supposed to surface first.
    /// </summary>
    [Fact]
    public void AmbientOverride_does_not_flow_across_an_ExecutionContext_SuppressFlow_boundary()
    {
        var scoped = BatonEnvironmentSnapshot.Blank with { HomeOverride = "scoped-value-suppressed" };

        BatonEnvironmentSnapshot? observedInSuppressedRegion = null;
        using (BatonEnvironmentSnapshot.BeginScope(scoped))
        {
            var flowControl = System.Threading.ExecutionContext.SuppressFlow();
            try
            {
                var thread = new Thread(() => observedInSuppressedRegion = BatonEnvironmentSnapshot.Current);
                thread.Start();
                thread.Join();
            }
            finally
            {
                flowControl.Undo();
            }
        }

        Assert.NotNull(observedInSuppressedRegion);
        Assert.NotEqual(scoped.HomeOverride, observedInSuppressedRegion!.HomeOverride);
    }
}
