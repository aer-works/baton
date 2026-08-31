using Baton.Domain;

namespace Baton.Tests.TestSupport;

public class FlowAssertTests
{
    private static StepState StepWith(
        StepStatus status,
        string? latestFailureReason = null,
        FailureClassification? latestFailureClassification = null,
        int consecutiveFailureCount = 0) =>
        new(
            new StepId("architect"),
            status,
            LatestExecutionId: null,
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>(),
            ConsecutiveFailureCount: consecutiveFailureCount,
            LatestFailureClassification: latestFailureClassification,
            LatestFailureReason: latestFailureReason);

    [Fact]
    public void Succeeded_does_not_throw_when_the_step_actually_succeeded()
    {
        FlowAssert.Succeeded(StepWith(StepStatus.Succeeded));
    }

    [Fact]
    public void Succeeded_reports_status_and_failure_reason_on_mismatch()
    {
        var step = StepWith(
            StepStatus.Failed,
            latestFailureReason: "contract output 'plan' was not produced",
            latestFailureClassification: FailureClassification.Permanent,
            consecutiveFailureCount: 2);

        var ex = Record.Exception(() => FlowAssert.Succeeded(step));

        Assert.NotNull(ex);
        Assert.Equal(
            "Expected step 'architect' to be Succeeded, but was Failed. "
            + "Latest failure reason: contract output 'plan' was not produced "
            + "(classification: Permanent, consecutive failures: 2)",
            ex.Message);
    }

    [Fact]
    public void Succeeded_says_so_explicitly_when_no_failure_reason_was_recorded()
    {
        var step = StepWith(StepStatus.Cancelled);

        var ex = Record.Exception(() => FlowAssert.Succeeded(step));

        Assert.NotNull(ex);
        Assert.Contains("Latest failure reason: no failure reason recorded", ex.Message);
    }
}
