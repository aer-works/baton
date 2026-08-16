using Aer.Flow.Concurrency;

namespace Aer.Flow.Tests.Concurrency;

/// <summary>
/// #1296: see <see cref="ConcurrencySlotGate"/>'s own doc comment for what this caps and why. Its
/// counters are static/process-wide, so every test resets them first -- xunit runs this class's
/// methods sequentially by default (one collection per class), which is what makes that safe.
/// </summary>
public class ConcurrencySlotGateTests
{
    public ConcurrencySlotGateTests() => ConcurrencySlotGate.ResetForTests();

    [Fact]
    public async Task AcquireAsync_below_the_global_cap_completes_immediately()
    {
        using var slot = await ConcurrencySlotGate.AcquireAsync("/rooms/a", "claude");

        Assert.False(ConcurrencySlotGate.IsWaiting("/rooms/a"));
    }

    [Fact]
    public async Task AcquireAsync_beyond_the_global_cap_queues_until_a_slot_releases()
    {
        var slots = new List<IDisposable>();
        for (var i = 0; i < ConcurrencySlotGate.GlobalCap; i++)
        {
            // Distinct vendors so this exercises the GLOBAL cap specifically, not the per-vendor one.
            slots.Add(await ConcurrencySlotGate.AcquireAsync($"/rooms/{i}", $"vendor-{i}"));
        }

        var acquireTask = ConcurrencySlotGate.AcquireAsync("/rooms/queued", "vendor-extra");
        await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: settle time for an in-process async continuation, not an external wait

        Assert.False(acquireTask.IsCompleted);
        Assert.True(ConcurrencySlotGate.IsWaiting("/rooms/queued"));

        slots[0].Dispose();
        var dispatched = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // wait-ok: hang backstop, not an expected-duration wait -- the awaited task resolves near-instantly once the slot frees

        Assert.False(ConcurrencySlotGate.IsWaiting("/rooms/queued"));
        dispatched.Dispose();
        foreach (var slot in slots.Skip(1))
        {
            slot.Dispose();
        }
    }

    [Fact]
    public async Task AcquireAsync_beyond_the_per_vendor_cap_queues_even_with_global_room_free()
    {
        using var first = await ConcurrencySlotGate.AcquireAsync("/rooms/a", "claude");
        using var second = await ConcurrencySlotGate.AcquireAsync("/rooms/b", "claude");

        // Global cap is 3; both active slots are "claude", whose own per-vendor cap is 2 -- a third
        // claude turn must queue despite a free global slot.
        var acquireTask = ConcurrencySlotGate.AcquireAsync("/rooms/c", "claude");
        await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: settle time for an in-process async continuation, not an external wait

        Assert.False(acquireTask.IsCompleted);
        Assert.True(ConcurrencySlotGate.IsWaiting("/rooms/c"));

        first.Dispose();
        var dispatched = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // wait-ok: hang backstop, not an expected-duration wait -- the awaited task resolves near-instantly once the slot frees
        dispatched.Dispose();
    }

    [Fact]
    public async Task A_later_waiter_of_a_different_vendor_is_not_head_of_line_blocked()
    {
        // Saturate claude's own per-vendor cap (2) while a global slot remains free (cap 3).
        using var first = await ConcurrencySlotGate.AcquireAsync("/rooms/a", "claude");
        using var second = await ConcurrencySlotGate.AcquireAsync("/rooms/b", "claude");
        var claudeQueued = ConcurrencySlotGate.AcquireAsync("/rooms/c", "claude");
        await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: settle time for an in-process async continuation, not an external wait
        Assert.True(ConcurrencySlotGate.IsWaiting("/rooms/c"));

        // gemini is not vendor-capped and the global cap still has one free slot -- it must not wait
        // behind claude's queued (and still blocked) waiter.
        var geminiSlot = await ConcurrencySlotGate.AcquireAsync("/rooms/d", "gemini")
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // wait-ok: hang backstop, not an expected-duration wait -- the awaited task resolves near-instantly once the slot frees

        Assert.False(ConcurrencySlotGate.IsWaiting("/rooms/d"));
        Assert.True(ConcurrencySlotGate.IsWaiting("/rooms/c"));

        geminiSlot.Dispose();
        first.Dispose();
        (await claudeQueued).Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task CancelWaiting_removes_a_still_queued_room_and_never_dispatches_it()
    {
        var slots = new List<IDisposable>();
        for (var i = 0; i < ConcurrencySlotGate.GlobalCap; i++)
        {
            slots.Add(await ConcurrencySlotGate.AcquireAsync($"/rooms/{i}", $"vendor-{i}"));
        }

        var acquireTask = ConcurrencySlotGate.AcquireAsync("/rooms/queued", "vendor-extra");
        await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: settle time for an in-process async continuation, not an external wait
        Assert.True(ConcurrencySlotGate.IsWaiting("/rooms/queued"));

        var wasQueued = ConcurrencySlotGate.CancelWaiting("/rooms/queued");

        Assert.True(wasQueued);
        Assert.False(ConcurrencySlotGate.IsWaiting("/rooms/queued"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => acquireTask);

        // A slot freeing afterward must not resurrect the cancelled waiter.
        slots[0].Dispose();
        await Task.Delay(50, TestContext.Current.CancellationToken); // wait-ok: settle time for an in-process async continuation, not an external wait
        Assert.False(ConcurrencySlotGate.IsWaiting("/rooms/queued"));

        foreach (var slot in slots.Skip(1))
        {
            slot.Dispose();
        }
    }

    [Fact]
    public void CancelWaiting_on_a_room_that_was_never_queued_is_a_no_op()
    {
        Assert.False(ConcurrencySlotGate.CancelWaiting("/rooms/never-queued"));
    }
}
