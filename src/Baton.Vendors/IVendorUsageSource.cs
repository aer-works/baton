namespace Baton.Vendors;

/// <summary>
/// One vendor's own reported usage window — e.g. claude's "Current session" or agy's "Gemini Models
/// · Weekly Limit". <see cref="PercentUsed"/>/<see cref="ResetsAt"/> are null whenever the vendor's
/// own line does not carry that half (decision: "unparsed → unknown, never a number", issue #1391) —
/// never a guessed or zero-filled value. <see cref="RawLine"/> is the vendor's own line verbatim, kept
/// so a reader can show the vendor's own reset text even when <see cref="ResetsAt"/> failed to parse
/// into a real instant.
/// </summary>
public sealed record VendorUsageWindow(
    string Name,
    int? PercentUsed,
    DateTimeOffset? ResetsAt,
    string RawLine);

/// <summary>
/// One harvest of a single vendor's headless <c>/usage</c> report (issue #1391, reporting slice only
/// — spec/baton.md §6). <see cref="Caveat"/> is the vendor's own machine-local disclaimer, verbatim,
/// when the harvested output carried one; never fabricated. <see cref="Windows"/> is empty (never
/// null) rather than the whole snapshot being null when a harvest ran but nothing recognizable
/// parsed — see each <see cref="IVendorUsageSource"/> implementation's own doc comment for exactly
/// what shape it recognizes.
/// </summary>
public sealed record VendorUsageSnapshot(
    string Vendor,
    DateTimeOffset HarvestedAt,
    string? Caveat,
    IReadOnlyList<VendorUsageWindow> Windows);

/// <summary>
/// A vendor CLI's own headless usage report, read directly from that CLI — never a
/// <c>QuotaLedgerStore</c>-derived estimate, never an operator-declared ceiling (issue #1391's
/// settled source-of-truth ruling). One implementation per adapter; <see cref="Vendor"/> matches the
/// adapter tag the rest of the codebase already uses (<c>ClaudeWorkerAdapter.DeniedToolsVendorTag</c>
/// / <c>AgyWorkerAdapter</c>'s own tag).
/// </summary>
public interface IVendorUsageSource
{
    /// <summary>The adapter tag this source harvests, e.g. <c>"claude"</c> or <c>"agy"</c>.</summary>
    string Vendor { get; }

    /// <summary>
    /// Runs the vendor's own headless usage command once and parses its output. Returns null when the
    /// CLI could not be spawned, exited non-zero, or exited zero having written nothing at all —
    /// never a snapshot with fabricated content, and a null tells the harvester to leave the last
    /// persisted snapshot alone rather than blank it (<see cref="VendorUsageCommandRun"/> is where
    /// all three cases are decided, and its doc comment has the #1869 defect they close). Output that
    /// was written but is unrecognizable still returns a snapshot, with
    /// <see cref="VendorUsageSnapshot.Windows"/> empty, so a caller can tell "harvested, nothing
    /// parsed" apart from "did not harvest at all".
    /// </summary>
    Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken);
}
