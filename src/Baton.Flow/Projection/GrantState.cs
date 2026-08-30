using Baton.Flow.Domain;

namespace Baton.Flow.Projection;

/// <summary>
/// Projections of an active grant.
/// </summary>
public sealed record GrantState(
    GrantId GrantId,
    GrantId? BaseGrantId,
    WorkerId WorkerId,
    GrantLevel Level,
    GrantScope Scope,
    SpendBounds SpendBounds,
    string Grantor,
    DateTimeOffset Timestamp);
