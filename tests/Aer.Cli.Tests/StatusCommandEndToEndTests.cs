using System.Text.Json;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Store;
using Aer.Flow.Templates;

namespace Aer.Cli.Tests;

public class StatusCommandEndToEndTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public async Task Status_of_a_terminal_workflow_reports_one_line_per_step_with_status_and_execution_id()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var architectExecutionId = finalState.Steps.First(s => s.StepId.Value == "architect").LatestExecutionId!.Value.Value;

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("Workflow status: Terminal", text);
            // The " @ " keeps this as tight as the old closing-paren form: nothing may sit
            // between the execution id and the timestamp the envelope now renders there.
            Assert.Contains($"architect: Succeeded (execution={architectExecutionId} @ ", text);
            Assert.Contains("critic: Succeeded", text);
            Assert.Contains("publisher: Succeeded", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_against_a_nonexistent_room_directory_throws_a_typed_error_and_creates_nothing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), TextWriter.Null, TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(roomDirectory));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_against_an_existing_directory_with_no_snapshot_throws_the_same_typed_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);

            await Assert.ThrowsAsync<SnapshotLoadException>(
                () => StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), TextWriter.Null, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_succeeds_while_another_process_holds_the_workflow_lock_and_writes_nothing()
    {
        // The control that actually discriminates: if StatusCommand ever acquired
        // ConcurrencyGuard's lock itself, this call would throw WorkflowLockedException the moment
        // another holder (simulated here) already has it -- exactly the failure a live `aer run`
        // pump would trigger for a real operator running `aer status` alongside it.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var guard = ConcurrencyGuard.Acquire(roomDirectory);
            var filesBefore = Directory.GetFiles(roomDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var filesAfter = Directory.GetFiles(roomDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(filesBefore, filesAfter);
            Assert.Contains("Workflow status: Terminal", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Follow_on_an_already_terminal_workflow_prints_state_and_exits_without_hanging()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var output = new StringWriter();
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), output, TestContext.Current.CancellationToken);

            var completedFirst = await Task.WhenAny(
                statusTask, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

            Assert.True(ReferenceEquals(statusTask, completedFirst), "aer status --follow hung on an already-terminal workflow instead of exiting.");
            await statusTask;

            Assert.Contains("Workflow status: Terminal", output.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Regression test for the startup race documented on <c>StatusCommand.FollowAsync</c>'s own
    /// baseline-seeding comment (see it for the mechanism — a slow consumer applying backpressure
    /// to a piped <c>Console.Out</c> between the initial print and the tailing loop's baseline
    /// capture).
    /// <para>
    /// Reproduced deterministically with a <see cref="TextWriter"/> that blocks its first
    /// <c>WriteLine</c> call on a gate the test controls, rather than by racing real timing (an
    /// earlier version of this test appended the terminal event immediately after starting the
    /// follow task and found it usually landed before <c>ExecuteAsync</c>'s own initial read too —
    /// a false pass that would have looked identical whether or not the fix below existed).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Follow_does_not_hang_when_the_workflow_finishes_while_the_initial_print_is_still_blocked_on_a_slow_consumer()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(roomDirectory);
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("race-probe"),
                1,
                [new WorkflowStepDefinition(new StepId("step-one"), "step-one", [], ["out"], [], new RetryPolicy(1))]);
            var snapshot = SnapshotBinder.Bind(definition);
            var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
            await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            var executionId = new ExecutionId("exec-race-1");
            var request = new ExecutionRequest(
                executionId,
                new WorkflowId("wf-race"),
                new StepId("step-one"),
                "step-one",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromSeconds(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            }

            using var releaseGate = new ManualResetEventSlim(false);
            var blockingWriter = new BlockingOnFirstWriteLineTextWriter(releaseGate);

            // ExecuteAsync's synchronous prefix (the initial read, PrintState, and FollowAsync's
            // baseline capture) runs on whatever thread calls it; that prefix is about to block
            // inside `blockingWriter`'s first WriteLine, so it must run on its own thread rather
            // than this test's -- otherwise the block below would deadlock against itself.
            var statusTask = Task.Run(
                () => StatusCommand.ExecuteAsync(
                    new StatusOptions(roomDirectory, Follow: true), blockingWriter, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);

            Assert.True(
                blockingWriter.FirstWriteLineStarted.Wait(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken),
                "PrintState's first WriteLine never started -- the harness itself is broken, not the fix under test.");

            // The workflow finishes now, while ExecuteAsync is still blocked inside PrintState --
            // strictly before FollowAsync's baseline capture, which only runs once PrintState (and
            // therefore this blocked WriteLine call) returns.
            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(executionId), TestContext.Current.CancellationToken);
            }

            releaseGate.Set();

            var completedFirst = await Task.WhenAny(
                statusTask, Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            Assert.True(
                ReferenceEquals(statusTask, completedFirst),
                "aer status --follow hung: the workflow finished while the initial print was still blocked, " +
                "before the tailing loop's own baseline capture.");
            await statusTask;

            Assert.Contains("Workflow status: Terminal", blockingWriter.ToString());
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Blocks its first <see cref="WriteLine(string?)"/> call on a caller-supplied gate, standing
    /// in for a piped <c>Console.Out</c> whose downstream reader is applying backpressure --
    /// deterministic where racing real wall-clock timing against the command's own internals is
    /// not (see the test this backs).
    /// </summary>
    private sealed class BlockingOnFirstWriteLineTextWriter(ManualResetEventSlim releaseGate) : TextWriter
    {
        private readonly StringWriter _inner = new();
        private bool _hasBlockedOnce;

        public ManualResetEventSlim FirstWriteLineStarted { get; } = new(false);

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);

            if (_hasBlockedOnce)
            {
                return;
            }

            _hasBlockedOnce = true;
            FirstWriteLineStarted.Set();
            releaseGate.Wait(TimeSpan.FromMinutes(2));
        }

        public override string ToString() => _inner.ToString();
    }

    private sealed class SignalingTextWriter(string signalFilePath) : TextWriter
    {
        private readonly StringWriter _inner = new();
        private bool _signaled;

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            Signal();
        }

        public override void Write(string? value)
        {
            _inner.Write(value);
            Signal();
        }

        private void Signal()
        {
            if (!_signaled)
            {
                _signaled = true;
                try
                {
                    File.WriteAllText(signalFilePath, "started");
                }
                catch
                {
                }
            }
        }

        public override string ToString() => _inner.ToString();
    }

    [Fact]
    public async Task Following_a_running_workflow_prints_new_events_as_they_land_and_exits_at_terminal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var signalFilePath = Path.Combine(testRoot, "status-started.flag");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepGatedBindingsAsync(testRoot, signalFilePath);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            while (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var output = new SignalingTextWriter(signalFilePath);
            var statusTask = StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), output, TestContext.Current.CancellationToken);

            var runResult = await runTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);

            await statusTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("ExecutionRequestAccepted", text);
            Assert.Contains("ExecutionSucceeded", text);
            Assert.Contains("Workflow status: Terminal", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_follow_on_a_still_running_workflow_returns_cleanly_instead_of_throwing()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        var signalFilePath = Path.Combine(testRoot, "status-started.flag");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepGatedBindingsAsync(testRoot, signalFilePath);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var runTask = RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            var logPath = Path.Combine(roomDirectory, "flow.jsonl");
            while (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            using var followCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            followCancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

            var output = new SignalingTextWriter(signalFilePath);
            var statusTask = StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory, Follow: true), output, followCancellation.Token);

            await statusTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            var exception = await Record.ExceptionAsync(() => statusTask);
            Assert.Null(exception);

            // Ensure the signal file is created so runTask can finish cleanly
            if (!File.Exists(signalFilePath))
            {
                File.WriteAllText(signalFilePath, "started");
            }

            await runTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_follow_at_its_first_await_returns_cleanly_instead_of_throwing()
    {
        // #999 (mechanism recorded there and at StatusCommand.ExecuteAsync's catch): an
        // already-cancelled token interrupts the guarded region's FIRST awaited call — the
        // snapshot load, per the #999 reviewer, not the journal read the gates run caught —
        // deterministically instead of needing a loaded machine. The journal-read window is
        // covered by the same enclosing filter, not independently exercised here.
        // Red arm: with the follow-mode OperationCanceledException filter removed from
        // StatusCommand.ExecuteAsync, this throws.
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var exception = await Record.ExceptionAsync(() => StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: true), TextWriter.Null, cancelled.Token));
            Assert.Null(exception);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Cancelling_a_one_shot_status_probe_still_throws_it_produced_no_answer()
    {
        // #999's polarity arm: without --follow there is no "stop following" semantic — a
        // cancelled probe returned nothing, and returning cleanly would report silence as
        // success (fail-open).
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);
            await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StatusCommand.ExecuteAsync(
                new StatusOptions(roomDirectory, Follow: false), TextWriter.Null, cancelled.Token));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_output_includes_per_step_timestamp_for_events_with_writer_timestamp()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var workflowFilePath = await WriteThreeStepWorkflowAsync(testRoot);
            var bindingsFilePath = await WriteThreeStepBindingsAsync(testRoot);
            var runOptions = new RunOptions(workflowFilePath, bindingsFilePath, roomDirectory);

            var finalState = (await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken)).State;
            Assert.Equal(WorkflowStatus.Terminal, finalState.Status);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            // Timestamp format is ISO 8601 (O format), which looks like "2026-01-15T12:34:56.1234567Z"
            // and appears in output as "@ 2026-01-15T12:34:56.1234567Z"
            Assert.Matches(@"@ \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_a_quota_parked_step_renders_its_classification_and_local_retry_time()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, _, _, retryNotBefore) = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            var expectedLocalTime = retryNotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains($"implement: parked (vendor quota) — retries {expectedLocalTime}", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_an_unknown_instant_exhausted_step_renders_parked_vendor_quota_reset_unknown()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            await WriteUnknownInstantExhaustedStepFixtureAsync(testRoot, roomDirectory);

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.Contains("implement: parked (vendor quota) — reset unknown", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_an_ordinary_backoff_park_days_away_renders_retryable_with_the_full_date()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, _, _, retryNotBefore) = await WriteParkedStepFixtureAsync(
                testRoot,
                roomDirectory,
                FailureClassification.Retryable,
                retryIn: TimeSpan.FromDays(3));

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            var expectedLocalTime = retryNotBefore.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains($"implement: parked (retryable) — retries {expectedLocalTime}", text);
            Assert.DoesNotContain("parked (vendor quota)", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Status_of_a_step_that_retried_after_being_parked_no_longer_renders_parked()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"cli-e2e-{Guid.NewGuid():N}");
        var roomDirectory = Path.Combine(testRoot, "task");
        try
        {
            var (_, logPath, _, _) = await WriteParkedStepFixtureAsync(testRoot, roomDirectory);

            var retriedExecutionId = new ExecutionId("exec-parked-2");
            var retriedRequest = new ExecutionRequest(
                retriedExecutionId,
                new WorkflowId("wf-parked"),
                new StepId("implement"),
                "implement",
                Inputs: [],
                Outputs: [],
                Timeout: TimeSpan.FromSeconds(30),
                Environment: [],
                UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

            await using (var writer = new FlowEventLogWriter(logPath))
            {
                await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(retriedRequest), TestContext.Current.CancellationToken);
                await writer.AppendAsync(new FlowEvent.ExecutionSucceeded(retriedExecutionId), TestContext.Current.CancellationToken);
            }

            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(roomDirectory), output, TestContext.Current.CancellationToken);

            var text = output.ToString();
            Assert.DoesNotContain("implement: parked", text);
            Assert.Contains("implement: Succeeded", text);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <summary>
    /// Hand-writes a snapshot plus an <c>ExecutionFailed</c>(<see cref="FailureClassification.ExhaustedUntil"/>)
    /// / <c>StepRetryScheduled</c> pair directly to <c>flow.jsonl</c> — the shape #594's retry
    /// scheduling actually records for a quota park — rather than driving it through
    /// <see cref="RunCommand"/>, whose <see cref="ShellCommandWorkerAdapter"/> has no way to report
    /// a quota classification.
    /// </summary>
    private static async Task<(string SnapshotPath, string LogPath, ExecutionId ExecutionId, DateTimeOffset RetryNotBefore)>
        WriteParkedStepFixtureAsync(
            string testRoot,
            string roomDirectory,
            FailureClassification classification = FailureClassification.ExhaustedUntil,
            TimeSpan? retryIn = null)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("parked-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-parked-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-parked"),
            new StepId("implement"),
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        var retryNotBefore = DateTimeOffset.UtcNow.Add(retryIn ?? TimeSpan.FromMinutes(45));

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, classification, "attempt failed", retryNotBefore),
                TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.StepRetryScheduled(new StepId("implement"), executionId, retryNotBefore, 2_700_000),
                TestContext.Current.CancellationToken);
        }

        return (snapshotPath, logPath, executionId, retryNotBefore);
    }

    private static async Task<(string SnapshotPath, string LogPath, ExecutionId ExecutionId)>
        WriteUnknownInstantExhaustedStepFixtureAsync(
            string testRoot,
            string roomDirectory)
    {
        Directory.CreateDirectory(roomDirectory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("parked-probe"),
            1,
            [new WorkflowStepDefinition(new StepId("implement"), "implement", [], ["out"], [], new RetryPolicy(3))]);
        var snapshot = SnapshotBinder.Bind(definition);
        var snapshotPath = Path.Combine(roomDirectory, "snapshot.json");
        await SnapshotBinder.PersistAsync(snapshot, snapshotPath, TestContext.Current.CancellationToken);

        var logPath = Path.Combine(roomDirectory, "flow.jsonl");
        var executionId = new ExecutionId("exec-parked-1");
        var request = new ExecutionRequest(
            executionId,
            new WorkflowId("wf-parked"),
            new StepId("implement"),
            "implement",
            Inputs: [],
            Outputs: [],
            Timeout: TimeSpan.FromSeconds(30),
            Environment: [],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        await using (var writer = new FlowEventLogWriter(logPath))
        {
            await writer.AppendAsync(new FlowEvent.ExecutionRequestAccepted(request), TestContext.Current.CancellationToken);
            await writer.AppendAsync(
                new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: null),
                TestContext.Current.CancellationToken);
        }

        return (snapshotPath, logPath, executionId);
    }

    private static async Task<string> WriteThreeStepWorkflowAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var definition = new WorkflowDefinition(
            new WorkflowTemplateId("three-step-linear"),
            1,
            [
                new WorkflowStepDefinition(new StepId("architect"), "architect", [], ["plan"], [], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("critic"), "critic", ["plan"], ["review"], [new StepId("architect")], new RetryPolicy(1)),
                new WorkflowStepDefinition(new StepId("publisher"), "publisher", ["review"], ["summary"], [new StepId("critic")], new RetryPolicy(1)),
            ]);

        var path = Path.Combine(directory, "workflow.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(definition));
        return path;
    }

    private static async Task<string> WriteThreeStepBindingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromMinutes(3)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                CopyFirstInputCommand("review"),
                TimeSpan.FromMinutes(3)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromMinutes(3)),
        };

        var path = Path.Combine(directory, "bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    /// <summary>
    /// Three-step chain where step 2 ("critic") waits for a signal file created when
    /// <c>aer status --follow</c> begins its output, eliminating wall-clock races while guaranteeing
    /// that follow observes intermediate events as they land.
    /// </summary>
    private static async Task<string> WriteThreeStepGatedBindingsAsync(string directory, string signalFilePath)
    {
        Directory.CreateDirectory(directory);
        var config = new Dictionary<string, WorkerBindingConfigEntry>
        {
            ["architect"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("architect", [], [new ProducedOutput("plan")], []),
                WriteFileCommand("plan", "the-plan"),
                TimeSpan.FromMinutes(3)),
            ["critic"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("critic", ["plan"], [new ProducedOutput("review")], []),
                GatedCopyFirstInputCommand("review", signalFilePath),
                TimeSpan.FromMinutes(3)),
            ["publisher"] = new WorkerBindingConfigEntry(
                "shell",
                new WorkerContract("publisher", ["review"], [new ProducedOutput("summary")], []),
                CopyFirstInputCommand("summary"),
                TimeSpan.FromMinutes(3)),
        };

        var path = Path.Combine(directory, "gated-bindings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
        return path;
    }

    private static string WriteFileCommand(string outputName, string content) => OperatingSystem.IsWindows()
        ? $"echo {content}>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo {content} > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string CopyFirstInputCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
        : $"cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";

    private static string GatedCopyFirstInputCommand(string outputName, string signalFilePath)
    {
        // && on both arms, never cmd's & (#809): & runs the payload even when the gating
        // powershell fails to spawn, which skips the gate SILENTLY -- the run reaches Terminal
        // before the follow's first read and the assert fails with a missing-events signature
        // instead of naming the gate. Fail closed: a gate that cannot run fails the step loudly.
        // The wait loop's timeout-expiry path still exits 0, so best-effort semantics after 60s
        // are unchanged.
        var normalizedPath = signalFilePath.Replace("\\", "/");
        return OperatingSystem.IsWindows()
            ? $"powershell -NoProfile -Command \"for ($i=0; $i -lt 1200; $i++) {{ if (Test-Path '{normalizedPath}') {{ break }}; Start-Sleep -Milliseconds 50 }}\" && type %AER_INPUT_0% >%AER_OUTPUT_DIR%\\{outputName}"
            : $"sh -c \"for i in \\$(seq 1 1200); do if [ -f \\\"{normalizedPath}\\\" ]; then break; fi; sleep 0.05; done\" && cat \"$AER_INPUT_0\" > \"$AER_OUTPUT_DIR/{outputName}\"";
    }
}

