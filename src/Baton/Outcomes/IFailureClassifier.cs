using Baton.Domain;

namespace Baton.Outcomes;

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

    /// <summary>
    /// The same classification, asked on the SATISFIED exit-0 path (#1622 (a)'s veto,
    /// <c>Outcomes.OutcomeClassifier</c>) rather than on a failing dispatch. Separate because the
    /// admissible evidence differs: on a failing dispatch the tails are diagnostics, but on a
    /// satisfied run the stdout tail is the worker's own answer text, so an adapter whose matcher is
    /// prose (<c>Vendors.AgyWorkerAdapter</c>'s quota sentence) would let a worker veto its own
    /// successful run by writing ABOUT a quota refusal (#1720 review F1). An adapter overrides this
    /// to require a vendor-CONTROLLED signal on that channel; the default is the failing-dispatch
    /// classification, which is correct for an adapter whose stdout matcher is already a typed field.
    /// This is safe only because <c>stdoutTail</c> is a stream-json tail: a text-mode binding
    /// (<c>WorkerBindingConfigEntry.StreamJson</c> false — reachable from a hand-authored
    /// <c>bindings.json</c> under <c>baton run</c>, never from <c>dispatch</c>/<c>redispatch</c>, which
    /// force it true) puts the worker's raw answer prose on this channel instead, and a verbatim
    /// vendor-envelope quoted in that prose would then parse as a genuine top-level JSON object
    /// (#1720 review Finding A). No such binding is threaded through today; this is a documented
    /// constraint on the channel, not a guard.
    /// </summary>
    bool TryClassifySatisfiedRunFailure(
        string? stderrTail,
        string? stdoutTail,
        TimeProvider timeProvider,
        out FailureClassification? classification,
        out DateTimeOffset? retryNotBefore)
    {
        return TryClassifyFailure(stderrTail, stdoutTail, timeProvider, out classification, out retryNotBefore);
    }
}

