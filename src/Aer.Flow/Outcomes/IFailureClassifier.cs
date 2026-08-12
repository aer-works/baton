using Aer.Flow.Domain;

namespace Aer.Flow.Outcomes;

/// <summary>
/// An optional capability provided by worker adapters to interpret vendor-specific failure stderr
/// into a <see cref="FailureClassification"/> and reset instant.
/// </summary>
public interface IFailureClassifier
{
    /// <summary>
    /// Attempts to classify a worker failure from vendor-specific stderr / exit output.
    /// </summary>
    bool TryClassifyFailure(
        string? stderrTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        classification = null;
        retryNotBefore = null;
        return false;
    }

    /// <summary>
    /// Attempts to classify a worker failure from vendor-specific stderr and stdout tails / exit output.
    /// </summary>
    bool TryClassifyFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        return TryClassifyFailure(stderrTail, timeProvider, out classification, out retryNotBefore);
    }
}

