namespace Baton.Domain;

/// <summary>
/// A vendor CLI's own self-reported per-execution usage (issue #1360), parsed by an adapter from its
/// vendor-specific stream-json terminal line — never fabricated by Flow. Every field is nullable and
/// independently absent: a vendor that reports turns but not tokens, or nothing at all, yields exactly
/// the fields it reported and no others. Wall-clock is deliberately not here — it is a Flow-derived
/// fact (execution start/exit timestamps already in the ledger), not something a vendor reports.
/// </summary>
/// <param name="ContextLevelTokens">
/// #1623 re-review N6: a monitor that tracks a running *level* rather than a single vendor-raw reading
/// (<see cref="Mutation.TokenBudgetMonitor"/>) reports its aggregate here — already the sum of
/// <paramref name="TokensIn"/> + <paramref name="CacheReadTokens"/> + <paramref name="CacheCreationTokens"/>
/// — rather than overloading <paramref name="TokensIn"/> with that aggregate. A consumer computing
/// <c>TokensIn + CacheReadTokens + CacheCreationTokens</c> on a <see cref="WorkerUsage"/> that carries
/// this field would otherwise double-count it. Null for every ordinary vendor-parsed reading
/// (<c>IWorkerUsageParser</c>'s two methods never set it).
/// </param>
public sealed record WorkerUsage(
    long? TokensIn = null,
    long? TokensOut = null,
    int? Turns = null,
    long? CacheReadTokens = null,
    long? CacheCreationTokens = null,
    long? ThinkingTokens = null,
    long? ContextLevelTokens = null);
