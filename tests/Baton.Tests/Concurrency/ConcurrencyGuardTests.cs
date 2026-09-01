using System.Diagnostics;
using System.Text.Json;
using Baton.Tests.TestSupport;
using Baton.Concurrency;

namespace Baton.Tests.Concurrency;

public class ConcurrencyGuardTests
{
    [Fact]
    public void Acquire_creates_the_room_directory_if_it_does_not_exist()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            Assert.False(Directory.Exists(roomDirectory));

            using var guard = ConcurrencyGuard.Acquire(roomDirectory);

            Assert.True(Directory.Exists(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Acquire_throws_WorkflowLockedException_when_another_holder_already_has_the_lock()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var firstHolder = ConcurrencyGuard.Acquire(roomDirectory);

            Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Dispose_releases_the_lock_so_a_subsequent_Acquire_succeeds()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var firstHolder = ConcurrencyGuard.Acquire(roomDirectory);
            firstHolder.Dispose();

            using var secondHolder = ConcurrencyGuard.Acquire(roomDirectory);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Dispose_leaves_the_lock_file_on_disk_because_only_the_OS_held_lock_carries_meaning_not_the_files_existence()
    {
        // Proves the guard is not a sentinel-file mechanism: the lock file's mere existence
        // must never be read as "still locked" — only the live FileShare.None hold does that.
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var holder = ConcurrencyGuard.Acquire(roomDirectory);
            var lockFilePath = Path.Combine(roomDirectory, "flow.lock");
            Assert.True(File.Exists(lockFilePath));

            holder.Dispose();

            Assert.True(File.Exists(lockFilePath));
            using var secondHolder = ConcurrencyGuard.Acquire(roomDirectory);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #857: the behaviour the whole issue turns on — a holder that lets go quickly is waited out
    /// rather than refused.
    /// <para>
    /// Neither side runs on the thread pool. The release is a dedicated thread whose
    /// <c>Thread.Sleep</c> wakes on time under any pool pressure, and <c>AcquireWithin</c>'s own
    /// retry uses <c>Thread.Sleep</c> for the same reason. A contention test scheduled on a pool is
    /// a test that stops discriminating exactly when the machine is busy, which is the condition it
    /// exists for.
    /// </para>
    /// </summary>
    [Fact]
    public void AcquireWithin_waits_out_a_holder_that_releases_inside_the_budget()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        var hold = TimeSpan.FromMilliseconds(250);
        try
        {
            var holder = ConcurrencyGuard.Acquire(roomDirectory);
            var release = new Thread(() =>
            {
                Thread.Sleep(hold);
                holder.Dispose();
            })
            {
                IsBackground = true,
                Name = "baton-857-release",
            };

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            release.Start();

            using (ConcurrencyGuard.AcquireWithin(roomDirectory, TimeSpan.FromSeconds(5))) // wait-ok: test timing bounds lock acquisition
            {
                elapsed.Stop();
            }

            release.Join(TimeSpan.FromSeconds(10));

            Assert.True(
                elapsed.Elapsed >= hold,
                $"Acquired in {elapsed.ElapsedMilliseconds}ms, inside the {hold.TotalMilliseconds}ms hold -- " +
                "the lock was never actually contended, so this proves nothing.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Other polarity: the wait is BOUNDED. A holder that never lets go must still surface as a
    /// failure rather than being waited on forever — a stuck holder is a real problem and hiding it
    /// would be the opposite of the fix.
    /// </summary>
    [Fact]
    public void AcquireWithin_still_throws_when_the_holder_outlasts_the_budget()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(roomDirectory);

            var exception = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.AcquireWithin(roomDirectory, TimeSpan.FromMilliseconds(100))); // wait-ok: test timing bounds timeout

            Assert.Contains("not a routine overlap", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// The third polarity, and the one that guards the blast radius: <see cref="ConcurrencyGuard.Acquire"/>
    /// stays FAIL-FAST. #857 adds waiting for the operator-facing path only; an <c>baton run</c> pump
    /// that loses this lock means another pump owns the task, and waiting for it is exactly wrong.
    /// <para>
    /// Relative form (#1008): the refusal is measured against a deliberate <see cref="ConcurrencyGuard.AcquireWithin"/>
    /// wait run in the same test on the same loaded machine, not against a fixed wall-clock budget —
    /// the old 500ms budget produced two recorded false reds (531ms, 665ms) that were pure
    /// suite-load scheduling delay. The control arm asserts the waited acquire actually waited its
    /// full budget, so a clock or harness fault cannot fake the comparison; the margin assertion is
    /// what a regressed <c>Acquire</c> that adopted the wait cannot satisfy, because both arms would
    /// then take roughly the budget and the gap between them collapses toward zero (red-proven by
    /// exactly that substitution before this landed).
    /// </para>
    /// <para>
    /// Deliberate trade-off (named by this change's reviewer): an <c>Acquire</c> that regressed to
    /// blocking for under ~half the budget passes — the margin only catches adoption of the wait,
    /// which is the #857 claim under guard, not a sub-second latency contract. The old fixed 500ms
    /// budget nominally caught smaller slowdowns but false-redded on 31–165ms of ordinary suite
    /// load; this form trades that phantom sensitivity for a real one.
    /// </para>
    /// </summary>
    [Fact]
    public void Acquire_remains_fail_fast_and_does_not_wait()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(roomDirectory);

            // wait-ok: the measured-against clock, not a condition-wait ceiling (#1008, doc comment)
            var waitBudget = TimeSpan.FromSeconds(2);

            var fast = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(roomDirectory));
            fast.Stop();

            var waited = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.AcquireWithin(roomDirectory, waitBudget));
            waited.Stop();

            Assert.True(
                waited.Elapsed >= waitBudget,
                $"AcquireWithin returned in {waited.ElapsedMilliseconds}ms, under its {waitBudget.TotalMilliseconds}ms budget -- the control arm did not discriminate, so the comparison below proves nothing.");
            Assert.True(
                fast.Elapsed <= waited.Elapsed - (waitBudget / 2),
                $"Acquire took {fast.ElapsedMilliseconds}ms against AcquireWithin's deliberate {waited.ElapsedMilliseconds}ms -- refusing is meant to be immediate, not to adopt the wait.");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #857's second half. The reasoning lives on <c>ConcurrencyGuard.BuildLockedMessage</c>; what
    /// is pinned here is that the old single-cause wording cannot come back, in either direction —
    /// the discarded claim is absent and the missing one is present.
    /// </summary>
    [Fact]
    public void The_locked_message_does_not_blame_a_pump_as_the_single_likely_cause()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(roomDirectory);

            var exception = Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(roomDirectory));

            Assert.DoesNotContain("most likely a live 'baton run' pump", exception.Message, StringComparison.Ordinal);
            // Deliberately not "room sweep" -- BuildLockedMessage's own summary owns why. What is
            // pinned here is that the second shape a holder can take is still named at all.
            Assert.Contains("background component", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void The_cancel_falls_through_message_appears_only_for_flow_lock_contention()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            // Flow lock contention carries the cancel fall-through sentence:
            using (ConcurrencyGuard.Acquire(roomDirectory))
            {
                var flowException = Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(roomDirectory));
                Assert.Contains("cancel.request", flowException.Message, StringComparison.Ordinal);
                Assert.Contains("falls through", flowException.Message, StringComparison.Ordinal);
            }

            // Room-events lock contention omits the cancel fall-through sentence:
            using (ConcurrencyGuard.AcquireRoomEvents(roomDirectory))
            {
                var roomEventsException = Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.AcquireRoomEvents(roomDirectory));
                Assert.DoesNotContain("cancel.request", roomEventsException.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("falls through", roomEventsException.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Acquire_writes_holder_sidecar_file_with_caller_description()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(roomDirectory, "Test Runner (pid 999)");
            var sidecarPath = Path.Combine(roomDirectory, "flow.lock.holder");

            Assert.True(File.Exists(sidecarPath));
            var content = File.ReadAllText(sidecarPath);
            Assert.Contains("Test Runner (pid 999)", content);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Second_Acquire_exception_carries_first_holder_description_and_acquired_at()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(roomDirectory, "Custom Holder (pid 123)");

            var exception = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.Acquire(roomDirectory, "Second Holder"));

            Assert.Equal("Custom Holder (pid 123)", exception.HolderDescription);
            Assert.NotNull(exception.AcquiredAtUtc);
            Assert.Contains("Currently held by: Custom Holder (pid 123) since", exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Missing_sidecar_polarity_leaves_HolderDescription_null_and_retains_two_shapes_message()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var holder = ConcurrencyGuard.Acquire(roomDirectory);
            var sidecarPath = Path.Combine(roomDirectory, "flow.lock.holder");
            FileCleanup.EnsureDeleted(sidecarPath);

            var exception = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.Acquire(roomDirectory));

            Assert.Null(exception.HolderDescription);
            Assert.Null(exception.AcquiredAtUtc);
            Assert.DoesNotContain("Currently held by:", exception.Message);
            Assert.Contains("background component", exception.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Dispose_removes_holder_sidecar_file()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            var holder = ConcurrencyGuard.Acquire(roomDirectory, "Temp Holder");
            var sidecarPath = Path.Combine(roomDirectory, "flow.lock.holder");
            Assert.True(File.Exists(sidecarPath));

            holder.Dispose();

            Assert.False(File.Exists(sidecarPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Why the probe is confined to <see cref="ConcurrencyGuard.AcquireWithin"/>'s
    /// exhausted-budget path lives on that catch's own comment. What is pinned here is the
    /// polarity pair: the fail-fast refusal never carries probe text (that would erode
    /// <see cref="Acquire_remains_fail_fast_and_does_not_wait"/>'s margin), the waited one does.
    /// </summary>
    [Fact]
    public void Only_the_exhausted_wait_enriches_the_locked_message_with_the_probed_holder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare contention is OS-enforced only on Windows");
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var lockPath = Path.Combine(roomDirectory, "flow.lock");
            using var lockHolder = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            var fastEx = Assert.Throws<WorkflowLockedException>(() => ConcurrencyGuard.Acquire(roomDirectory));
            Assert.DoesNotContain("Current holder:", fastEx.Message);

            var waitedEx = Assert.Throws<WorkflowLockedException>(
                () => ConcurrencyGuard.AcquireWithin(roomDirectory, TimeSpan.FromMilliseconds(50))); // wait-ok: test timing bounds timeout

            Assert.Contains("Current holder:", waitedEx.Message);
            Assert.Contains($"(pid {Environment.ProcessId})", waitedEx.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Non_sharing_failure_on_guard_acquire_does_not_carry_holder_text()
    {
        var rootFile = Path.Combine(Path.GetTempPath(), $"task-guard-control-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(rootFile, []);
            var invalidRoomDirectory = Path.Combine(rootFile, "subfolder");

            // ThrowsAny: the exact subtype is the OS's choice (Windows throws plain IOException
            // here, Linux DirectoryNotFoundException); the claim is only that a non-sharing
            // failure stays a raw IOException-family throw with no probe text.
            var ex = Assert.ThrowsAny<IOException>(() => ConcurrencyGuard.Acquire(invalidRoomDirectory));

            Assert.DoesNotContain("Current holder:", ex.Message);
        }
        finally
        {
            FileCleanup.Delete(rootFile);
        }
    }

    [Fact]
    public void Flow_and_room_events_locks_are_independent_in_both_directions()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            // Direction 1: Flow held -> room-events acquirable
            using (var flowGuard = ConcurrencyGuard.Acquire(roomDirectory, "Flow holder"))
            {
                Assert.True(ConcurrencyGuard.IsHeld(roomDirectory));
                Assert.False(ConcurrencyGuard.IsRoomEventsHeld(roomDirectory));

                using var roomEventsGuard = ConcurrencyGuard.AcquireRoomEvents(roomDirectory, "RoomEvents holder");
                Assert.True(ConcurrencyGuard.IsRoomEventsHeld(roomDirectory));
            }

            // Direction 2: Room-events held -> flow acquirable
            using (var roomEventsGuard = ConcurrencyGuard.AcquireRoomEvents(roomDirectory, "RoomEvents holder"))
            {
                Assert.True(ConcurrencyGuard.IsRoomEventsHeld(roomDirectory));
                Assert.False(ConcurrencyGuard.IsHeld(roomDirectory));

                using var flowGuard = ConcurrencyGuard.Acquire(roomDirectory, "Flow holder");
                Assert.True(ConcurrencyGuard.IsHeld(roomDirectory));
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public void Room_events_holder_file_names_do_not_collide_with_flow_holder_files()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var flowGuard = ConcurrencyGuard.Acquire(roomDirectory, "Flow Holder 123");
            using var roomEventsGuard = ConcurrencyGuard.AcquireRoomEvents(roomDirectory, "RoomEvents Holder 456");

            var flowSidecarPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            var roomEventsSidecarPath = Path.Combine(roomDirectory, ConcurrencyGuard.RoomEventsHolderFileName);

            Assert.True(File.Exists(flowSidecarPath));
            Assert.True(File.Exists(roomEventsSidecarPath));

            var (flowHolder, _, _, _) = ConcurrencyGuard.ReadHolderInfo(roomDirectory);
            var (roomEventsHolder, _) = ConcurrencyGuard.ReadRoomEventsHolderInfo(roomDirectory);

            Assert.Equal("Flow Holder 123", flowHolder);
            Assert.Equal("RoomEvents Holder 456", roomEventsHolder);

            roomEventsGuard.Dispose();
            Assert.False(File.Exists(roomEventsSidecarPath));
            Assert.True(File.Exists(flowSidecarPath));

            flowGuard.Dispose();
            Assert.False(File.Exists(flowSidecarPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// #1604 F1: the field <see cref="Baton.Outcomes.EngineLivenessProbe.Probe"/> actually needs as
    /// its pid-recycling discriminator is the holder's PROCESS start time, not
    /// <c>AcquiredAtUtc</c> (when the lock itself was won — always at least a little later than the
    /// holding process's own start, which is what made the Alive arm unreachable before this fix).
    /// This process is its own holder here, so the two timestamps should read within a whisker of
    /// each other, not the seconds-to-minutes gap <c>AcquiredAtUtc</c> would show against a
    /// long-lived holder.
    /// </summary>
    [Fact]
    public void Acquire_writes_the_holders_own_process_start_time_into_the_sidecar()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var expectedStartTimeUtc = currentProcess.StartTime.ToUniversalTime();

            using var guard = ConcurrencyGuard.Acquire(roomDirectory);

            var (_, _, _, processStartTimeUtc) = ConcurrencyGuard.ReadHolderInfo(roomDirectory);

            Assert.NotNull(processStartTimeUtc);
            Assert.True(
                Math.Abs((processStartTimeUtc!.Value - expectedStartTimeUtc).TotalMilliseconds) < 1000,
                $"expected {processStartTimeUtc} to be within 1s of the process's own start time {expectedStartTimeUtc}");
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    /// <summary>
    /// Backward compat, named explicitly by #1604 F1: a sidecar written before this fix has no
    /// <c>ProcessStartTimeUtc</c> property at all. A reader must get null back for it (Unknown to a
    /// liveness probe), not throw and not silently substitute another field.
    /// </summary>
    [Fact]
    public void ReadHolderInfo_tolerates_a_pre_1604_sidecar_with_no_process_start_time_field()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"task-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var sidecarPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            var preFixShapeJson = JsonSerializer.Serialize(new
            {
                HolderDescription = "an old holder",
                Pid = 4242,
                AcquiredAtUtc = DateTime.UtcNow,
            });
            File.WriteAllText(sidecarPath, preFixShapeJson);

            var (holderDescription, pid, acquiredAtUtc, processStartTimeUtc) = ConcurrencyGuard.ReadHolderInfo(roomDirectory);

            Assert.Equal("an old holder", holderDescription);
            Assert.Equal(4242, pid);
            Assert.NotNull(acquiredAtUtc);
            Assert.Null(processStartTimeUtc);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
