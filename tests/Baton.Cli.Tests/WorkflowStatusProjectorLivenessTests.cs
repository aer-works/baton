using System.Diagnostics;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Projection;
using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// #1375: <c>baton status --json</c> must report the same <see cref="EngineLivenessProbe"/> verdict
/// <see cref="StatusCommand.FormatStepStatus"/> already renders for the human path -- one probe, two
/// renderings (spec/baton.md §3), never a second, independently-invented check. Hand-assembled
/// <see cref="FlowEvent"/>/state, mirroring <see cref="EngineLivenessProbeTests"/>'s own
/// direct-construction idiom, rather than racing a real SIGKILL against a live dispatch.
/// </summary>
public sealed class WorkflowStatusProjectorLivenessTests
{
    private static readonly StepId StepId = new("solo");
    private static readonly WorkflowId WorkflowId = new("wf-1");

    private static WorkflowDefinitionSnapshot OneStepSnapshot() => new(
        new WorkflowDefinitionSnapshotId("snapshot-1"),
        new WorkflowTemplateId("liveness-json"),
        WorkflowTemplateVersion: 1,
        Steps: [new WorkflowStepDefinition(StepId, "solo", [], ["out"], DependsOn: [], RetryPolicy: new RetryPolicy(1))]);

    private static ExecutionRequest MakeRequest(ExecutionId executionId) => new(
        executionId, WorkflowId, StepId, "worker",
        Inputs: [], Outputs: [], Timeout: TimeSpan.FromMinutes(10), Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

    /// <summary>Same technique as <c>EngineLivenessProbeTests.DeadProcessIdentity</c>: capture a real
    /// process's identity while it is provably alive, then kill it, so the probe's OS-level checks
    /// (start-time match, <c>HasExited</c>) see a genuinely dead PID rather than a fabricated one that
    /// might coincidentally collide with something else running on the host.</summary>
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

    [Fact]
    public void A_running_step_whose_recorded_engine_is_dead_reports_liveness_dead_while_state_stays_Running()
    {
        var executionId = new ExecutionId("exec-1");
        var (deadPid, deadStartTime) = DeadProcessIdentity();
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId), EnginePid: deadPid, EngineStartTime: deadStartTime);

        var state = StateProjector.Project([accepted], OneStepSnapshot());
        var entries = new List<LogEntry> { new LogEntry.FlowLogEntry(accepted) };

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        // The raw state a pre-#1375 caller already saw -- unchanged, still "Running" forever from the
        // ledger's own point of view. `liveness` is the new, additive fact that a SIGKILLed engine
        // does not silently keep reading as healthy.
        Assert.Equal("Running", step.State);
        Assert.Equal("dead", step.Liveness);
    }

    [Fact]
    public void A_running_step_whose_recorded_engine_is_alive_reports_liveness_alive()
    {
        var executionId = new ExecutionId("exec-1");
        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId), EnginePid: livePid, EngineStartTime: liveStartTime);

        var state = StateProjector.Project([accepted], OneStepSnapshot());
        var entries = new List<LogEntry> { new LogEntry.FlowLogEntry(accepted) };

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("Running", step.State);
        Assert.Equal("alive", step.Liveness);
    }

    [Fact]
    public void A_running_step_with_no_recorded_engine_identity_reports_liveness_unknown_never_an_absent_field()
    {
        // The legacy/miss arm: no ExecutionRequestAccepted identity for this execution at all (e.g. an
        // older ledger entry recorded before EnginePid/EngineStartTime existed). FormatStepStatus's
        // human path still calls Probe(null, null) unconditionally and gets "liveness unknown" back
        // (EngineLivenessProbeTests.FormatStepStatus_renders_both_polarities_and_probe_failure's
        // "legacyAccepted" case) -- this pins the JSON path doing the identical thing rather than
        // omitting the field on a miss, which would make "liveness present" mean something different
        // between the two renderings.
        var executionId = new ExecutionId("exec-1");
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId), EnginePid: null, EngineStartTime: null);

        var state = StateProjector.Project([accepted], OneStepSnapshot());
        var entries = new List<LogEntry> { new LogEntry.FlowLogEntry(accepted) };

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("Running", step.State);
        Assert.Equal("unknown", step.Liveness);
    }

    [Fact]
    public void A_failed_step_with_no_pending_retry_never_carries_a_liveness_verdict_even_over_a_dead_engine()
    {
        // #1513 widened the gate to a Failed step with a pending RetryNotBefore -- this pins the
        // negative next to it: a step whose retry budget is exhausted (Permanent classification, no
        // FlowEvent.StepRetryScheduled ever recorded) has no pending wait for a dead engine to be
        // failing to honor, and must stay ungated exactly like the pre-#1513 Failed case did.
        var executionId = new ExecutionId("exec-1");
        var (deadPid, deadStartTime) = DeadProcessIdentity();
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId), EnginePid: deadPid, EngineStartTime: deadStartTime);
        var events = new FlowEvent[]
        {
            accepted,
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Permanent, "unrecoverable", RetryNotBefore: null),
        };

        var state = StateProjector.Project(events, OneStepSnapshot());
        var entries = events.Select(e => (LogEntry)new LogEntry.FlowLogEntry(e)).ToList();

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("Failed", step.State);
        Assert.Null(step.Liveness);
        Assert.DoesNotContain("\"liveness\"", System.Text.Json.JsonSerializer.Serialize(step));
    }

    [Fact]
    public void A_paused_step_never_carries_a_liveness_verdict_even_over_a_dead_engine()
    {
        // Mirrors FormatStepStatus's own gate (StatusCommand.cs): a Paused step's engine has
        // legitimately exited -- probing it would misreport a healthy paused room as crashed.
        var executionId = new ExecutionId("exec-1");
        var decisionId = new DecisionId("decision-1");
        var (deadPid, deadStartTime) = DeadProcessIdentity();
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId), EnginePid: deadPid, EngineStartTime: deadStartTime);
        var events = new FlowEvent[]
        {
            accepted,
            new FlowEvent.ExecutionSucceeded(executionId),
            new FlowEvent.WorkflowPaused(executionId, StepId),
        };

        var state = StateProjector.Project(events, OneStepSnapshot());
        var entries = events.Select(e => (LogEntry)new LogEntry.FlowLogEntry(e)).ToList();

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("Paused", step.State);
        Assert.Null(step.Liveness);
        // Wire-level: the key must be ABSENT, not present-as-null — the omission rests on the
        // WhenWritingNull attribute, and only a serialized assertion catches that attribute breaking.
        Assert.DoesNotContain("\"liveness\"", System.Text.Json.JsonSerializer.Serialize(step));
    }

    [Fact]
    public void An_ExhaustedUntil_park_with_a_recorded_obligation_surfaces_the_reset_instant_verbatim()
    {
        // #1551: StepRetryScheduled's own RetryNotBefore, round-tripped verbatim (ISO "O") rather
        // than re-derived -- same value FormatVendorQuotaParkNotice/StatusCommand render as
        // "resumes at HH:mm" for the human path.
        var executionId = new ExecutionId("exec-1");
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId));
        var resetInstant = new DateTimeOffset(2026, 9, 1, 21, 59, 0, TimeSpan.Zero);
        var events = new FlowEvent[]
        {
            accepted,
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: resetInstant),
            new FlowEvent.StepRetryScheduled(StepId, executionId, resetInstant, RetryDelayMs: 0),
        };

        var state = StateProjector.Project(events, OneStepSnapshot());
        var entries = events.Select(e => (LogEntry)new LogEntry.FlowLogEntry(e)).ToList();

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("Failed", step.State);
        Assert.Equal("ExhaustedUntil", step.FailureKind);
        Assert.Equal(resetInstant.ToString("O"), step.ExhaustedUntil);
    }

    [Fact]
    public void An_ExhaustedUntil_park_with_no_recorded_obligation_omits_the_reset_instant()
    {
        // Post-#1115/0026 §5: an un-obligated ExhaustedUntil (no StepRetryScheduled) renders
        // "reset unknown" on the human path (StatusCommand.FormatStepStatus) -- the machine field
        // must stay absent here too rather than fabricate an instant nobody recorded.
        var executionId = new ExecutionId("exec-1");
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId));
        var events = new FlowEvent[]
        {
            accepted,
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: null),
        };

        var state = StateProjector.Project(events, OneStepSnapshot());
        var entries = events.Select(e => (LogEntry)new LogEntry.FlowLogEntry(e)).ToList();

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Null(step.ExhaustedUntil);
        Assert.DoesNotContain("\"exhaustedUntil\"", System.Text.Json.JsonSerializer.Serialize(step));
    }

    [Fact]
    public void An_ordinary_Retryable_backoff_never_surfaces_a_reset_instant_despite_sharing_RetryNotBefore()
    {
        // The gate is FailureKind == "ExhaustedUntil" specifically, not "any Failed step with a
        // pending RetryNotBefore" -- an ordinary backoff schedules a RetryNotBefore too, but it is
        // not a vendor-quota park and this field must not claim otherwise.
        var executionId = new ExecutionId("exec-1");
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId));
        var retryNotBefore = DateTimeOffset.UtcNow.AddMinutes(5);
        var events = new FlowEvent[]
        {
            accepted,
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.Retryable, "transient", RetryNotBefore: retryNotBefore),
            new FlowEvent.StepRetryScheduled(StepId, executionId, retryNotBefore, RetryDelayMs: 5000),
        };

        var state = StateProjector.Project(events, OneStepSnapshot());
        var entries = events.Select(e => (LogEntry)new LogEntry.FlowLogEntry(e)).ToList();

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("Retryable", step.FailureKind);
        Assert.Null(step.ExhaustedUntil);
    }

    [Fact]
    public void A_Stalled_park_keeps_reporting_its_now_past_reset_instant_rather_than_clearing_it()
    {
        // #1513: liveness confirming the scheduling engine dead is a display-only downgrade at the
        // FleetStatusTool room level (StalledDisplayState) -- it never touches this projection or
        // the step's own recorded RetryNotBefore. A consumer (the glass chip) is what renders a
        // past instant honestly; the data layer keeps reporting the same fact it always did.
        var executionId = new ExecutionId("exec-1");
        var (deadPid, deadStartTime) = DeadProcessIdentity();
        var accepted = new FlowEvent.ExecutionRequestAccepted(MakeRequest(executionId), EnginePid: deadPid, EngineStartTime: deadStartTime);
        var pastResetInstant = DateTimeOffset.UtcNow.AddHours(-2);
        var events = new FlowEvent[]
        {
            accepted,
            new FlowEvent.ExecutionFailed(executionId, FailureClassification.ExhaustedUntil, "quota exhausted", RetryNotBefore: pastResetInstant),
            new FlowEvent.StepRetryScheduled(StepId, executionId, pastResetInstant, RetryDelayMs: 0),
        };

        var state = StateProjector.Project(events, OneStepSnapshot());
        var entries = events.Select(e => (LogEntry)new LogEntry.FlowLogEntry(e)).ToList();

        var view = WorkflowStatusProjector.Project(state, OneStepSnapshot(), Path.GetTempPath(), entries);

        var step = Assert.Single(view.Steps);
        Assert.Equal("dead", step.Liveness);
        Assert.Equal(pastResetInstant.ToString("O"), step.ExhaustedUntil);
    }
}
