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
/// (<c>IWorkerUsageParser</c>'s two methods never set it). Kept, unchanged in meaning, as the
/// DISPLAYED context size (#1682) — no longer what a budget arrests on; see <paramref name="BilledTokens"/>.
/// </param>
/// <param name="BilledTokens">
/// #1682: the additive quantity a token budget actually arrests on — a running Σ, across every
/// incremental usage line, of <c>TokensIn + TokensOut [+ CacheCreationTokens]</c> for that line,
/// deliberately excluding <paramref name="ThinkingTokens"/> (spec/baton.md §3 has the measured case
/// for the exclusion). Null for an ordinary per-line vendor-parsed reading, same convention as
/// <paramref name="ContextLevelTokens"/>; only <c>Mutation.TokenBudgetMonitor</c>'s own snapshot sets it.
/// </param>
/// <param name="CacheReadTokens">
/// A vendor-parsed reading (<c>ClaudeUsageParser</c>/<c>AgyUsageParser</c>): the raw scalar off that
/// one line/terminal report, same as every other field on an ordinary reading. On
/// <see cref="Mutation.TokenBudgetMonitor"/>'s own snapshot this instead carries a running Σ across
/// every incremental line (#1682) — display-only, never itself compared to a budget — the same
/// per-context duality <paramref name="ContextLevelTokens"/> documents for the input side.
/// </param>
/// <param name="MessageId">
/// #1686 (review F6): claude's <c>message.id</c> off an incremental <c>"type":"assistant"</c> line —
/// measured against real captures to repeat across several consecutive lines with the SAME
/// <c>message.usage</c> object (a single API response split across content-block chunks), which would
/// double-count <paramref name="BilledTokens"/> if summed per line rather than per message. Null on
/// every reading agy produces (that vendor's shape has no analogous id) and on the terminal-line
/// reading, which is never summed. <see cref="Mutation.TokenBudgetMonitor"/> is the sole consumer —
/// it dedupes its own running Σ by this field rather than exposing it as a general-purpose identity.
/// </param>
/// <param name="BilledIsFloor">
/// #1706: <see langword="true"/> when the reading this belongs to is a LOWER BOUND on the execution's
/// real billed tokens rather than a measurement of it — some billed component the vendor charges for is
/// not present anywhere in the live stream, so no accumulation over that stream can recover it.
/// Set by <c>ClaudeUsageParser.TryParseIncrementalUsage</c> on every mid-stream reading it produces
/// (that method's own doc has the measurement) and carried forward, sticky, onto
/// <see cref="Mutation.TokenBudgetMonitor"/>'s snapshot: once ANY line on a stream was a floor, the
/// running Σ is a floor. False on agy's incremental reading and on both vendors' terminal reading,
/// which are measurements. Never inverts a comparison — a floor crossing a budget is still a real
/// crossing; what it cannot do is prove a budget was NOT crossed, which is exactly what the arrest
/// text and the glass now say rather than leaving to inference.
/// </param>
public sealed record WorkerUsage(
    long? TokensIn = null,
    long? TokensOut = null,
    int? Turns = null,
    long? CacheReadTokens = null,
    long? CacheCreationTokens = null,
    long? ThinkingTokens = null,
    long? ContextLevelTokens = null,
    long? BilledTokens = null,
    string? MessageId = null,
    bool BilledIsFloor = false);
