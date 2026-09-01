using System.Text.Json;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// Built-in usage parsers for vendor CLI streaming logs (issue #1360).
/// </summary>
public static class StandardWorkerUsageParsers
{
    public static IReadOnlyDictionary<string, IWorkerUsageParser> Default { get; } =
        new Dictionary<string, IWorkerUsageParser>(StringComparer.Ordinal)
        {
            ["claude"] = new ClaudeUsageParser(),
            ["agy"] = new AgyUsageParser(),
        };
}

/// <summary>
/// Parses claude's <c>stream-json</c> terminal <c>"type":"result"</c> line (issue #1360, extended by
/// #1569). The sole implementation for this vendor (#1599) -- <c>ClaudeWorkerAdapter.TryParseFinalUsage</c>
/// delegates here rather than re-implementing the same read, closing the drift #1590's fix left
/// behind: an all-null result (no tokens, no turns, no cache/thinking figures) now returns
/// <see langword="false"/> here too, matching the adapter's own guard, because a usage record with
/// nothing in it claims nothing.
/// <c>usage.input_tokens</c>/<c>output_tokens</c>/<c>cache_creation_input_tokens</c>/
/// <c>cache_read_input_tokens</c>, the nested <c>usage.output_tokens_details.thinking_tokens</c>, and
/// top-level <c>num_turns</c> are each read independently: a line reporting some and not others yields
/// exactly the fields it reported, never a fabricated zero (docs/vendor-capabilities.md's "Usage,
/// cost and quota" section is the register this reads against). <c>total_cost_usd</c> is real on this
/// vendor but outside #1569's additive shape, so it is read by nothing here.
/// <para>
/// <b>Scope, measured (docs/vendor-doc-audit.md, #479): this is a top-level figure, not a whole-tree
/// one.</b> <c>usage.output_tokens</c> excludes tokens spent by any subagent the dispatched worker
/// itself fans out to -- confirmed at a 22% shortfall against the same result's <c>modelUsage</c>
/// object on a single subagent, growing with the tree. AER caps a worker's own subagent fan-out at
/// depth 1 (<c>ClaudeWorkerAdapter.MaxSubagentSpawnDepthVariable</c>) rather than zero, so this
/// undercount is a real, reachable case here, not a hypothetical. <c>modelUsage</c> is left unread:
/// summing it correctly needs a per-model breakdown this shape's scalars cannot carry without
/// inventing a field neither #1360 nor #1569 asked for. Per <c>spec/baton.md</c> §7, none of this
/// shape is the reset-time source of truth -- it is attribution, and the fleet-level <c>/usage</c>
/// poll is what that section rules authoritative.
/// </para>
/// </summary>
public sealed class ClaudeUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() != "result")
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            long? cacheReadTokens = null;
            long? cacheCreationTokens = null;
            long? thinkingTokens = null;
            if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }

                if (usageProp.TryGetProperty("cache_read_input_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue))
                {
                    cacheReadTokens = cacheReadValue;
                }

                if (usageProp.TryGetProperty("cache_creation_input_tokens", out var cacheCreationProp) && cacheCreationProp.TryGetInt64(out var cacheCreationValue))
                {
                    cacheCreationTokens = cacheCreationValue;
                }

                if (usageProp.TryGetProperty("output_tokens_details", out var outputDetailsProp)
                    && outputDetailsProp.ValueKind == JsonValueKind.Object
                    && outputDetailsProp.TryGetProperty("thinking_tokens", out var thinkingProp)
                    && thinkingProp.TryGetInt64(out var thinkingValue))
                {
                    thinkingTokens = thinkingValue;
                }
            }

            int? turns = null;
            if (root.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnCount))
            {
                turns = turnCount;
            }

            if (tokensIn is null && tokensOut is null && turns is null
                && cacheReadTokens is null && cacheCreationTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns, cacheReadTokens, cacheCreationTokens, thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Parses agy's <c>stream-json</c> terminal <c>"event":"result"</c> line (issue #1360, extended by
/// #1569). The sole implementation for this vendor (#1599) -- <c>AgyWorkerAdapter.TryParseFinalUsage</c>
/// delegates here rather than re-implementing the same read. agy's <c>result.usage</c> shape is
/// inconsistent across observed captures (#1088, docs/vendor-capabilities.md): sometimes a full
/// breakdown (<c>input_tokens</c>/<c>output_tokens</c>/<c>thinking_tokens</c>/<c>cache_read_tokens</c>/
/// <c>total_tokens</c>), sometimes only <c>total_tokens</c>. Only <c>input_tokens</c>/
/// <c>output_tokens</c> map to this shape's <c>tokensIn</c>/<c>tokensOut</c> -- a lone
/// <c>total_tokens</c> is a real number but not a direction, and splitting it would fabricate a
/// breakdown agy never reported. <c>thinking_tokens</c>/<c>cache_read_tokens</c> read the same way,
/// independently of each other and of the input/output split. agy has never been observed reporting a
/// cache-creation figure (docs/vendor-capabilities.md), so this parser has no field to bind
/// <see cref="WorkerUsage.CacheCreationTokens"/> to and leaves it null rather than inventing one.
/// Turns come from <c>result.num_turns</c>, read independently of the usage object. This shape is
/// attribution, never the reset-time source of truth -- see <see cref="ClaudeUsageParser"/>'s own doc
/// comment for the <c>spec/baton.md</c> §7 ruling this rests on.
/// </summary>
public sealed class AgyUsageParser : IWorkerUsageParser
{
    public bool TryParseFinalUsage(string rawLine, out WorkerUsage? usage)
    {
        usage = null;
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventProp) || eventProp.GetString() != "result"
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            long? tokensIn = null;
            long? tokensOut = null;
            long? cacheReadTokens = null;
            long? thinkingTokens = null;
            if (result.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
            {
                if (usageProp.TryGetProperty("input_tokens", out var inProp) && inProp.TryGetInt64(out var inTokens))
                {
                    tokensIn = inTokens;
                }

                if (usageProp.TryGetProperty("output_tokens", out var outProp) && outProp.TryGetInt64(out var outTokens))
                {
                    tokensOut = outTokens;
                }

                if (usageProp.TryGetProperty("cache_read_tokens", out var cacheReadProp) && cacheReadProp.TryGetInt64(out var cacheReadValue))
                {
                    cacheReadTokens = cacheReadValue;
                }

                if (usageProp.TryGetProperty("thinking_tokens", out var thinkingProp) && thinkingProp.TryGetInt64(out var thinkingValue))
                {
                    thinkingTokens = thinkingValue;
                }
            }

            int? turns = result.TryGetProperty("num_turns", out var turnsProp) && turnsProp.TryGetInt32(out var turnsValue)
                ? turnsValue
                : null;

            if (tokensIn is null && tokensOut is null && turns is null && cacheReadTokens is null && thinkingTokens is null)
            {
                return false;
            }

            usage = new WorkerUsage(tokensIn, tokensOut, turns, cacheReadTokens, ThinkingTokens: thinkingTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
