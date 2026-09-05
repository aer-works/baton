namespace Baton.Accounting;

/// <summary>
/// Settle-time repository facts for one execution, gathered by the git/GitHub-aware CLI before
/// <see cref="CostLedgerStore.BuildEntries"/> constructs the git-agnostic accounting row (#1901 C1).
/// Every member is optional: an unavailable lookup stays absent on the row rather than being guessed;
/// <c>DeliverySource</c> discloses whether live workspace HEAD or the dispatch-stamped fallback supplied it.
/// </summary>
public sealed record CostLedgerExecutionMetadata(
    string? Issue = null,
    string? PullRequest = null,
    string? DeliverySource = null,
    int? FilesChanged = null,
    int? Additions = null,
    int? Deletions = null,
    int? TestFilesChanged = null);
