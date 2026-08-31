using Baton.Domain;

namespace Baton.Tests.TestSupport;

/// <summary>
/// Assertion helpers for E2E tests that drive a real workflow to a terminal <see cref="StepStatus"/>.
/// A bare <c>Assert.Equal(StepStatus.Succeeded, step.Status)</c> only reports what the status
/// ended up being — a red run then requires spelunking room artifacts to learn why. This surfaces
/// <see cref="StepState.LatestFailureReason"/> (and the retry context sitting next to it) directly
/// in the assertion failure instead.
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
