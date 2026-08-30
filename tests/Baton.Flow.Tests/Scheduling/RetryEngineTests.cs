using Baton.Flow.Domain;
using Baton.Flow.Scheduling;

namespace Baton.Flow.Tests.Scheduling;

public class RetryEngineTests
{
    private static readonly StepId Architect = new("architect");
    private static readonly ExecutionId ExecutionId = new("exec-1");
    private static readonly IReadOnlyDictionary<StepId, ExecutionId> NoUpstream = new Dictionary<StepId, ExecutionId>();

    private static StepState Failed(int consecutiveFailureCount, FailureClassification? classification = null) =>
        new(Architect, StepStatus.Failed, ExecutionId, NoUpstream, consecutiveFailureCount, classification);

    [Fact]
    public void A_retryable_failure_under_budget_may_retry()
    {
        var mayRetry = RetryEngine.MayRetry(Failed(consecutiveFailureCount: 1), new RetryPolicy(MaxAttempts: 3));

        Assert.True(mayRetry);
    }

    [Fact]
    public void An_absent_classification_defaults_to_retryable()
    {
        var mayRetry = RetryEngine.MayRetry(
            Failed(consecutiveFailureCount: 0, classification: null),
            new RetryPolicy(MaxAttempts: 1));

        Assert.True(mayRetry);
    }

    [Fact]
    public void A_Permanent_classification_short_circuits_retry_regardless_of_remaining_budget()
    {
        var mayRetry = RetryEngine.MayRetry(
            Failed(consecutiveFailureCount: 0, FailureClassification.Permanent),
            new RetryPolicy(MaxAttempts: 10));

        Assert.False(mayRetry);
    }

    [Fact]
    public void An_exhausted_budget_may_not_retry()
    {
        var mayRetry = RetryEngine.MayRetry(Failed(consecutiveFailureCount: 3), new RetryPolicy(MaxAttempts: 3));

        Assert.False(mayRetry);
    }

    [Fact]
    public void A_budget_of_one_more_than_the_current_failure_count_may_still_retry()
    {
        var mayRetry = RetryEngine.MayRetry(Failed(consecutiveFailureCount: 2), new RetryPolicy(MaxAttempts: 3));

        Assert.True(mayRetry);
    }

    [Fact]
    public void An_ExhaustedUntil_classification_never_spends_retry_budget()
    {
        // 0026: "an ExhaustedUntil outcome consumes no retry budget, because retrying is not what
        // is wrong." The polarity partner is An_exhausted_budget_may_not_retry above -- same
        // count, same policy, one classification apart.
        var mayRetry = RetryEngine.MayRetry(
            Failed(consecutiveFailureCount: 3, FailureClassification.ExhaustedUntil),
            new RetryPolicy(MaxAttempts: 3));

        Assert.True(mayRetry);
    }

    [Theory]
    [InlineData(StepStatus.Succeeded)]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Running)]
    [InlineData(StepStatus.Paused)]
    [InlineData(StepStatus.Cancelled)]
    public void A_step_whose_latest_attempt_is_not_Failed_may_not_retry(StepStatus status)
    {
        var stepState = new StepState(Architect, status, ExecutionId, NoUpstream);

        var mayRetry = RetryEngine.MayRetry(stepState, new RetryPolicy(MaxAttempts: 5));

        Assert.False(mayRetry);
    }

    [Fact]
    public void A_failed_resume_shaped_attempt_never_retries_even_with_budget_remaining()
    {
        // Pins the RetryEngine's linked-from bypass (why: RetryEngine.ShouldRetry's doc, #1359 F4),
        // with MaxAttempts budget deliberately left over so only that bypass can explain a no-retry.
        var stepState = new StepState(
            Architect, StepStatus.Failed, ExecutionId, NoUpstream,
            ConsecutiveFailureCount: 1, LinkedFromExecutionId: new ExecutionId("resumed-from"));

        var mayRetry = RetryEngine.MayRetry(stepState, new RetryPolicy(MaxAttempts: 3));

        Assert.False(mayRetry);
    }

    [Fact]
    public void The_same_failure_shape_without_a_link_still_retries()
    {
        // The polarity partner of the test above -- one field apart (no LinkedFromExecutionId),
        // otherwise identical, to prove the refusal above is about the link, not incidentally about
        // ConsecutiveFailureCount or MaxAttempts.
        var stepState = new StepState(
            Architect, StepStatus.Failed, ExecutionId, NoUpstream,
            ConsecutiveFailureCount: 1, LinkedFromExecutionId: null);

        var mayRetry = RetryEngine.MayRetry(stepState, new RetryPolicy(MaxAttempts: 3));

        Assert.True(mayRetry);
    }
}
