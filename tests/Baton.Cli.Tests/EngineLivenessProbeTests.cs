using System.Diagnostics;
using Baton.Cli;
using Baton.Domain;
using Baton.Outcomes;
using static Baton.Cli.Tests.TestSupport.ProcessIdentityFixture;

namespace Baton.Cli.Tests;

public class EngineLivenessProbeTests
{
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly WorkflowId WorkflowId = new("wf-1");
    private static readonly StepId StepId = new("step-1");

    [Fact]
    public void Probe_discrimination_with_real_live_process()
    {
        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var probeResult = EngineLivenessProbe.Probe(livePid, liveStartTime);

        Assert.Equal(EngineLivenessStatus.Alive, probeResult.Status);
        Assert.Null(probeResult.Why);
    }

    [Fact]
    public void Probe_discrimination_with_real_dead_process()
    {
        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var probeResult = EngineLivenessProbe.Probe(deadPid, deadStartTime);

        Assert.Equal(EngineLivenessStatus.Dead, probeResult.Status);
    }

    [Fact]
    public void Probe_failure_arm_returns_unknown_when_identity_is_missing_or_invalid()
    {
        var missingResult = EngineLivenessProbe.Probe(null, null);
        Assert.Equal(EngineLivenessStatus.Unknown, missingResult.Status);
        Assert.Contains("no process identity recorded", missingResult.Why);

        var invalidResult = EngineLivenessProbe.Probe(-1, DateTimeOffset.UtcNow);
        Assert.Equal(EngineLivenessStatus.Unknown, invalidResult.Status);
        Assert.Contains("invalid process identity", invalidResult.Why);
    }

    [Fact]
    public void FormatStepStatus_renders_both_polarities_and_probe_failure()
    {
        var livePid = Environment.ProcessId;
        var liveStartTime = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();

        var (deadPid, deadStartTime) = DeadProcessIdentity();

        var liveAccepted = new FlowEvent.ExecutionRequestAccepted(
            MakeRequest("exec-live"), EnginePid: livePid, EngineStartTime: liveStartTime);

        var deadAccepted = new FlowEvent.ExecutionRequestAccepted(
            MakeRequest("exec-dead"), EnginePid: deadPid, EngineStartTime: deadStartTime);

        var legacyAccepted = new FlowEvent.ExecutionRequestAccepted(
            MakeRequest("exec-legacy"), EnginePid: null, EngineStartTime: null);

        var emptyUpstreams = new Dictionary<StepId, ExecutionId>();
        var runningStepLive = new StepState(StepId, StepStatus.Running, LatestExecutionId: new ExecutionId("exec-live"), emptyUpstreams);
        var runningStepDead = new StepState(StepId, StepStatus.Running, LatestExecutionId: new ExecutionId("exec-dead"), emptyUpstreams);
        var runningStepLegacy = new StepState(StepId, StepStatus.Running, LatestExecutionId: new ExecutionId("exec-legacy"), emptyUpstreams);
        var terminalStepDead = new StepState(StepId, StepStatus.Succeeded, LatestExecutionId: new ExecutionId("exec-dead"), emptyUpstreams);

        // Alive arm: renders Running (no annotation)
        var liveOutput = StatusCommand.FormatStepStatus(runningStepLive, [liveAccepted]);
        Assert.Equal("Running", liveOutput);

        // Dead arm: renders annotation
        var deadOutput = StatusCommand.FormatStepStatus(runningStepDead, [deadAccepted]);
        Assert.Equal("Running — engine not alive; crash recovery will classify on next pump", deadOutput);

        // Terminal step: does NOT render annotation even if probe is dead
        var terminalOutput = StatusCommand.FormatStepStatus(terminalStepDead, [deadAccepted]);
        Assert.Equal("Succeeded", terminalOutput);

        // Probe failure arm: renders liveness unknown (<why>)
        var failureOutput = StatusCommand.FormatStepStatus(runningStepLegacy, [legacyAccepted]);
        Assert.Equal("liveness unknown (no process identity recorded)", failureOutput);

        // Paused step: NO annotation even with a dead engine on record -- why is documented at
        // the positive Running gate in StatusCommand.FormatStepStatus.
        var pausedStepDead = new StepState(StepId, StepStatus.Paused, LatestExecutionId: new ExecutionId("exec-dead"), emptyUpstreams);
        var pausedOutput = StatusCommand.FormatStepStatus(pausedStepDead, [deadAccepted]);
        Assert.Equal("Paused", pausedOutput);

        // Pending step: no execution yet, no liveness claim -- never "liveness unknown".
        var pendingStep = new StepState(StepId, StepStatus.Pending, LatestExecutionId: null, emptyUpstreams);
        Assert.Equal("Pending", StatusCommand.FormatStepStatus(pendingStep, []));
    }

    private static ExecutionRequest MakeRequest(string execId) =>
        new(new ExecutionId(execId), WorkflowId, StepId, "worker", [], [], TimeSpan.FromSeconds(30), [], new Dictionary<StepId, ExecutionId>());
}
