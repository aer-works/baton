using Baton.Flow.Domain;

namespace Baton.Flow.Projection;

public sealed record HeldWorkState(
    HeldWorkRef Ref,
    string Shape,
    TimeSpan Budget,
    string DeciderIdentity,
    HeldWorkStatus Status,
    string? EscalatedTo = null,
    HeldWorkCitation? Citation = null);
