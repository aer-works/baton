using Baton.Domain;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// Unit coverage for <see cref="ExecutionBindingResolver.Resolve"/> — the one primitive both
/// <c>ExecutionUsageProjectorTests</c>' rebind arms and <c>QuotaLedgerStoreTests</c>' ledger-level arm
/// now rest on (#1781 review finding 1). These three mirror the scenarios
/// <c>ExecutionUsageProjectorTests</c> already drove end-to-end through a hand-written
/// <c>.stdout.log</c>, but pinned directly against the resolver's output so a change to the
/// precedence itself fails here first, without a filesystem fixture in the way.
/// </summary>
public sealed class ExecutionBindingResolverTests
{
    private static ExecutionRequest AcceptedRequest(ExecutionId executionId, string worker, string? adapter, string? model) => new(
        executionId,
        new WorkflowId("wf-binding-test"),
        new StepId(worker),
        worker,
        Inputs: [],
        Outputs: [],
        Timeout: TimeSpan.FromSeconds(30),
        Environment: [],
        UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
        Adapter: adapter,
        Model: model);

    [Fact]
    public void A_rebound_execution_resolves_to_the_new_binding_from_StepRebound()
    {
        var executionId = new ExecutionId("exec-rebound");
        var stepId = new StepId("plan");
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                AcceptedRequest(executionId, "plan", adapter: "agy", model: "gemini-3-pro"))),
            new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                stepId, executionId, PreviousAdapter: "agy", PreviousModel: "gemini-3-pro",
                NewAdapter: "claude", NewModel: "sonnet")),
        };

        var bindings = ExecutionBindingResolver.Resolve(entries);

        var binding = Assert.Single(bindings).Value;
        Assert.Equal("claude", binding.Adapter);
        Assert.Equal("sonnet", binding.Model);
    }

    [Fact]
    public void An_execution_without_StepRebound_resolves_to_the_originally_accepted_binding()
    {
        // Polarity partner to the test above: with no StepRebound at all, the frozen accepted
        // request's own adapter/model stand.
        var executionId = new ExecutionId("exec-no-rebound");
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                AcceptedRequest(executionId, "plan", adapter: "agy", model: "gemini-3-pro"))),
        };

        var bindings = ExecutionBindingResolver.Resolve(entries);

        var binding = Assert.Single(bindings).Value;
        Assert.Equal("agy", binding.Adapter);
        Assert.Equal("gemini-3-pro", binding.Model);
    }

    [Fact]
    public void Two_StepRebound_events_for_the_same_execution_resolve_to_the_binding_that_actually_ran()
    {
        // #1583 HIGH, review scenario B: a rebind claude->agy followed by a reverting rebind
        // agy->claude must land on "claude" via last-write-wins, not the intermediate "agy".
        var executionId = new ExecutionId("exec-double-rebound");
        var stepId = new StepId("plan");
        var entries = new List<LogEntry>
        {
            new LogEntry.FlowLogEntry(new FlowEvent.ExecutionRequestAccepted(
                AcceptedRequest(executionId, "plan", adapter: "claude", model: "sonnet"))),
            new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                stepId, executionId, PreviousAdapter: "claude", PreviousModel: "sonnet",
                NewAdapter: "agy", NewModel: "gemini-3-pro")),
            new LogEntry.FlowLogEntry(new FlowEvent.StepRebound(
                stepId, executionId, PreviousAdapter: "agy", PreviousModel: "gemini-3-pro",
                NewAdapter: "claude", NewModel: "sonnet")),
        };

        var bindings = ExecutionBindingResolver.Resolve(entries);

        var binding = Assert.Single(bindings).Value;
        Assert.Equal("claude", binding.Adapter);
        Assert.Equal("sonnet", binding.Model);
    }
}
