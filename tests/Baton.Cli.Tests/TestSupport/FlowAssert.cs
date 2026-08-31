using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// Assertion helpers for E2E tests. Duplicated per test assembly (no project reference between
/// them) rather than shared — see <c>Baton.Tests.TestSupport.FlowAssert</c> (in
/// <c>tests/Baton.Tests/TestSupport/FlowAssert.cs</c>) for the rationale and full doc comment.
/// </summary>
internal static class FlowAssert
{
    /// <summary>Asserts <paramref name="step"/> reached <see cref="StepStatus.Succeeded"/>.</summary>
    public static void Succeeded(StepState step)
    {
        if (step.Status == StepStatus.Succeeded)
        {
            return;
        }

        Assert.Fail(FailureMessage(step));
    }

    internal static string FailureMessage(StepState step) =>
        $"Expected step '{step.StepId}' to be Succeeded, but was {step.Status}. "
        + $"Latest failure reason: {step.LatestFailureReason ?? "no failure reason recorded"} "
        + $"(classification: {step.LatestFailureClassification?.ToString() ?? "none"}, "
        + $"consecutive failures: {step.ConsecutiveFailureCount})";
}
