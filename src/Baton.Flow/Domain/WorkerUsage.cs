namespace Baton.Flow.Domain;

/// <summary>
/// A vendor CLI's own self-reported per-execution usage (issue #1360), parsed by an adapter from its
/// vendor-specific stream-json terminal line — never fabricated by Flow. Every field is nullable and
/// independently absent: a vendor that reports turns but not tokens, or nothing at all, yields exactly
/// the fields it reported and no others. Wall-clock is deliberately not here — it is a Flow-derived
/// fact (execution start/exit timestamps already in the ledger), not something a vendor reports.
/// </summary>
public sealed record WorkerUsage(long? TokensIn = null, long? TokensOut = null, int? Turns = null);
