using Baton.Domain;
using Baton.Mutation;
using Baton.Store;

namespace Baton.Cli.Tests;

/// <summary>
/// #1495 / PR #1528: unit-level tests against <see cref="CancelRequestPoller.TickAsync"/> and
/// <see cref="CancelRequestPoller.RunAsync"/>. Covers successful delivery, the false-but-settled
/// consume path, the false-but-still-running retry and deferred delivery path, retry exhaustion
/// rejecting with the #1530 reason, poller reject branches for <c>latest</c> with reason in body,
/// and <see cref="CancelRequestPoller.RunAsync"/>'s own resilience contract.
/// </summary>
public class CancelRequestPollerTests
{
    private static readonly WorkflowDefinitionSnapshot Snapshot = new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("poller-test"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(new StepId("a"), "a", [], ["out"], [], new RetryPolicy(1))]);

    private static readonly WorkflowDefinitionSnapshot TwoStepSnapshot = new(
        new WorkflowDefinitionSnapshotId("snapshot-2"),
        new WorkflowTemplateId("poller-test-2"),
        WorkflowTemplateVersion: 1,
        Steps:
        [
            new WorkflowStepDefinition(new StepId("a"), "a", [], ["out_a"], [], new RetryPolicy(1)),
            new WorkflowStepDefinition(new StepId("b"), "b", [], ["out_b"], [], new RetryPolicy(1)),
        ]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId, StepId stepId)
        => new(
            executionId,
            new WorkflowId("poller-test"),
            stepId,
            "worker",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromMinutes(10),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    [Fact]
    public async Task Successful_delivery_when_registry_holds_target_delivers_and_consumes()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execId = new ExecutionId("exec-1");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

                var registry = new InFlightExecutionRegistry();
                registry.Bind(writer);
                var token = registry.Register(execId);

                await CancelRequestFile.WriteAsync(roomDirectory, "exec-1", TestContext.Current.CancellationToken);

                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

                var requestPath = CancelRequestFile.GetPath(roomDirectory);
                Assert.False(File.Exists(requestPath), "expected the request to be consumed");
                Assert.True(File.Exists($"{requestPath}.consumed"), "expected .consumed sibling to exist");
                Assert.True(token.IsCancellationRequested, "expected registry to signal cancellation");
            }
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_request_naming_an_execution_not_currently_registered_and_not_running_is_consumed_as_a_too_late_no_op()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            // Settled execution in log (Succeeded):
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execId = new ExecutionId("exec-settled");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(execId), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-settled", TestContext.Current.CancellationToken);

            // An empty registry: not in flight, but also no longer projecting Running -> genuinely settled.
            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the request to be consumed, not left pending");
            Assert.True(File.Exists($"{requestPath}.consumed"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task False_but_still_running_execution_is_left_pending_then_delivered_on_later_tick_after_registration()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-racing");

            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-racing", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            registry.Bind(writer);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);

            // Tick 1: Not yet registered in registry, but STILL Running in projection -> left pending.
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(requestPath), "request must remain pending while target still projects Running");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));

            // Register the execution now (simulating registration closing the race gap).
            var token = registry.Register(execId);

            // Tick 2: Now registered -> delivered and consumed!
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(requestPath), "request must be consumed on successful retry");
            Assert.True(File.Exists($"{requestPath}.consumed"));
            Assert.True(token.IsCancellationRequested);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Retry_exhaustion_after_5_still_running_ticks_rejects_with_reason_in_body()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-non-process");

            await using var writer = new FlowEventLogWriter(logPath);
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-non-process", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            registry.Bind(writer);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);

            // Ticks 1 to 4: Left pending.
            for (var i = 1; i <= 4; i++)
            {
                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);
                Assert.True(File.Exists(requestPath), $"request must remain pending on tick {i}");
            }

            // Tick 5: Reaches 5th still-running tick -> rejected!
            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(requestPath), "request must not remain pending after 5 retries");
            var rejectedPath = $"{requestPath}.rejected";
            Assert.True(File.Exists(rejectedPath), "expected .rejected sibling to exist");

            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal("exec-non-process", rejected.Target);
            Assert.Contains("target still running but not reachable through the in-flight registry (likely non-process work, #1530)", rejected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // #1563 (S0 of the quota design, #802): a step Failed with a scheduled RetryNotBefore — the
    // shape the idle-deferral park leaves behind once its worker process has already exited — is
    // neither "still running" (so the old bounded-retry-until-registered path never fires) nor
    // "already settled" (so the pre-#1563 code told the operator "too late", a false claim #802's
    // "three independent locks" finding identified — see CancelRequestPoller.cs's own comment on
    // that finding, F7 #1605 review, for the ASSUMED/code-derived confidence it actually carries).
    // It must be marked on the registry's wake latch and left pending, not consumed, until the pump
    // this registry is bound to actually drains it.
    [Fact]
    public async Task A_quota_parked_target_is_marked_on_the_registry_and_left_pending_not_declared_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-parked");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-parked", TestContext.Current.CancellationToken);

            // Not bound to any pump — mirrors production, where the poller only ever holds the
            // in-process handle to whatever pump started it; marking must not require a live process.
            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.True(File.Exists(requestPath), "request must remain pending until the pump actually settles the park");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));
            Assert.Contains(execId, registry.DrainParkedCancelIntents());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Second-reader review finding, explained once beside the fix at CancelRequestPoller.cs's
    // `isParked` early-return above the bounded-retry counter: ticks well past 5 with no pump ever
    // draining the mark, to prove the request survives indefinitely rather than being rejected on a
    // ceiling sized for a different failure mode.
    [Fact]
    public async Task A_quota_parked_target_survives_past_the_bounded_retry_ceiling_without_being_rejected()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-parked-slow");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-parked-slow", TestContext.Current.CancellationToken);

            // No pump is ever started against this registry — the mark is drained by nobody, for
            // as many ticks as the old "still running" ceiling (5) would have tolerated and beyond.
            var registry = new InFlightExecutionRegistry();

            for (var i = 0; i < 8; i++)
            {
                await CancelRequestPoller.TickAsync(
                    roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);
            }

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.True(File.Exists(requestPath), "a parked mark must never be rejected on a tick ceiling — only the pump's own settle consumes it");
            Assert.False(File.Exists($"{requestPath}.consumed"));
            Assert.False(File.Exists($"{requestPath}.rejected"));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    // Once the pump this registry is bound to has actually processed the mark and settled the park
    // as Cancelled, the poller's own consume branch must say so honestly rather than repeat the
    // generic "too late" text — that text is what #802's "three independent locks" finding
    // identified as a false claim once an arrest is what actually ended the park (see
    // CancelRequestPoller.cs's own comment for that finding's actual confidence — F7, #1605 review).
    [Fact]
    public async Task Once_the_pump_settles_a_marked_park_as_Cancelled_the_poller_reports_arrested_not_too_late()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        var originalError = Console.Error;
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var execId = new ExecutionId("exec-arrested");
            var reset = DateTimeOffset.UtcNow.AddHours(2);

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execId, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionFailed(execId, FailureClassification.ExhaustedUntil, "quota exhausted", reset), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.StepRetryScheduled(new StepId("a"), execId, reset, RetryDelayMs: (int)TimeSpan.FromHours(2).TotalMilliseconds), TestContext.Current.CancellationToken);
                // Simulates the pump having already drained a prior mark and settled the park —
                // this test isolates the poller's own message branch from the pump's wake wiring,
                // which QuotaParkCancelArrestTests (Baton.Tests) covers end to end.
                await writer.AppendAsync(new FlowEvent.CancellationRequested(execId), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionCancelled(execId), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, "exec-arrested", TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath), "expected the request to be consumed once settled");
            Assert.True(File.Exists($"{requestPath}.consumed"));
            Assert.Contains("arrested by this request", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("too late", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Latest_requested_with_zero_running_rejects_with_reason_in_body()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            // Empty log: no running steps.
            await File.WriteAllTextAsync(logPath, string.Empty, TestContext.Current.CancellationToken);
            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, Snapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath));
            var rejectedPath = $"{requestPath}.rejected";
            Assert.True(File.Exists(rejectedPath));

            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal(CancelRequestFile.LatestTarget, rejected.Target);
            Assert.Contains("'latest' requested, but no execution is currently Running", rejected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Latest_requested_with_two_running_rejects_with_reason_in_body()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                var execA = new ExecutionId("exec-a");
                var execB = new ExecutionId("exec-b");
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execA, new StepId("a"))), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(MakeRequest(execB, new StepId("b"))), TestContext.Current.CancellationToken);
            }

            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();

            await CancelRequestPoller.TickAsync(
                roomDirectory, logPath, TwoStepSnapshot, registry, TestContext.Current.CancellationToken);

            var requestPath = CancelRequestFile.GetPath(roomDirectory);
            Assert.False(File.Exists(requestPath));
            var rejectedPath = $"{requestPath}.rejected";
            Assert.True(File.Exists(rejectedPath));

            var rejected = await CancelRequestFile.TryReadRejectedAsync(rejectedPath, TestContext.Current.CancellationToken);
            Assert.NotNull(rejected);
            Assert.Equal(CancelRequestFile.LatestTarget, rejected.Target);
            Assert.Contains("2 executions are currently Running", rejected.Reason);
            Assert.Contains("ambiguous", rejected.Reason);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_survives_a_tick_that_throws_and_keeps_polling_until_cancelled()
    {
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"cancel-request-poller-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            // A corrupt flow.jsonl: FlowEventLogReader.ReadAllAsync throws FlowEventLogReadException on
            // a malformed complete line (Baton/Store/FlowEventLogReader.cs) -- the poller's "latest"
            // branch hits this on every tick for as long as the request stays pending.
            await File.WriteAllTextAsync(logPath, "{ not valid json }\n", TestContext.Current.CancellationToken);
            await CancelRequestFile.WriteAsync(roomDirectory, CancelRequestFile.LatestTarget, TestContext.Current.CancellationToken);

            var registry = new InFlightExecutionRegistry();
            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            pollCancellation.CancelAfter(TimeSpan.FromMilliseconds(1200));

            // Would throw FlowEventLogReadException out of RunAsync itself if a tick's fault were not
            // caught -- the assertion is simply that this completes at all (via the CancelAfter timeout)
            // rather than propagating.
            await CancelRequestPoller.RunAsync(
                roomDirectory, logPath, Snapshot, registry, TimeSpan.FromMilliseconds(150), pollCancellation.Token);

            // Still pending: every tick faulted before ever reaching Consume/Reject.
            Assert.True(File.Exists(CancelRequestFile.GetPath(roomDirectory)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }
}
