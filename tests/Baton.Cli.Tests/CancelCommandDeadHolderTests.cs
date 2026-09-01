using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Mutation;
using Baton.Vendors;
using static Baton.Cli.Tests.TestSupport.ParkedStepFixture;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests;

/// <summary>
/// Proves the #1586 dead-holder fail-fast at <see cref="CancelCommand"/>'s own dead-holder-check
/// comment — the mechanism it guards against (what a crashed mid-park pump leaves behind, and what
/// the old behaviour did about it) is documented at that call site, not repeated here. This file
/// asserts the fix's two externally-observable halves: the refusal fires before any lock acquire, and
/// the holder sidecar survives byte-for-byte. <see cref="CancelCommandWorkflowLockedFallThroughTests"/>
/// is this file's control arm — a genuinely live holder (the lock is still OS-held) must still fall
/// through unchanged.
/// </summary>
public class CancelCommandDeadHolderTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Cancelling_a_room_with_a_dead_lock_holder_fails_fast_and_leaves_the_holder_file_byte_identical()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            // The gate this fixture must exercise is `hasFutureDeferral` (CancelCommand.cs) -- see
            // ParkedStepFixture's own doc for why that shape is what gets hand-written here rather
            // than driven through RunCommand.
            await WriteParkedStepFixtureAsync(testRoot, roomDirectory);
            var bindingsFilePath = await WriteImplementBindingsFileAsync(testRoot);

            // Reconstruct the exact stale-sidecar-beside-a-free-lock shape a crash leaves behind.
            // ProcessIdentityFixture.DeadProcessIdentity names a real pid, never a fabricated one.
            // #1604 F2: AcquiredAtUtc is the PRODUCT shape -- distinct from, and here deliberately
            // ten minutes later than, ProcessStartTimeUtc (ConcurrencyGuard.CreateWithSidecar always
            // writes AcquiredAtUtc as DateTime.UtcNow, never the holder's own start time) --
            // ProcessStartTimeUtc separately carries the value EngineLivenessProbe.Probe actually
            // discriminates on, so this fixture cannot pass by accident against a real sidecar shape
            // the way feeding AcquiredAtUtc as a start time did. A fixed ten-minute offset rather than
            // a literal `DateTime.UtcNow` snapshot: a self-hosted xUnit process can itself be well
            // under a second old when this test runs (measured directly against this test host, #1604
            // F2 verification), which would make "now" and "this process's own start time" coincide
            // and mask exactly the bug this fixture exists to catch.
            var (deadPid, deadStartTime) = DeadProcessIdentity();
            var holderPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            var originalHolderJson = JsonSerializer.Serialize(new
            {
                HolderDescription = $"baton run pump (pid {deadPid})",
                Pid = deadPid,
                AcquiredAtUtc = deadStartTime.UtcDateTime.AddMinutes(10),
                ProcessStartTimeUtc = deadStartTime.UtcDateTime,
            });
            await File.WriteAllTextAsync(holderPath, originalHolderJson, TestContext.Current.CancellationToken);
            Assert.False(ConcurrencyGuard.IsHeld(roomDirectory), "the lock must read as free -- only a stale sidecar is being simulated");

            var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: "whatever-exec-id", bindingsFilePath);
            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));

            Assert.Contains("no live pump", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(deadPid.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.TryInvocation);
            Assert.Contains("baton run", ex.TryInvocation, StringComparison.Ordinal);
            Assert.Contains("--room-dir", ex.TryInvocation, StringComparison.Ordinal);

            // The forensic artifact: byte-identical, never overwritten and never deleted.
            var holderContentAfter = await File.ReadAllTextAsync(holderPath, TestContext.Current.CancellationToken);
            Assert.Equal(originalHolderJson, holderContentAfter);

            // Never journalled the too-late cancellation the old behaviour recorded before hanging.
            var journalText = await File.ReadAllTextAsync(Path.Combine(roomDirectory, "flow.jsonl"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("cancellationRequested", journalText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Control arm, polarity check: the identical parked-room shape as the test above -- same
    /// pending future retry, same product-shape sidecar (<c>AcquiredAtUtc</c> ten minutes after
    /// <c>ProcessStartTimeUtc</c>, never equal to it) -- but naming a pid that is genuinely still
    /// alive must NOT be treated as dead. The gate has to discriminate on liveness, not on room or
    /// sidecar shape; varying only the pid (and its matching start time) between this arm and the
    /// one above is what makes this a real control rather than a different test. Confirmed to go red
    /// against the pre-#1604 code (which fed <c>AcquiredAtUtc</c> to the probe as if it were
    /// <c>ProcessStartTimeUtc</c>) before this fix landed.
    /// Falls through to the ordinary unknown-execution refusal, proving the dead-holder branch was
    /// never entered.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_room_whose_stale_sidecar_names_a_still_alive_pid_is_not_treated_as_dead()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteParkedStepFixtureAsync(testRoot, roomDirectory);
            var bindingsFilePath = await WriteImplementBindingsFileAsync(testRoot);

            using var currentProcess = Process.GetCurrentProcess();
            var holderPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            var currentProcessStartTimeUtc = currentProcess.StartTime.ToUniversalTime();
            var aliveHolderJson = JsonSerializer.Serialize(new
            {
                HolderDescription = "leftover holder from an unrelated, still-running process",
                Pid = currentProcess.Id,
                AcquiredAtUtc = currentProcessStartTimeUtc.AddMinutes(10),
                ProcessStartTimeUtc = currentProcessStartTimeUtc,
            });
            await File.WriteAllTextAsync(holderPath, aliveHolderJson, TestContext.Current.CancellationToken);
            Assert.False(ConcurrencyGuard.IsHeld(roomDirectory));

            var cancelOptions = new CancelOptions(roomDirectory, ExecutionId: "whatever-exec-id", bindingsFilePath);

            // Reaches the ordinary machinery instead of the new fail-fast -- "whatever-exec-id" was
            // never admitted, so the pre-existing refusal for that fires, not the dead-holder one.
            await Assert.ThrowsAsync<UnknownExecutionIdException>(
                () => CancelCommand.ExecuteAsync(cancelOptions, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// A bindings.json naming the same "implement" step <see cref="ParkedStepFixture.WriteParkedStepFixtureAsync"/>
    /// journals, for <see cref="CancelOptions"/> to point at -- the dead-holder throw fires before this
    /// is ever read, so its content is a formality, not a fixture under test.
    /// </summary>
    private static async Task<string> WriteImplementBindingsFileAsync(string testRoot)
    {
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["implement"] = new WorkerBindingConfigEntry(
                "shell", new WorkerContract("implement", [], [new ProducedOutput("out")], []), "echo unused", TimeSpan.FromSeconds(30)),
        };
        var bindingsFilePath = Path.Combine(testRoot, "bindings.json");
        await File.WriteAllTextAsync(bindingsFilePath, JsonSerializer.Serialize(config));

        return bindingsFilePath;
    }
}
