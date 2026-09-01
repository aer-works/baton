using System.Diagnostics;
using System.Text.Json;
using Baton.Cli.Tests.TestSupport;
using Baton.Concurrency;
using Baton.Domain;
using Baton.Mutation;
using Baton.Store;
using Baton.Templates;
using Baton.Vendors;

namespace Baton.Cli.Tests;

/// <summary>
/// #1586: a room whose <c>baton run</c> pump crashed while it held <c>flow.lock</c> — most visibly,
/// mid-wait on a vendor-quota park's <c>RetryNotBefore</c> — leaves the OS lock free (release is
/// automatic on process exit) but its <c>flow.lock.holder</c> sidecar stale, still naming the dead
/// pump's pid. Measured against a copy of a real such room (issue #1586's own conductor run):
/// <c>baton cancel</c> acquired the free lock, overwrote the sidecar with its own identity —
/// destroying the record of which engine died — journalled a too-late <c>CancellationRequested</c>,
/// and then hung, because <c>MutationInterface</c>'s pump re-enters the identical
/// <c>Task.Delay</c> the dead pump was in for the same doomed retry. This file proves the fix: a
/// dead holder is refused before any of that happens, and the sidecar is never touched.
/// <see cref="CancelCommandWorkflowLockedFallThroughTests"/> is this file's control arm — a genuinely
/// live holder (the lock is still OS-held) must still fall through unchanged.
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
            // The gate this fixture must exercise is `hasFutureDeferral` (CancelCommand.cs) --
            // it only fires for a room with a step still waiting on a future RetryNotBefore, the
            // exact shape a dead-mid-park pump leaves behind. A room that ran to Terminal (no
            // pending retry) never reaches the dead-holder throw at all -- hand-write the parked
            // shape directly, the same way StatusCommandEndToEndTests.WriteParkedStepFixtureAsync
            // proves a quota park without needing an adapter that can report one.
            var bindingsFilePath = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            // Reconstruct the exact stale-sidecar-beside-a-free-lock shape a crash leaves behind,
            // naming a real pid that was genuinely alive and is now genuinely dead (never a
            // fabricated number that might coincidentally collide with something else on the host).
            var (deadPid, deadStartTime) = DeadProcessIdentity();
            var holderPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            var originalHolderJson = JsonSerializer.Serialize(new
            {
                HolderDescription = $"baton run pump (pid {deadPid})",
                Pid = deadPid,
                AcquiredAtUtc = deadStartTime.UtcDateTime,
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
    /// pending future retry -- but a stale sidecar naming a pid that is genuinely still alive must
    /// NOT be treated as dead. The gate has to discriminate on liveness, not on room shape; varying
    /// only the pid (never the room) is what makes this a real control rather than a different test.
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
            var bindingsFilePath = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            using var currentProcess = Process.GetCurrentProcess();
            var holderPath = Path.Combine(roomDirectory, ConcurrencyGuard.FlowHolderFileName);
            var aliveHolderJson = JsonSerializer.Serialize(new
            {
                HolderDescription = "leftover holder from an unrelated, still-running process",
                Pid = currentProcess.Id,
                AcquiredAtUtc = currentProcess.StartTime.ToUniversalTime(),
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

    private static (int Pid, DateTimeOffset StartTime) DeadProcessIdentity()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "30") { CreateNoWindow = true };

        using var process = Process.Start(psi)!;
        try
        {
            return (process.Id, new DateTimeOffset(process.StartTime).ToUniversalTime());
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    /// <summary>
    /// Hand-writes a snapshot plus an <c>ExecutionFailed</c>(<see cref="FailureClassification.ExhaustedUntil"/>)
    /// / <c>StepRetryScheduled</c> pair directly to <c>flow.jsonl</c> -- the same shape
    /// <c>StatusCommandEndToEndTests.WriteParkedStepFixtureAsync</c> hand-writes for the identical
    /// reason: a room with a step still waiting on a future <c>RetryNotBefore</c> is the only shape
    /// that reaches CancelCommand's dead-holder gate at all (<c>hasFutureDeferral</c>) -- a room that
    /// ran to <c>Terminal</c> has no pending retry, so the gate never fires and the two arms above
    /// become indistinguishable. Also writes bindings.json for <see cref="CancelOptions"/> to name,
    /// even though the dead-holder throw fires before it is ever read.
    /// </summary>
    private static async Task<string> WriteParkedStepFixtureAsync(string testRoot, string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("parked-dead-holder-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-parked-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-parked-dead-holder"),
            new StepId("implement"),
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var retryNotBefore = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(45));

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", retryNotBefore),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.StepRetryScheduled(new StepId("implement"), executionId, retryNotBefore, 2_700_000),
                TestContext.Current.CancellationToken);
        }

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
